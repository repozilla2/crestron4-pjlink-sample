#!/usr/bin/env python3
"""A controllable PJLink 1.04 power endpoint for the RMC3 lab."""

import argparse
import datetime
import getpass
import hashlib
import hmac
import ipaddress
import json
import math
import os
from pathlib import Path
import re
import secrets
import signal
import socket
import socketserver
import sys
import threading
import time
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Callable, Iterator, Optional


SCENARIOS = (
    "normal", "fragmented", "no-greeting", "no-response", "late-response",
    "device-error", "disconnect", "reject-auth", "non-ascii",
)
COMMAND_PATTERN = re.compile(rb"%1([A-Za-z0-9]{4}) ([\x20-\x7e]{0,128})")
MAX_FRAME_BYTES = 32 + 7 + 128
MAX_CLIENTS = 4


class PowerState(IntEnum):
    OFF = 0
    ON = 1
    COOLING = 2
    WARMING = 3


@dataclass(frozen=True)
class Configuration:
    bind: str = "127.0.0.1"
    port: int = 4352
    allow_client: Optional[str] = None
    scenario: str = "normal"
    password: Optional[str] = field(default=None, repr=False)
    initial_power: str = "off"
    warmup_seconds: float = 3.0
    cooldown_seconds: float = 3.0
    idle_seconds: float = 30.0
    fault_delay: float = 35.0
    fragment_size: int = 2
    fragment_delay: float = 0.03
    crlf: bool = False

    def validate(self) -> None:
        ipaddress.IPv4Address(self.bind)
        if self.allow_client is not None:
            ipaddress.IPv4Address(self.allow_client)
        if not 0 <= self.port <= 65535:
            raise ValueError("Port must be between 0 and 65535.")
        if self.scenario not in SCENARIOS:
            raise ValueError("Unknown scenario.")
        if self.initial_power not in ("off", "on", "cooling", "warming"):
            raise ValueError("Unknown initial power state.")
        for name in ("warmup_seconds", "cooldown_seconds", "fragment_delay"):
            value = getattr(self, name)
            if not math.isfinite(value) or not 0 <= value <= 3600:
                raise ValueError(name + " must be between 0 and 3600 seconds.")
        for name in ("idle_seconds", "fault_delay"):
            value = getattr(self, name)
            if not math.isfinite(value) or not 0 < value <= 3600:
                raise ValueError(name + " must be positive and at most 3600 seconds.")
        if not 1 <= self.fragment_size <= MAX_FRAME_BYTES + 2:
            raise ValueError("Fragment size is outside the frame bounds.")
        if self.password is not None:
            if re.fullmatch(r"[A-Za-z0-9]{1,32}", self.password) is None:
                raise ValueError("An authentication password needs 1 to 32 ASCII alphanumeric characters.")
        if self.scenario == "reject-auth" and self.password is None:
            raise ValueError("The reject-auth scenario requires authentication.")


class EventLog:
    """Emit structured fields chosen by the server, without incoming wire bytes."""

    def __init__(self, path: Optional[Path] = None, stream=None):
        self.stream = sys.stdout if stream is None else stream
        self.file = None
        self.lock = threading.Lock()
        if path is not None:
            descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            self.file = os.fdopen(descriptor, "w", encoding="utf-8")

    def emit(self, event: str, **fields) -> None:
        with self.lock:
            record = {
                "timestamp_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "monotonic_seconds": time.monotonic(),
                "event": event,
                **fields,
            }
            line = json.dumps(record, sort_keys=True) + "\n"
            if self.file is not None:
                self.file.write(line)
                self.file.flush()
            self.stream.write(line)
            self.stream.flush()

    def close(self) -> None:
        if self.file is not None:
            self.file.close()


