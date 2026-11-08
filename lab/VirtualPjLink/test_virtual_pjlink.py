"""Independent wire fixtures and local TCP tests for the virtual display."""

import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import selectors
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest

from virtual_pjlink import (
    Configuration, EventLog, FrameError, FrameReader, MAX_FRAME_BYTES,
    PowerModel, PowerState, VirtualPjLinkServer, argument_parser, read_password,
)


FIXTURE_PASSWORD = "JBMIAProjectorLink"
FIXTURE_CHALLENGE = "498e4a67"
FIXTURE_DIGEST = b"5d8409bc1c3fa39749434aa3a5c38682"


class ManualClock:
    def __init__(self):
        self.value = 0.0

    def __call__(self):
        return self.value


class Peer:
    def __init__(self, address):
        self.socket = socket.create_connection(address, timeout=1.5)
        self.socket.settimeout(1.5)

    def send(self, wire):
        self.socket.sendall(wire)

    def line(self):
        frame = bytearray()
        while True:
            value = self.socket.recv(1)
            if not value:
                raise EOFError("Peer closed before CR")
            if value == b"\r":
                return bytes(frame)
            if value == b"\n" and not frame:
                continue
            frame.extend(value)

    def close(self):
        self.socket.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class RunningServer:
    def __init__(self, clock=time.monotonic, challenge_factory=None, **options):
        self.stream = io.StringIO()
        self.log = EventLog(stream=self.stream)
        self.config = Configuration(port=0, **options)
        self.server = VirtualPjLinkServer(self.config, self.log, clock, challenge_factory)
        self.thread = threading.Thread(target=self.server.serve_forever, kwargs={"poll_interval": 0.01})

    def __enter__(self):
        self.thread.start()
        return self

    def peer(self):
        return Peer(self.server.server_address)

    def records(self):
        with self.log.lock:
            return [json.loads(line) for line in self.stream.getvalue().splitlines()]

    def __exit__(self, *args):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)
        if self.thread.is_alive():
            raise AssertionError("Server thread did not stop")


class PowerModelTests(unittest.TestCase):
    def test_all_four_states_and_exact_transition_boundaries(self):
        clock = ManualClock()
        power = PowerModel(Configuration(warmup_seconds=3, cooldown_seconds=2), clock)
        self.assertEqual(power.command(b"?"), "0")
        self.assertEqual(power.command(b"1"), "OK")
        self.assertEqual(power.command(b"?"), "3")
        clock.value = 2.999
        self.assertEqual(power.command(b"?"), "3")
        clock.value = 3
        self.assertEqual(power.command(b"?"), "1")
        self.assertEqual(power.command(b"0"), "OK")
        self.assertEqual(power.command(b"?"), "2")
        clock.value = 4.999
        self.assertEqual(power.command(b"?"), "2")
        clock.value = 5
        self.assertEqual(power.command(b"?"), "0")

    def test_idempotent_set_and_invalid_parameter(self):
        power = PowerModel(Configuration(warmup_seconds=0, cooldown_seconds=0))
        self.assertEqual(power.command(b"0"), "OK")
        self.assertEqual(power.command(b"1"), "OK")
        self.assertEqual(power.command(b"1"), "OK")
        self.assertEqual(power.command(b"2"), "ERR2")
        self.assertEqual(power.command(b""), "ERR2")
        self.assertEqual(power.command(b"?"), "1")

    def test_sets_during_transition_return_err3(self):
        power = PowerModel(Configuration(initial_power="warming", warmup_seconds=100))
        self.assertEqual(power.command(b"0"), "ERR3")
        self.assertEqual(power.command(b"1"), "ERR3")
        self.assertEqual(power.command(b"?"), "3")

    def test_initial_cooling_completes_with_clock(self):
        clock = ManualClock()
        power = PowerModel(Configuration(initial_power="cooling", cooldown_seconds=7), clock)
        self.assertEqual(power.snapshot(), PowerState.COOLING)
        clock.value = 7
        self.assertEqual(power.snapshot(), PowerState.OFF)


