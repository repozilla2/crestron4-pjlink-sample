#!/usr/bin/env python3
"""Run the ported protocol against the virtual device over loopback TCP."""
import argparse
import datetime
import hashlib
import json
from pathlib import Path
import selectors
import subprocess
import sys
import time


def listening_event(process, timeout):
    deadline = time.monotonic() + timeout
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    try:
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError("Virtual display exited before announcing its listening port.")
            if not selector.select(max(0, deadline - time.monotonic())):
                break
            line = process.stdout.readline()
            if not line:
                break
            event = json.loads(line)
            if event.get("event") == "listening":
                return event
    finally:
        selector.close()
    raise TimeoutError("Virtual display did not announce its listening port.")


def run_case(dotnet, dll, simulator, directory, scenario):
    server_scenario = "normal" if scenario in ("authenticated", "wrong-password") else scenario
    authenticated = scenario in ("authenticated", "reject-auth", "wrong-password")
    log = directory / (scenario + ".jsonl")
    args = [sys.executable, str(simulator), "--bind", "127.0.0.1", "--port", "0",
            "--scenario", server_scenario, "--warmup-seconds", "0.15",
            "--cooldown-seconds", "0.15", "--idle-seconds", "5",
            "--fault-delay", "0.55", "--fragment-size", "1", "--fragment-delay", "0.002",
            "--log", str(log)]
    credential = directory / (scenario + ".password")
    if authenticated:
        credential.write_text("LabTestCredential123\n", encoding="ascii")
        credential.chmod(0o600)
        args.extend(["--password-file", str(credential)])
    started = time.monotonic()
    process = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    driver = None
    server_stdout = ""
    server_stderr = ""
    try:
        bound = listening_event(process, 5)
        command = [dotnet, str(dll), str(bound["port"]), scenario]
        if authenticated:
            command.append(str(credential))
        driver = subprocess.run(command, capture_output=True, text=True, timeout=12)
    finally:
        if process.poll() is None:
            process.terminate()
        try:
            server_stdout, server_stderr = process.communicate(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            server_stdout, server_stderr = process.communicate(timeout=5)
        credential.unlink(missing_ok=True)
    events = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
    transcript = server_stdout + server_stderr + (log.read_text() if log.exists() else "")
    if driver is not None:
        transcript += driver.stdout + driver.stderr
    credential_redacted = "LabTestCredential123" not in transcript and "WrongTestCredential" not in transcript
    clean_shutdown = process.returncode == 0 and bool(events) and events[-1].get("event") == "server_stopped"
    late_response_attempt_observed = None
    if scenario == "late-response":
        faults = [event for event in events if event.get("event") == "fault_injected"
                  and event.get("scenario") == "late-response"]
        late_response_attempt_observed = False
        if len(faults) == 1:
            fault = faults[0]
            due = fault["monotonic_seconds"] + fault["delay_seconds"]
            late_response_attempt_observed = any(
                event.get("connection_id") == fault["connection_id"]
                and event.get("event") in ("response_sent", "connection_error")
                and event["monotonic_seconds"] >= due - 0.005
                for event in events
            )
    passed = (driver is not None and driver.returncode == 0 and credential_redacted
              and clean_shutdown and late_response_attempt_observed is not False)
    return {
        "scenario": scenario,
        "status": "PASS" if passed else "FAIL",
        "duration_seconds": round(time.monotonic() - started, 3),
        "driver_exit_code": driver.returncode if driver else None,
        "driver_stdout": driver.stdout if driver else "",
        "driver_stderr": driver.stderr if driver else "",
        "simulator_exit_code": process.returncode,
        "simulator_stderr": server_stderr,
        "simulator_log": log.name,
        "simulator_event_count": len(events),
        "credential_values_absent_from_logs": credential_redacted,
        "simulator_clean_shutdown": clean_shutdown,
        "late_response_attempt_observed": late_response_attempt_observed,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--results", type=Path, required=True)
    args = parser.parse_args()
    directory = args.results.resolve()
    directory.mkdir(mode=0o700, parents=True, exist_ok=False)
    root = Path(__file__).resolve().parent
    project = root / "PjLinkIntegration.Tests.csproj"
    simulator = root.parent / "VirtualPjLink/virtual_pjlink.py"
    repository = root.parent.parent
    inputs = [root / "Program.cs", project, Path(__file__).resolve(),
              root.parent / "Rmc3PjLink/PjLinkSession.cs", simulator, repository / "global.json"]
    source_hashes = {str(path.relative_to(repository)): hashlib.sha256(path.read_bytes()).hexdigest()
                     for path in inputs}
    build = subprocess.run([args.dotnet, "build", str(project), "-c", "Release", "--nologo"],
                           capture_output=True, text=True, timeout=60)
    (directory / "build.log").write_text(build.stdout + build.stderr)
    if build.returncode:
        print(build.stdout + build.stderr, file=sys.stderr)
        return 1
    dll = root / "bin/Release/net10.0/PjLinkIntegration.Tests.dll"
    scenarios = ["normal", "authenticated", "fragmented", "device-error", "no-greeting",
                 "no-response", "late-response", "disconnect", "reject-auth", "wrong-password", "non-ascii"]
    results = []
    for scenario in scenarios:
        try:
            result = run_case(args.dotnet, dll, simulator, directory, scenario)
        except Exception as error:
            result = {"scenario": scenario, "status": "FAIL", "error_type": type(error).__name__,
                      "error": str(error)}
        results.append(result)
        print(result["status"] + " " + scenario, flush=True)
    inputs_unchanged = all(hashlib.sha256(path.read_bytes()).hexdigest() == source_hashes[str(path.relative_to(repository))]
                           for path in inputs)
    report = {
        "completed_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "status": "PASS" if all(item["status"] == "PASS" for item in results) and inputs_unchanged else "FAIL",
        "transport": "Real TCP over loopback",
        "application_source": "Ported PjLinkSession linked into a desktop transport harness",
        "processor_adapter_execution": "Desktop protocol harness. Hardware evidence is in docs/HARDWARE-VALIDATION.md.",
        "source_sha256": source_hashes,
        "source_inputs_unchanged": inputs_unchanged,
        "limitations": [
            "The desktop driver uses System.Net.Sockets and its own polling clock. Crestron socket and timer behavior requires processor execution.",
            "The desktop protocol build uses System.Security.Cryptography.MD5. The RMC3 build selects Crestron.SimplSharp.Cryptography.MD5.",
            "These cases use a 350-millisecond protocol timeout and 150-millisecond power transitions. The processor sample uses a 30-second response timeout.",
            "The late-response case verifies local closure of the old TCP client and continued correctness of a fresh session across the delayed-send window.",
        ],
        "results": results,
    }
    (directory / "integration-results.json").write_text(json.dumps(report, indent=2) + "\n")
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
