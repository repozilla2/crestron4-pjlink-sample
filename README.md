# Crestron 4-Series C# programming sample

## PJLink display power control

**Keith Gariepy · Crestron 4-Series programming sample**

This original, AI-assisted example demonstrates a small SIMPL# Pro program for a programmable 4-Series control system. An operator connects to a PJLink display, queries its power state, requests power on or off, and reads the resulting feedback. The source includes a reusable protocol component, the Crestron TCP adapter, and two executable regression-test harnesses.

A Compact Framework 3.5 port under `lab/Rmc3PjLink` passed 20 hardware checks on a physical RMC3 against the included Mac-hosted virtual display on November 7, 2026. ChatGPT independently reviewed the Mac CLI run's evidence, corroborated all 20 results, and verified 692 evidence checksums. The implementation, package identity, observations, and remaining coverage are recorded in the [RMC3 hardware validation](docs/HARDWARE-VALIDATION.md).

## Review order

| File | What to inspect |
| --- | --- |
| [4-Series adapter](src/CrestronSample/ControlSystem.cs) | Crestron lifecycle, TCP callbacks, console commands, serialization, cleanup, and safe diagnostics |
| [Protocol component](src/DisplayControl.Core/PjLinkSession.cs) | Framing, greeting and authentication, command state, confirmed feedback, and timeouts |
| [Protocol tests](tests/DisplayControl.Tests/Program.cs) | Deterministic examples and protocol regression cases |
| [Adapter tests](tests/CrestronAdapter.Tests/Program.cs) | Lifecycle, stale callbacks, inline completion, cleanup ownership, and credential handling using SDK doubles |
| [4-Series configuration](src/CrestronSample/SampleConfiguration.cs) | Demonstration endpoint, port, timeout, and runtime password loading |
| [Validation history](VALIDATION.txt) | Recorded 4-Series build, desktop tests, and archive inspection |
| [RMC3 port](lab/Rmc3PjLink/README.txt) | Compact Framework compatibility, runtime providers, and additional desktop tests |
| [Hardware validation](docs/HARDWARE-VALIDATION.md) | Exact tested RMC3 sources, 20 hardware results, and evidence qualifications |

## Implementations and validation

| Implementation | Runtime target | Completed validation |
| --- | --- | --- |
| Primary sample in `src/` | Protocol `netstandard2.0`, processor `net6.0` | macOS and Linux builds and CPZ packaging, 18 core groups and 10 adapter groups |
| RMC3-compatible port in `lab/Rmc3PjLink/` | Compact Framework 3.5 with legacy Crestron references | Vendor validation/signing/packaging, 18 core groups, 15 adapter groups, 5 event groups, and 20 hardware checks |
| Virtual display and TCP harness in `lab/` | Python standard library and desktop .NET 10 | 36 simulator tests and 11 loopback TCP scenarios |

The RMC3 port adapts syntax, event accessors, clock, file I/O, cryptography, ASCII conversion, and host configuration for the 3-Series runtime. Its production-source hashes tie the published files to the hardware-tested package. Execution of the primary `net6.0` program on a 4-Series processor is pending. [Port details](lab/Rmc3PjLink/README.txt) · [Hardware record](docs/HARDWARE-VALIDATION.md)

## Design decisions

- The protocol component receives a monotonic time value from its caller. Tests can exercise exact timeout boundaries immediately.
- One command may be outstanding on a connection. Requests made during a pending transaction receive a clear rejection.
- Received text is buffered until a carriage return arrives. The component validates the entire delivered chunk before raising completion callbacks. Data received while idle and extra response data invalidate the connection before they can complete a later command.
- A successful power-command acknowledgment records command acceptance. Confirmed power feedback changes through a successful status query and resets to `Unknown` when the connection closes.
- A timeout or protocol fault invalidates the session. The adapter detaches the old socket before disposal and ignores callbacks belonging to that old socket. Recovery begins with an explicit operator reconnection.
- Socket callbacks, timer callbacks, and console actions enter the same serialized state boundary. The protocol component remains independent of Crestron runtime services.
- Inline socket callbacks defer cleanup until the outermost serialized call releases its lock.
- Pausing closes the connection, clears feedback, and disposes the timer. Resuming starts the timer and requires an explicit new connection. Stopping also releases the program's registrations.
- The configured password is read from a private runtime file. Diagnostic messages omit outgoing protocol data and exception details that could contain credentials.

