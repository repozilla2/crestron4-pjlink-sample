# Crestron 4-Series PJLink programming sample

**Keith Gariepy**

A SIMPL# Pro C# sample for PJLink Class 1 display power control. It demonstrates protocol framing, authentication, confirmed power feedback, and serialized lifecycle handling. An operator uses console commands to connect, query, and request power changes.

## Validation

| Component or environment | Recorded result |
| --- | --- |
| Primary protocol core, `netstandard2.0` | 18 desktop test groups passed |
| Primary adapter, `net6.0`, SDK 2.22.40 | Release build and CPZ packaging passed, plus 13 desktop adapter groups using SDK doubles |
| RMC3-compatible port | 20 hardware acceptance checks passed against the virtual display on November 7, 2026 |
| RMC3 port in the desktop TCP harness | 11 loopback scenarios passed |
| Python virtual display | 36 simulator tests passed |
| 4-Series processor or VC-4 execution | Pending |
| Physical PJLink display | Pending |

The validation records identify each run's environment, tested sources, and observations. [Build and test record](VALIDATION.txt) · [RMC3 hardware record](docs/HARDWARE-VALIDATION.md)

## What to read

| File | Look at |
| --- | --- |
| [PjLinkSession.cs](src/DisplayControl.Core/PjLinkSession.cs) | Framing, greeting/MD5 auth, one-outstanding-command rule, timeouts, fault handling |
| [ControlSystem.cs](src/CrestronSample/ControlSystem.cs) | Lifecycle, TCP callbacks, serialized state, stale-socket rejection, deferred disposal |
| [Protocol tests](tests/DisplayControl.Tests/Program.cs) | Fragmentation, trailing data, idle fragments, timeout boundaries, invalid frames |
| [Adapter tests](tests/CrestronAdapter.Tests/Program.cs) | Pause/resume/stop, callback ordering, runtime configuration, closure diagnostics |
| [SampleConfiguration.cs](src/CrestronSample/SampleConfiguration.cs) | Runtime IPv4 selection, bounded file reads, password loading |

## Design decisions

- `PjLinkSession` receives monotonic time from its caller and raises `SendRequested`. Tests drive exact timeout boundaries deterministically.
- One command may be pending. Additional requests receive a clear rejection.
- Bytes are buffered to CR, with CRLF tolerated. The whole received chunk is validated before completion callbacks run. Trailing and idle data invalidate the session.
- `POWR=OK` records command acceptance. A query updates confirmed power feedback, which resets to `Unknown` on disconnect.
- Timeouts and protocol faults close the socket. The adapter detaches the old `TCPClient` before disposal and ignores its late callbacks. The operator explicitly reconnects.
- Socket, timer, and console callbacks share a lock. Socket disposal waits until the outermost serialized call releases it.
- Diagnostics use controlled messages, exception type names, and protocol results. Credentials reside in a private runtime file.

The implemented protocol is the power subset of PJLink Class 1 v1.04 with `PJLINK 0` and legacy `PJLINK 1` MD5 authentication. Security-enhanced v2.10 authentication remains future work. The test password fixture is the public example from the specification. [JBMIA Class 1 specification](https://pjlink.jbmia.or.jp/english/data/5-1_PJLink_eng_20131210.pdf) · [JBMIA v2.10 specification](https://pjlink.jbmia.or.jp/english/data_cl2/PJLink_5-1.pdf)

The sample uses TCP port `4352` and a **30-second timeout** for connection/greeting acquisition and each command response.

## Build and test

Use the .NET 10 SDK selected by `global.json`, version `10.0.403` or a later stable 10.0 release, with NuGet access. Building the processor project requires authorization under Crestron's SDK terms. SDK assemblies restore from NuGet. [Crestron SDK terms](https://www.crestron.com/Legal/software-license-agreement/Software-Development-Tools-License-Agreement)

```bash
dotnet build CrestronSample.sln -c Release -m:1
dotnet run --project tests/DisplayControl.Tests/DisplayControl.Tests.csproj -c Release --no-build
dotnet run --project tests/CrestronAdapter.Tests/CrestronAdapter.Tests.csproj -c Release --no-build
```

The build writes `src/CrestronSample/bin/Release/net6.0/CrestronSample.cpz`. Each test executable exits `0` on success. The `-m:1` option addresses the recorded Linux restore-graph failure in the default parallel solution build, whose cause remains unresolved. [Validation history](VALIDATION.txt)

The RMC3 port README provides its desktop, simulator, and loopback-TCP commands and the legacy processor-build prerequisites. [RMC3 port instructions](lab/Rmc3PjLink/README.txt)

## Run on a processor

Use a 4-Series processor with firmware that supports .NET 6 programs. CP4N release notes document that support beginning with `2.8003.00037`. Verify the selected model and installed firmware during commissioning. [CP4N firmware release notes](https://www.crestron.com/release_notes/cp4n_2.8003.00057_release_notes.pdf)

1. Load the CPZ.
2. Place the runtime configuration in the program directory returned by `Crestron.SimplSharp.CrestronIO.Directory.GetApplicationDirectory()`.

   - `pjlink-host.txt` is required and contains the display's dotted unicast IPv4 address. The program reads it on every explicit `connect`. Update this file to retarget the program.
   - `pjlink-password.txt` is optional and contains up to 32 ASCII letters or digits. The program reads it at startup. Transfer it over SFTP and keep it private.

   Each configuration read accepts at most 64 characters, including trailing CR/LF. An invalid or unreadable password file stops startup with a generic configuration diagnostic. Correct the file and restart the program.

3. Restart the program and use the console commands below.

| Command | Behavior |
| --- | --- |
| `pjlink connect` | Open TCP and wait for the greeting |
| `pjlink status` | Socket, ready, pending, and last confirmed power state |
| `pjlink query` | Query power |
| `pjlink on` / `pjlink off` | Request power on / off |
| `pjlink disconnect` | Close and clear feedback |

With `PJLINK 1`, a wrong password produces `PJLINK ERRA` on the first command. Displays may close idle TCP sessions, so check readiness and explicitly reconnect before further commands. Paused programs ignore `pjlink` commands. Finish each command sequence with `pjlink disconnect`.

For commissioning, connect and query a display in standby, request power on, and query through warm-up to `On`. Request power off and query through cooling to `Off`, then disconnect. Wait for each command's response before issuing the next. Transition durations and observable intermediate states depend on the display model, so use its documented timing when polling. Also exercise a wrong password, unreachable host, interrupted connection, pause/resume, and program stop/restart. Confirm that both runtime files survive a fresh program load. Record processor model, firmware, display model, and results.

## Repository layout

- `src/` — 4-Series sample (protocol core + adapter)
- `tests/` — desktop test harnesses for both
- `lab/Rmc3PjLink/` — Compact Framework 3.5 port that ran on RMC3 hardware, with its own tests
- `lab/VirtualPjLink/` — Python PJLink display simulator with fault injection
- `lab/PjLinkIntegration.Tests/` — loopback-TCP integration harness

## How this was built

Developed with assistance from OpenAI Codex, Claude Code, and ChatGPT under Keith Gariepy's direction.