class PowerModel:
    def __init__(self, config: Configuration, clock: Callable[[], float] = time.monotonic):
        self.config = config
        self.clock = clock
        self.lock = threading.Lock()
        self.state = PowerState[config.initial_power.upper()]
        self.deadline = None
        self._set_transition_deadline()

    def _set_transition_deadline(self) -> None:
        if self.state == PowerState.WARMING:
            self.deadline = self.clock() + self.config.warmup_seconds
        elif self.state == PowerState.COOLING:
            self.deadline = self.clock() + self.config.cooldown_seconds

    def _refresh(self) -> None:
        if self.deadline is not None and self.clock() >= self.deadline:
            self.state = PowerState.ON if self.state == PowerState.WARMING else PowerState.OFF
            self.deadline = None

    def snapshot(self) -> PowerState:
        with self.lock:
            self._refresh()
            return self.state

    def command(self, parameter: bytes) -> str:
        with self.lock:
            self._refresh()
            if parameter == b"?":
                return str(int(self.state))
            if parameter not in (b"0", b"1"):
                return "ERR2"
            if self.state in (PowerState.WARMING, PowerState.COOLING):
                return "ERR3"
            requested = PowerState.ON if parameter == b"1" else PowerState.OFF
            if requested != self.state:
                self.state = PowerState.WARMING if requested == PowerState.ON else PowerState.COOLING
                self._set_transition_deadline()
                self._refresh()
            return "OK"


class FrameError(Exception):
    pass


class FrameReader:
    def __init__(self):
        self.buffer = bytearray()
        self.after_cr = False

    def feed(self, chunk: bytes) -> Iterator[bytes]:
        for value in chunk:
            if self.after_cr and value == 10:
                self.after_cr = False
                continue
            self.after_cr = False
            if value == 13:
                frame = bytes(self.buffer)
                self.buffer.clear()
                self.after_cr = True
                yield frame
            elif value < 32 or value > 126:
                raise FrameError("invalid_ascii")
            elif len(self.buffer) >= MAX_FRAME_BYTES:
                raise FrameError("oversized_frame")
            else:
                self.buffer.append(value)


class VirtualPjLinkServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = False
    block_on_close = True

    def __init__(self, config: Configuration, log: EventLog,
                 clock: Callable[[], float] = time.monotonic,
                 challenge_factory: Optional[Callable[[], str]] = None):
        config.validate()
        self.config = config
        self.log = log
        self.power = PowerModel(config, clock)
        self.challenge_factory = challenge_factory or (lambda: secrets.token_hex(4))
        self.stop_event = threading.Event()
        self.state_lock = threading.Lock()
        self.clients = {}
        self.next_connection_id = 0
        self.fault_used = False
        self.slots = threading.BoundedSemaphore(MAX_CLIENTS)
        super().__init__((config.bind, config.port), DeviceConnection)

    def verify_request(self, request, client_address) -> bool:
        if self.config.allow_client is not None and client_address[0] != self.config.allow_client:
            self.log.emit("connection_rejected", reason="client_address", peer_ip=client_address[0])
            return False
        if not self.slots.acquire(blocking=False):
            self.log.emit("connection_rejected", reason="connection_limit", peer_ip=client_address[0])
            return False
        with self.state_lock:
            self.next_connection_id += 1
            suppress_greeting = self.config.scenario == "no-greeting" and not self.fault_used
            if suppress_greeting:
                self.fault_used = True
            self.clients[request] = (self.next_connection_id, suppress_greeting)
        return True

    def claim_fault(self, scenario: str) -> bool:
        with self.state_lock:
            if self.config.scenario != scenario or self.fault_used:
                return False
            self.fault_used = True
            return True

    def connection_info(self, request):
        with self.state_lock:
            return self.clients[request]

    def release_client(self, request) -> None:
        with self.state_lock:
            self.clients.pop(request)
        self.slots.release()

    def server_close(self) -> None:
        self.stop_event.set()
        with self.state_lock:
            clients = list(self.clients)
        for client in clients:
            try:
                client.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
        super().server_close()


