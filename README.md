# Crestron 4-Series C# programming sample

## PJLink display power control

Prepared for Keith Gariepy · 6 November 2026

This original, AI-assisted example demonstrates a small SIMPL# Pro program for a programmable 4-Series control system. An operator connects to a PJLink display, queries its power state, requests power on or off, and reads the resulting feedback. The source includes a reusable protocol component, the Crestron TCP adapter, and two executable regression-test harnesses.

## Review order

| File | What to inspect |
| --- | --- |
| `src/CrestronSample/ControlSystem.cs` | Crestron lifecycle, TCP callbacks, console commands, serialization, cleanup, and safe diagnostics |
| `src/DisplayControl.Core/PjLinkSession.cs` | Framing, greeting and authentication, command state, confirmed feedback, and timeouts |
| `tests/DisplayControl.Tests/Program.cs` | Deterministic examples and protocol regression cases |
| `tests/CrestronAdapter.Tests/Program.cs` | Lifecycle, stale callbacks, inline completion, cleanup ownership, and credential handling using SDK doubles |
| `src/CrestronSample/SampleConfiguration.cs` | Demonstration endpoint, port, timeout, and runtime password loading |
| `VALIDATION.txt` | Recorded build, test, and archive-inspection evidence |

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

Install the .NET 10 SDK for the Mac's architecture. The desktop test executables target `net10.0` and need the .NET 10 runtime, which the SDK includes. Microsoft supports .NET 10 into November 2028; .NET 8 support ends on November 10, 2026. Visual Studio Code is optional. Package restore needs access to NuGet. [Microsoft macOS installation guide](https://learn.microsoft.com/en-us/dotnet/core/install/macos)

Building the processor project requires appropriate authorization to use Crestron's SDK under its applicable software-development terms. The repository contains original source and package references. SDK assemblies are restored through NuGet during the build. [Crestron software-development terms](https://www.crestron.com/Legal/software-license-agreement/Software-Development-Tools-License-Agreement)

From the repository root or extracted source directory, run the following commands.

```bash
dotnet build CrestronSample.sln -c Release
dotnet run --project tests/DisplayControl.Tests/DisplayControl.Tests.csproj -c Release --no-build
dotnet run --project tests/CrestronAdapter.Tests/CrestronAdapter.Tests.csproj -c Release --no-build
```

Each test executable returns exit code `0` when every group passes and exit code `1` when any group fails. Both use small dependency-free harnesses with explicit assertions. The core targets `netstandard2.0`, both test executables target `net10.0`, and the processor program targets `net6.0`. The processor target follows the Crestron SDK: package `2.22.40` ships only `net47` and `net6.0` assemblies.

The adapter tests compile the production adapter source against explicit SDK doubles. They exercise its control-flow decisions, including adverse callback ordering and timer-driven timeouts. An internal constructor lets the tests supply a manual clock; the processor uses the public constructor and a `Stopwatch`. Processor runtime behavior and device integration remain part of the lab exercise below.

The processor project pins `Crestron.SimplSharp.SDK.Program` to `2.22.40`. Crestron's package produces `src/CrestronSample/bin/Release/net6.0/CrestronSample.cpz` as part of a successful build. [Crestron SDK package](https://www.nuget.org/packages/Crestron.SimplSharp.SDK.Program/2.22.40)

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

## Verification status

| Check | Status |
| --- | --- |
| Clean build on macOS 27.2 arm64 with .NET SDK 10.0.403 | Passed with 0 warnings and 0 errors at commit `7cf31ae` |
| Core regression tests on .NET 10 | 18 of 18 test groups passed, exit code 0 |
| Adapter regression tests using SDK doubles on .NET 10 | 9 of 9 test groups passed, exit code 0 |
| Crestron SDK compilation and CPZ packaging on macOS | Passed using the official, unmodified SDK build targets; CPZ inspected |
| Original Linux build and tests with .NET SDK 8.0.427 | Passed at commit `544b222`; preserved in `VALIDATION.txt` |
| Original `net8.0` tests on macOS with .NET SDK 10.0.403 | Passed 18/18 and 8/8 at commit `544b222` only with an explicit `DOTNET_ROLL_FORWARD=Major` override; superseded by the native .NET 10 rows above |
| Execution on a physical Crestron processor and firmware | Pending |
| Real display, network recovery, and lifecycle exercise | Pending |

The core tests cover fragmentation, authentication, power states, acknowledgments, error responses, overlapping requests, timeout boundaries, late data, restart, invalid messages, and buffer limits. The adapter tests cover pause/resume, stop, stale callbacks, inline callbacks, console-command ownership, transport failures, timer-driven timeouts, and runtime password loading. Processor callback behavior and real device timing require the lab exercise above.

## Preparation

The source was created specifically for this review with OpenAI Codex assistance and checked through code review, actual compilation, and the recorded tests. The example uses published protocol and SDK information. It is supplied with source and verification evidence so its behavior and design choices can be examined directly.