class FramingTests(unittest.TestCase):
    def test_every_fragment_boundary_and_crlf(self):
        wire = b"%1POWR ?\r\n%1POWR 1\r%1POWR ?\r"
        expected = [b"%1POWR ?", b"%1POWR 1", b"%1POWR ?"]
        for boundary in range(len(wire) + 1):
            reader = FrameReader()
            result = list(reader.feed(wire[:boundary])) + list(reader.feed(wire[boundary:]))
            self.assertEqual(result, expected)
        reader = FrameReader()
        frames = []
        for value in wire:
            frames.extend(reader.feed(bytes([value])))
        self.assertEqual(frames, expected)

    def test_oversize_and_invalid_ascii_are_bounded(self):
        self.assertEqual(list(FrameReader().feed(b"x" * MAX_FRAME_BYTES + b"\r")), [b"x" * MAX_FRAME_BYTES])
        with self.assertRaisesRegex(FrameError, "oversized_frame"):
            list(FrameReader().feed(b"x" * (MAX_FRAME_BYTES + 1)))
        for invalid in (b"\x80", b"\x00", b"\n", b"\t"):
            with self.assertRaisesRegex(FrameError, "invalid_ascii"):
                list(FrameReader().feed(invalid))


class WireTests(unittest.TestCase):
    def test_noauth_query_on_off_and_feedback(self):
        clock = ManualClock()
        with RunningServer(clock=clock, warmup_seconds=2, cooldown_seconds=2) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 0")
                expected = [(b"?", b"0"), (b"1", b"OK"), (b"?", b"3")]
                for command, response in expected:
                    peer.send(b"%1POWR " + command + b"\r")
                    self.assertEqual(peer.line(), b"%1POWR=" + response)
                clock.value = 2
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=1")
                peer.send(b"%1POWR 0\r")
                self.assertEqual(peer.line(), b"%1POWR=OK")
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=2")
                clock.value = 4
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=0")

    def test_official_auth_fixture_and_subsequent_plain_commands(self):
        with RunningServer(password=FIXTURE_PASSWORD, challenge_factory=lambda: FIXTURE_CHALLENGE,
                           warmup_seconds=0) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 1 498e4a67")
                peer.send(FIXTURE_DIGEST + b"%1POWR 1\r")
                self.assertEqual(peer.line(), b"%1POWR=OK")
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=1")
                peer.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=1")
                records = [r for r in lab.records() if r["event"] == "command_received"]
                self.assertEqual([r["digest_present"] for r in records], [True, False, True])

    def test_wrong_password_ignores_commands_and_times_out(self):
        with RunningServer(password=FIXTURE_PASSWORD, challenge_factory=lambda: FIXTURE_CHALLENGE,
                           idle_seconds=0.15) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 1 498e4a67")
                peer.send(b"0" * 32 + b"%1POWR 1\r")
                self.assertEqual(peer.line(), b"PJLINK ERRA")
                peer.send(FIXTURE_DIGEST + b"%1POWR 1\r")
                self.assertEqual(peer.socket.recv(1), b"")
                self.assertEqual(lab.server.power.snapshot(), PowerState.OFF)
                self.assertFalse(any(r["event"] == "command_received" for r in lab.records()))

    def test_new_connection_requires_new_digest(self):
        challenges = iter([FIXTURE_CHALLENGE, "10203040", "50607080"])
        with RunningServer(password=FIXTURE_PASSWORD, challenge_factory=lambda: next(challenges)) as lab:
            with lab.peer() as first:
                self.assertEqual(first.line(), b"PJLINK 1 498e4a67")
                first.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                self.assertEqual(first.line(), b"%1POWR=0")
            with lab.peer() as second:
                self.assertEqual(second.line(), b"PJLINK 1 10203040")
                second.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                self.assertEqual(second.line(), b"PJLINK ERRA")
            with lab.peer() as third:
                self.assertEqual(third.line(), b"PJLINK 1 50607080")
                digest = hashlib.md5(("50607080" + FIXTURE_PASSWORD).encode("ascii"), usedforsecurity=False).hexdigest().encode("ascii")
                third.send(digest + b"%1POWR ?\r")
                self.assertEqual(third.line(), b"%1POWR=0")

    def test_fragmented_auth_command_is_buffered_until_cr(self):
        with RunningServer(password=FIXTURE_PASSWORD, challenge_factory=lambda: FIXTURE_CHALLENGE) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 1 498e4a67")
                peer.socket.settimeout(0.04)
                peer.send(FIXTURE_DIGEST[:7])
                with self.assertRaises(socket.timeout):
                    peer.socket.recv(1)
                peer.send(FIXTURE_DIGEST[7:] + b"%1POWR ?")
                with self.assertRaises(socket.timeout):
                    peer.socket.recv(1)
                peer.send(b"\r")
                peer.socket.settimeout(1.5)
                self.assertEqual(peer.line(), b"%1POWR=0")

    def test_case_insensitivity_err1_err2_and_err3(self):
        with RunningServer(warmup_seconds=5) as lab:
            with lab.peer() as peer:
                peer.line()
                for command, expected in [
                    (b"%1powr ?\r", b"%1POWR=0"),
                    (b"%1INPT ?\r", b"%1INPT=ERR1"),
                    (b"%1POWR 9\r", b"%1POWR=ERR2"),
                    (b"%1POWR 1\r", b"%1POWR=OK"),
                    (b"%1POWR 0\r", b"%1POWR=ERR3"),
                ]:
                    peer.send(command)
                    self.assertEqual(peer.line(), expected)

    def test_crlf_commands_and_responses(self):
        with RunningServer(crlf=True, warmup_seconds=0) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 0")
                self.assertEqual(peer.socket.recv(1), b"\n")
                peer.send(b"%1POWR ?\r\n")
                self.assertEqual(peer.line(), b"%1POWR=0")
                self.assertEqual(peer.socket.recv(1), b"\n")
                peer.send(b"%1POWR 1\r\n")
                self.assertEqual(peer.line(), b"%1POWR=OK")
                self.assertEqual(peer.socket.recv(1), b"\n")

    def test_idle_timer_closes_connection(self):
        with RunningServer(idle_seconds=0.08) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 0")
                self.assertEqual(peer.socket.recv(1), b"")

    def test_partial_input_does_not_extend_idle_deadline(self):
        with RunningServer(idle_seconds=0.10) as lab:
            with lab.peer() as peer:
                peer.line()
                peer.send(b"%1POWR")
                self.assertEqual(peer.socket.recv(1), b"")

    def test_invalid_ascii_and_oversized_frames_close(self):
        for wire in (b"\x80", b"x" * (MAX_FRAME_BYTES + 1)):
            with RunningServer() as lab:
                with lab.peer() as peer:
                    peer.line()
                    peer.send(wire)
                    self.assertEqual(peer.socket.recv(1), b"")

    def test_malformed_commands_are_ignored(self):
        with RunningServer() as lab:
            with lab.peer() as peer:
                peer.line()
                peer.send(b"%1POWR=1\r")
                peer.socket.settimeout(0.04)
                with self.assertRaises(socket.timeout):
                    peer.socket.recv(1)
                peer.socket.settimeout(1.5)
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=0")

    def test_two_clients_observe_shared_power(self):
        with RunningServer(warmup_seconds=0) as lab:
            with lab.peer() as first, lab.peer() as second:
                self.assertEqual(first.line(), b"PJLINK 0")
                self.assertEqual(second.line(), b"PJLINK 0")
                first.send(b"%1POWR 1\r")
                self.assertEqual(first.line(), b"%1POWR=OK")
                second.send(b"%1POWR ?\r")
                self.assertEqual(second.line(), b"%1POWR=1")

    def test_client_allowlist_rejects_other_address(self):
        with RunningServer(allow_client="127.0.0.2") as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.socket.recv(1), b"")

    def test_connection_limit_is_bounded(self):
        with RunningServer() as lab, contextlib.ExitStack() as peers:
            for _ in range(4):
                peer = peers.enter_context(lab.peer())
                self.assertEqual(peer.line(), b"PJLINK 0")
            with lab.peer() as extra:
                self.assertEqual(extra.socket.recv(1), b"")