The protocol target is the power-control subset of PJLink Class 1 version 1.04, including `PJLINK 0` and legacy `PJLINK 1` authentication. Security-enhanced version 2.10 authentication would require additional implementation. The 30-second sample timeout covers connection/greeting acquisition and each command response. The password fixture in the tests is the public example from the specification. [JBMIA Class 1 specification](https://pjlink.jbmia.or.jp/english/data/5-1_PJLink_eng_20131210.pdf)

## Build and exercise on macOS

Install the .NET 10 SDK, version `10.0.403` or a later stable 10.0 release, for the Mac's architecture. The repository's `global.json` sets `10.0.403` as the minimum with `rollForward` set to `latestFeature` and prerelease SDKs disallowed. The build uses the newest installed stable 10.0 SDK at or above that version, including later patches and feature bands such as `10.0.5xx`. Lower 10.0 SDKs, other major or minor versions, and prerelease SDKs are rejected. Run `dotnet --version` from the repository root to see the selected SDK. Each project's target framework determines its runtime, while `global.json` selects the SDK. [Microsoft global.json reference](https://learn.microsoft.com/en-us/dotnet/core/tools/global-json)

The desktop test executables target `net10.0` and need the .NET 10 runtime, which the SDK includes. Microsoft's support policy lists .NET 10 (LTS) support through November 14, 2028 and .NET 8 support ending November 10, 2026. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy) Visual Studio Code is optional. Package restore needs access to NuGet. [Microsoft macOS installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/macos)

Building the processor project requires appropriate authorization to use Crestron's SDK under its applicable software-development terms. The repository contains original source and package references. SDK assemblies are restored through NuGet during the build. [Crestron software-development terms](https://www.crestron.com/Legal/software-license-agreement/Software-Development-Tools-License-Agreement)

From the repository root or extracted source directory, run the following commands.

```bash
dotnet build CrestronSample.sln -c Release -m:1
dotnet run --project tests/DisplayControl.Tests/DisplayControl.Tests.csproj -c Release --no-build
dotnet run --project tests/CrestronAdapter.Tests/CrestronAdapter.Tests.csproj -c Release --no-build
```

`-m:1` uses single-node MSBuild. The November 7 Linux validation required this option after the default solution build exited with code 1 during restore-graph evaluation and reported no warnings or errors. The cause remains unresolved. The successful build and all desktop results are recorded in [VALIDATION.txt](VALIDATION.txt).

Each test executable returns exit code `0` when every group passes and exit code `1` when any group fails. Both use small dependency-free harnesses with explicit assertions. The core targets `netstandard2.0`, both test executables target `net10.0`, and the processor program targets `net6.0`. Crestron SDK package `2.22.40` supplies `net47` and `net6.0` assemblies. The selected processor firmware must support that program target as described below.

The adapter tests compile the production adapter source against explicit SDK doubles. They exercise its control-flow decisions, including adverse callback ordering and timer-driven timeouts. An internal constructor lets the tests supply a manual clock; the processor uses the public constructor and a `Stopwatch`. Processor runtime behavior and device integration remain part of the lab exercise below.

The processor project pins `Crestron.SimplSharp.SDK.Program` to `2.22.40`. Crestron's package produces `src/CrestronSample/bin/Release/net6.0/CrestronSample.cpz` as part of a successful build. [Crestron SDK package](https://www.nuget.org/packages/Crestron.SimplSharp.SDK.Program/2.22.40)

The lab source has additional desktop checks and a loopback TCP exercise. Its README includes the commands and the legacy processor-build prerequisites. [RMC3 port checks](lab/Rmc3PjLink/README.txt) · [Virtual display](lab/VirtualPjLink/README.md)

## Processor exercise

Select a programmable 4-Series processor whose firmware supports full .NET 6 programs and the selected SDK. Crestron's CP4N release history documents .NET 6 full-program support beginning with firmware `2.8003.00037`. The processor model and installed firmware must be checked during commissioning. [CP4N firmware release notes](https://www.crestron.com/release_notes/cp4n_2.8003.00057_release_notes.pdf)

1. Set `DisplayHost` in `SampleConfiguration.cs` to the lab display's address. The supplied `192.0.2.10` value is a documentation placeholder. PJLink uses port `4352`.
2. Build the project with the configured lab endpoint and load the resulting CPZ through the established Crestron deployment procedure.
3. For a password-protected display, place its password in a private `pjlink-password.txt` file in the processor's program directory, as returned by `Crestron.SimplSharp.CrestronIO.Directory.GetApplicationDirectory()`, then restart the program. Transfer it through an authorized secure connection. Keep this file outside source control and shared archives. A missing password file permits the unauthenticated PJLink mode. During commissioning, confirm that the file survives a program reload; Crestron's documentation does not state whether loading a program replaces the program directory's contents.
4. Open the processor console and use the commands below.

| Command | Behavior |
| --- | --- |
| `pjlink connect` | Start a TCP connection and wait for the display greeting |
| `pjlink status` | Show the connection, readiness, pending request, and last confirmed power state |
| `pjlink query` | Ask the display for its power state |
| `pjlink on` | Request power on |
| `pjlink off` | Request power off |
| `pjlink disconnect` | Close the connection and invalidate cached power feedback |

The program waits for an explicit `connect` command after startup. `Ready` means the display's greeting was accepted. With `PJLINK 1`, the password is confirmed only when the first command receives a response other than `PJLINK ERRA`. While the program is paused, `pjlink` commands are ignored without a reply. Run `query` after each power command completes to observe `Off`, `Warming`, `On`, or `Cooling`. If the peer closes an idle connection, reconnect before the next query. The host issues no automatic power-command retries.

For a lab walkthrough, query a display in standby, request power on, and query through warm-up. Then request power off and observe cooling and standby. Exercise a wrong password, unavailable device, interrupted connection, pause/resume, and program stop/restart. Record the processor model, firmware, display model, and observed results.

## 4-Series verification status

| Check | Status |
| --- | --- |
| Clean build on Ubuntu 24.04 x64 with .NET SDK 10.0.403 and single-node MSBuild | Passed with 0 warnings and 0 errors on November 7, 2026, with 18/18 core and 10/10 adapter groups |
| Clean build on macOS 27.2 arm64 with .NET SDK 10.0.403 selected by `global.json` | Passed with 0 warnings and 0 errors at commit `c9fb31f` |
| Core regression tests on .NET 10 | 18 of 18 test groups passed, exit code 0 |
| Adapter regression tests using SDK doubles on .NET 10 | 10 of 10 test groups passed, exit code 0 |
| Crestron SDK compilation and CPZ packaging on macOS | Passed using the official, unmodified SDK build targets, with CPZ inspection and a recorded checksum |
| Earlier native .NET 10 run at commit `7cf31ae` | 18 of 18 and 9 of 9 passed (superseded, preserved in `VALIDATION.txt`) |
| Original `net8.0` tests on macOS with .NET SDK 10.0.403 | Passed 18/18 and 8/8 at commit `544b222` with an explicit `DOTNET_ROLL_FORWARD=Major` override (superseded) |
| Original Linux build and tests with .NET SDK 8.0.427 | Passed at commit `544b222` (preserved in `VALIDATION.txt`) |
| Primary `net6.0` program execution on a 4-Series processor | Pending |
| Real display power control and authentication | Pending |
| Network recovery on a 4-Series processor | Pending |
| Pause/resume and program stop/restart on a 4-Series processor | Pending |
| Password file persists across a program reload | Pending |

The core tests cover fragmentation, authentication, power states, acknowledgments, error responses, overlapping requests, timeout boundaries, late data, restart, invalid messages, and buffer limits. The adapter tests cover pause/resume, stop, stale callbacks, inline callbacks, console-command ownership, transport failures, timer-driven timeouts, the production monotonic clock, and runtime password loading. Processor callback behavior and real device timing require the lab exercise above.

## Preparation

This original programming sample was developed with assistance from OpenAI Codex, followed by review, targeted improvements, and macOS build and test validation with Claude Code. Reproducible commands, validation results, and outstanding hardware checks are documented in `VALIDATION.txt`.

ChatGPT also assisted with the RMC3 port, the virtual display, and an independent review of the Mac CLI hardware-run evidence.