class DeviceConnection(socketserver.BaseRequestHandler):
    def emit(self, event: str, **fields) -> None:
        self.server.log.emit(event, connection_id=self.identifier, **fields)

    def send_frame(self, body: bytes, category: str, **fields) -> bool:
        ending = b"\r\n" if self.server.config.crlf else b"\r"
        wire = body + ending
        fragmented = self.server.config.scenario == "fragmented"
        size = self.server.config.fragment_size if fragmented else len(wire)
        for offset in range(0, len(wire), size):
            if self.server.stop_event.is_set():
                return False
            self.request.sendall(wire[offset:offset + size])
            if fragmented and offset + size < len(wire):
                if self.server.stop_event.wait(self.server.config.fragment_delay):
                    return False
        self.emit(category, bytes_sent=len(wire), fragments=(len(wire) + size - 1) // size, **fields)
        return True

    def wait_for_peer_close(self, seconds: float) -> None:
        deadline = time.monotonic() + seconds
        while not self.server.stop_event.is_set():
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                self.emit("hold_expired")
                return
            self.request.settimeout(min(0.2, remaining))
            try:
                received = self.request.recv(4096)
                if not received:
                    self.emit("peer_closed")
                    return
                self.emit("held_data_received", bytes_received=len(received))
            except socket.timeout:
                continue

    def authenticate(self, frame: bytes) -> Optional[bytes]:
        self.digest_present = False
        if not self.authenticated:
            rejected = self.server.claim_fault("reject-auth")
            matches = hmac.compare_digest(frame[:32].lower(), self.expected_digest)
            if rejected or not matches:
                self.emit("authentication_rejected", forced=rejected)
                self.send_frame(b"PJLINK ERRA", "authentication_error_sent")
                self.wait_for_peer_close(self.server.config.idle_seconds)
                return None
            self.authenticated = True
            self.digest_present = True
            self.emit("authentication_accepted")
            return frame[32:]
        if self.expected_digest is not None and hmac.compare_digest(frame[:32].lower(), self.expected_digest):
            self.digest_present = True
            return frame[32:]
        return frame

    def handle_command(self, frame: bytes) -> bool:
        command = self.authenticate(frame)
        if command is None:
            return False
        match = COMMAND_PATTERN.fullmatch(command)
        if match is None:
            self.emit("command_ignored", reason="invalid_format")
            return True
        body, parameter = match.groups()
        body = body.upper()
        kind = "unsupported"
        if body == b"POWR":
            kind = {b"?": "query", b"1": "on", b"0": "off"}.get(parameter, "invalid_parameter")
        self.command_count += 1
        self.emit("command_received", command_number=self.command_count,
                  kind=kind, digest_present=self.digest_present)

        valid_power = body == b"POWR" and parameter in (b"?", b"0", b"1")
        if valid_power and self.server.claim_fault("disconnect"):
            self.emit("fault_injected", scenario="disconnect")
            return False
        if valid_power and self.server.claim_fault("device-error"):
            response = "ERR4"
            self.emit("fault_injected", scenario="device-error")
        else:
            response = self.server.power.command(parameter) if body == b"POWR" else "ERR1"
        self.emit("feedback_generated", command_number=self.command_count, kind=kind,
                  response=response, power_state=int(self.server.power.snapshot()))
        if valid_power and self.server.claim_fault("no-response"):
            self.emit("fault_injected", scenario="no-response")
            self.wait_for_peer_close(self.server.config.fault_delay)
            return False
        if valid_power and self.server.claim_fault("late-response"):
            self.emit("fault_injected", scenario="late-response", delay_seconds=self.server.config.fault_delay)
            if self.server.stop_event.wait(self.server.config.fault_delay):
                return False
        response_body = b"%1" + body + b"=" + response.encode("ascii")
        if valid_power and self.server.claim_fault("non-ascii"):
            self.emit("fault_injected", scenario="non-ascii")
            response_body = b"%1POWR=\x80"
            response = "invalid_ascii"
        sent = self.send_frame(response_body,
                               "response_sent", command_number=self.command_count, kind=kind,
                               response=response)
        if sent:
            self.deadline = time.monotonic() + self.server.config.idle_seconds
        return sent

    def handle(self) -> None:
        self.identifier, suppress_greeting = self.server.connection_info(self.request)
        self.command_count = 0
        self.authenticated = self.server.config.password is None
        self.expected_digest = None
        self.digest_present = False
        try:
            self.emit("connection_opened", peer_ip=self.client_address[0], peer_port=self.client_address[1])
            self.request.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            if suppress_greeting:
                self.emit("fault_injected", scenario="no-greeting")
                self.wait_for_peer_close(self.server.config.fault_delay)
                return
            greeting = b"PJLINK 0"
            if not self.authenticated:
                challenge = self.server.challenge_factory()
                if re.fullmatch(r"[0-9a-f]{8}", challenge) is None:
                    raise ValueError("Challenge factory returned an invalid challenge.")
                secret = (challenge + self.server.config.password).encode("ascii")
                self.expected_digest = hashlib.md5(secret, usedforsecurity=False).hexdigest().encode("ascii")
                greeting = b"PJLINK 1 " + challenge.encode("ascii")
            if not self.send_frame(greeting, "greeting_sent", authentication="none" if self.authenticated else "md5"):
                return
            self.deadline = time.monotonic() + self.server.config.idle_seconds
            reader = FrameReader()
            while not self.server.stop_event.is_set():
                remaining = self.deadline - time.monotonic()
                if remaining <= 0:
                    self.emit("idle_timeout")
                    return
                self.request.settimeout(min(0.2, remaining))
                try:
                    chunk = self.request.recv(4096)
                except socket.timeout:
                    continue
                if not chunk:
                    self.emit("peer_closed")
                    return
                for frame in reader.feed(chunk):
                    if not self.handle_command(frame):
                        return
        except FrameError as error:
            self.emit("frame_rejected", reason=str(error))
        except (OSError, ValueError) as error:
            self.emit("connection_error", error_type=type(error).__name__)
        finally:
            try:
                self.emit("connection_closed")
            finally:
                self.server.release_client(self.request)


def argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1", help="IPv4 address of the listening Mac interface")
    parser.add_argument("--port", type=int, default=4352, help="TCP port, or 0 for an ephemeral test port")
    parser.add_argument("--allow-client", help="Accept connections only from this IPv4 address")
    parser.add_argument("--scenario", choices=SCENARIOS, default="normal")
    parser.add_argument("--initial-power", choices=("off", "on", "cooling", "warming"), default="off")
    parser.add_argument("--warmup-seconds", type=float, default=3.0)
    parser.add_argument("--cooldown-seconds", type=float, default=3.0)
    parser.add_argument("--idle-seconds", type=float, default=30.0)
    parser.add_argument("--fault-delay", type=float, default=35.0, help="Silent hold or delayed-response duration")
    parser.add_argument("--fragment-size", type=int, default=2)
    parser.add_argument("--fragment-delay", type=float, default=0.03)
    parser.add_argument("--crlf", action="store_true", help="Append LF after each response CR for tolerance testing")
    authentication = parser.add_mutually_exclusive_group()
    authentication.add_argument("--ask-password", action="store_true", help="Read the PJLink password in this Terminal")
    authentication.add_argument("--password-file", type=Path, help="Read the PJLink password from a local private file")
    parser.add_argument("--log", type=Path, help="Create a new JSONL evidence file with private permissions")
    return parser


def read_password(arguments) -> Optional[str]:
    if arguments.ask_password:
        if not sys.stdin.isatty():
            raise ValueError("Password prompting requires an interactive Terminal.")
        return getpass.getpass("PJLink test password: ")
    if arguments.password_file is not None:
        with arguments.password_file.open("rb") as source:
            raw = source.read(35)
        if len(raw) > 34:
            raise ValueError("Password file exceeds the accepted length.")
        return raw.decode("ascii").rstrip("\r\n")
    return None


def main(argv=None) -> int:
    parser = argument_parser()
    arguments = parser.parse_args(argv)
    try:
        password = read_password(arguments)
        config = Configuration(**{name: getattr(arguments, name) for name in Configuration.__dataclass_fields__ if name != "password"},
                               password=password)
        config.validate()
    except (OSError, UnicodeError, ValueError) as error:
        parser.error("Invalid configuration or password input (" + type(error).__name__ + ").")

    log = None
    try:
        log = EventLog(arguments.log)
        with VirtualPjLinkServer(config, log) as server:
            def interrupt(signum, frame):
                raise KeyboardInterrupt

            signal.signal(signal.SIGTERM, interrupt)
            log.emit("listening", bind=server.server_address[0], port=server.server_address[1],
                     allow_client=config.allow_client, scenario=config.scenario,
                     authentication="none" if config.password is None else "md5",
                     initial_power=int(server.power.snapshot()), warmup_seconds=config.warmup_seconds,
                     cooldown_seconds=config.cooldown_seconds, idle_seconds=config.idle_seconds,
                     fault_delay=config.fault_delay, fragment_size=config.fragment_size,
                     fragment_delay=config.fragment_delay, crlf=config.crlf)
            try:
                server.serve_forever(poll_interval=0.1)
            except KeyboardInterrupt:
                pass
        log.emit("server_stopped")
        return 0
    except OSError as error:
        if log is not None:
            log.emit("server_error", error_type=type(error).__name__)
        else:
            print("Could not create the evidence log (" + type(error).__name__ + ").", file=sys.stderr)
        return 1
    finally:
        if log is not None:
            log.close()


if __name__ == "__main__":
    raise SystemExit(main())