class ScenarioTests(unittest.TestCase):
    def test_greeting_fault_follows_accept_order(self):
        server = VirtualPjLinkServer(Configuration(port=0, scenario="no-greeting"), EventLog(stream=io.StringIO()))
        first, second = object(), object()
        try:
            self.assertTrue(server.verify_request(first, ("127.0.0.1", 50001)))
            self.assertTrue(server.verify_request(second, ("127.0.0.1", 50002)))
            self.assertEqual(server.connection_info(second), (2, False))
            self.assertEqual(server.connection_info(first), (1, True))
        finally:
            server.release_client(first)
            server.release_client(second)
            server.server_close()

    def test_fragmented_greeting_and_feedback(self):
        with RunningServer(scenario="fragmented", fragment_size=1, fragment_delay=0.001) as lab:
            with lab.peer() as peer:
                self.assertEqual(peer.line(), b"PJLINK 0")
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=0")
            records = [r for r in lab.records() if r["event"] == "greeting_sent"]
            self.assertEqual(records[0]["fragments"], 9)

    def test_silent_greeting_allows_prompt_reconnect(self):
        with RunningServer(scenario="no-greeting", fault_delay=1) as lab:
            with lab.peer() as first:
                first.socket.settimeout(0.04)
                with self.assertRaises(socket.timeout):
                    first.socket.recv(1)
                with lab.peer() as second:
                    self.assertEqual(second.line(), b"PJLINK 0")
                    second.send(b"%1POWR ?\r")
                    self.assertEqual(second.line(), b"%1POWR=0")

    def test_no_response_applies_command_and_allows_recovery(self):
        with RunningServer(scenario="no-response", fault_delay=1, warmup_seconds=0) as lab:
            with lab.peer() as first:
                first.line()
                first.send(b"%1POWR 1\r")
                first.socket.settimeout(0.04)
                with self.assertRaises(socket.timeout):
                    first.socket.recv(1)
                with lab.peer() as second:
                    self.assertEqual(second.line(), b"PJLINK 0")
                    second.send(b"%1POWR ?\r")
                    self.assertEqual(second.line(), b"%1POWR=1")

    def test_silent_hold_records_extra_bytes_without_payload(self):
        extra = FIXTURE_DIGEST + b"%1POWR 1\r" + FIXTURE_PASSWORD.encode("ascii")
        for send_extra in (False, True):
            with self.subTest(send_extra=send_extra):
                with RunningServer(scenario="no-response", fault_delay=2,
                                   password=FIXTURE_PASSWORD,
                                   challenge_factory=lambda: FIXTURE_CHALLENGE) as lab:
                    with lab.peer() as peer:
                        self.assertEqual(peer.line(), b"PJLINK 1 498e4a67")
                        peer.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                        deadline = time.monotonic() + 1
                        while not any(r["event"] == "fault_injected" for r in lab.records()):
                            self.assertLess(time.monotonic(), deadline, "Silent hold did not start")
                            time.sleep(0.005)
                        if send_extra:
                            peer.send(extra)
                        peer.socket.shutdown(socket.SHUT_WR)
                        self.assertEqual(peer.socket.recv(1), b"")
                    records = lab.records()
                    held = [r for r in records if r["event"] == "held_data_received"]
                    self.assertEqual(sum(r["bytes_received"] for r in held),
                                     len(extra) if send_extra else 0)
                    self.assertTrue(all(set(r) == {"event", "timestamp_utc", "monotonic_seconds",
                                                  "connection_id", "bytes_received"} for r in held))
                    self.assertEqual(sum(r["event"] == "command_received" for r in records), 1)
                    self.assertEqual(lab.server.power.snapshot(), PowerState.OFF)
                    self.assertTrue(any(r["event"] == "peer_closed" for r in records))
                    text = lab.stream.getvalue()
                    self.assertNotIn(FIXTURE_PASSWORD, text)
                    self.assertNotIn(FIXTURE_DIGEST.decode("ascii"), text)
                    self.assertNotIn(FIXTURE_CHALLENGE, text)

    def test_late_response_uses_same_connection(self):
        with RunningServer(scenario="late-response", fault_delay=0.15) as lab:
            with lab.peer() as peer:
                peer.line()
                began = time.monotonic()
                peer.send(b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=0")
                self.assertGreaterEqual(time.monotonic() - began, 0.14)

    def test_late_response_does_not_block_new_connection(self):
        with RunningServer(scenario="late-response", fault_delay=1) as lab:
            with lab.peer() as first:
                first.line()
                first.send(b"%1POWR ?\r")
                first.socket.settimeout(0.04)
                with self.assertRaises(socket.timeout):
                    first.socket.recv(1)
                with lab.peer() as second:
                    self.assertEqual(second.line(), b"PJLINK 0")
                    second.send(b"%1POWR ?\r")
                    self.assertEqual(second.line(), b"%1POWR=0")

    def test_device_error_is_one_shot_without_power_change(self):
        with RunningServer(scenario="device-error", warmup_seconds=0) as lab:
            with lab.peer() as peer:
                peer.line()
                for command, expected in [
                    (b"%1POWR 1\r", b"%1POWR=ERR4"),
                    (b"%1POWR ?\r", b"%1POWR=0"),
                    (b"%1POWR 1\r", b"%1POWR=OK"),
                    (b"%1POWR ?\r", b"%1POWR=1"),
                ]:
                    peer.send(command)
                    self.assertEqual(peer.line(), expected)

    def test_disconnect_precedes_command_execution(self):
        with RunningServer(scenario="disconnect", warmup_seconds=0) as lab:
            with lab.peer() as first:
                first.line()
                first.send(b"%1POWR 1\r")
                self.assertEqual(first.socket.recv(1), b"")
            with lab.peer() as second:
                second.line()
                second.send(b"%1POWR ?\r")
                self.assertEqual(second.line(), b"%1POWR=0")

    def test_auth_rejection_is_one_shot(self):
        with RunningServer(scenario="reject-auth", password=FIXTURE_PASSWORD,
                           challenge_factory=lambda: FIXTURE_CHALLENGE) as lab:
            with lab.peer() as first:
                first.line()
                first.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                self.assertEqual(first.line(), b"PJLINK ERRA")
            with lab.peer() as second:
                second.line()
                second.send(FIXTURE_DIGEST + b"%1POWR ?\r")
                self.assertEqual(second.line(), b"%1POWR=0")

    def test_non_ascii_response_and_reconnection(self):
        with RunningServer(scenario="non-ascii") as lab:
            with lab.peer() as first:
                first.line()
                first.send(b"%1POWR ?\r")
                self.assertEqual(first.line(), b"%1POWR=\x80")
            with lab.peer() as second:
                second.line()
                second.send(b"%1POWR ?\r")
                self.assertEqual(second.line(), b"%1POWR=0")


class ConfigurationAndLogTests(unittest.TestCase):
    def test_configuration_rejects_invalid_values(self):
        invalid = [
            {"bind": "example.com"}, {"port": -1}, {"port": 65536},
            {"allow_client": "bad"}, {"initial_power": "unknown"},
            {"warmup_seconds": -1}, {"fault_delay": 0},
            {"cooldown_seconds": float("nan")}, {"idle_seconds": float("inf")},
            {"fragment_size": 0}, {"password": ""}, {"password": "a" * 33},
            {"password": "space here"}, {"password": "\u00e9"},
            {"scenario": "reject-auth"}, {"scenario": "unknown"},
        ]
        for values in invalid:
            with self.subTest(values=values), self.assertRaises(ValueError):
                Configuration(**values).validate()

    def test_password_file_length_and_line_endings(self):
        parser = argument_parser()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "password.txt"
            path.write_bytes(b"A" * 32 + b"\r\n")
            self.assertEqual(read_password(parser.parse_args(["--password-file", str(path)])), "A" * 32)
            path.write_bytes(b"A" * 35)
            with self.assertRaises(ValueError):
                read_password(parser.parse_args(["--password-file", str(path)]))

    def test_existing_log_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.jsonl"
            path.write_text("prior evidence\n")
            with self.assertRaises(FileExistsError):
                EventLog(path, io.StringIO())
            self.assertEqual(path.read_text(), "prior evidence\n")

    def test_password_and_digest_never_enter_logs(self):
        password = "PrivateTestToken45"
        challenge = FIXTURE_CHALLENGE
        digest = hashlib.md5((challenge + password).encode("ascii"), usedforsecurity=False).hexdigest().encode("ascii")
        with RunningServer(password=password, challenge_factory=lambda: challenge) as lab:
            with lab.peer() as peer:
                peer.line()
                peer.send(digest + b"%1POWR ?\r")
                self.assertEqual(peer.line(), b"%1POWR=0")
                peer.send(b"%1POWR " + password.encode("ascii") + b"\r")
                self.assertEqual(peer.line(), b"%1POWR=ERR2")
                peer.send(b"%1INPT " + password.encode("ascii") + b"\r")
                self.assertEqual(peer.line(), b"%1INPT=ERR1")
            with lab.peer() as rejected:
                rejected.line()
                rejected.send(password.encode("ascii") + b"\r")
                self.assertEqual(rejected.line(), b"PJLINK ERRA")
            text = lab.stream.getvalue()
            self.assertNotIn(password, text)
            self.assertNotIn(digest.decode("ascii"), text)
            self.assertNotIn(challenge, text)
            self.assertNotIn(password, repr(lab.config))


class CliTests(unittest.TestCase):
    def test_cli_port_zero_log_and_clean_shutdown(self):
        script = Path(__file__).with_name("virtual_pjlink.py")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "evidence.jsonl"
            process = subprocess.Popen(
                [sys.executable, str(script), "--port", "0", "--warmup-seconds", "0", "--log", str(path)],
                stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
            )
            selector = selectors.DefaultSelector()
            selector.register(process.stdout, selectors.EVENT_READ)
            try:
                self.assertTrue(selector.select(5), "CLI startup event missing")
                startup = json.loads(process.stdout.readline())
                self.assertEqual(startup["event"], "listening")
                self.assertGreater(startup["port"], 0)
                with Peer((startup["bind"], startup["port"])) as peer:
                    self.assertEqual(peer.line(), b"PJLINK 0")
                    peer.send(b"%1POWR 1\r")
                    self.assertEqual(peer.line(), b"%1POWR=OK")
                    peer.send(b"%1POWR ?\r")
                    self.assertEqual(peer.line(), b"%1POWR=1")
                process.terminate()
                _, stderr = process.communicate(timeout=5)
                self.assertEqual(process.returncode, 0, stderr)
                records = [json.loads(line) for line in path.read_text().splitlines()]
                self.assertEqual(records[-1]["event"], "server_stopped")
                self.assertEqual(os.stat(path).st_mode & 0o777, 0o600)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait()
                process.stdout.close()
                process.stderr.close()
                selector.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
