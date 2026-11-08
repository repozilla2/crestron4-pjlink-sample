RMC3-compatible PJLink port

This port derives from the reviewed 4-Series sample at commit
80e58b24482c14ed364f703c2fedf6aace041094.

The three source files preserve the protocol state machine and adapter lifecycle
while targeting the Compact Framework 3.5 and genuine 3-Series SDK references.
Production compilation uses the RMC3 symbol. The desktop test projects compile
the same source with desktop equivalents for the clock, file I/O and MD5.

The signed Rmc3PjLink package passed all 20 hardware acceptance checks on an
RMC3 with PUF 1.8001.0298 and OS 1.8001.6192.21873 on November 7, 2026,
America/Los_Angeles. Its peer was the included virtual PJLink display running
on a Mac. A subsequent AI-assisted evidence review correlated the Mac CLI run's
results and verified all 692 evidence checksums. See docs/HARDWARE-VALIDATION.md from
the repository root for production-source hashes, package identity, and observations.

Production substitutions

- Crestron.SimplSharp.Stopwatch supplies elapsed time.
- Crestron.SimplSharp.CrestronIO supplies file reading and application paths.
- Crestron.SimplSharp.Cryptography.MD5 supplies PJLink 1.04 authentication.
- C# 3 syntax replaces newer syntax. StringBuilder.Length = 0 clears framing
  state, and invariant-culture casing replaces ToLowerInvariant.
- Explicit ASCII conversion rejects every byte or character above 0x7f.
- Explicit event accessors use a shared subscription lock. Callbacks use a
  delegate snapshot and run after that lock is released. This produces the
  Monitor synchronization accepted by the 3-Series sandbox validator.
- The host address is selected from a runtime file on every explicit connect,
  and configuration reads are bounded to 64 characters.

Runtime configuration

The program uses TCP port 4352 and a 30-second greeting/response timeout.
Place both configuration files in the program's application directory.

pjlink-host.txt

Required for each explicit connection. It contains one canonical dotted IPv4
address in 10.0.0.0/8, 172.16.0.0/12, or 192.168.0.0/16. The address is read on
every pjlink connect request. Leading-zero octets, whitespace, hostnames,
public addresses, and additional address lines are rejected. Trailing CR/LF is
accepted.

pjlink-password.txt

Optional. The password is read during program construction. A missing file
selects an empty password. A configured password contains up to 32 ASCII
alphanumeric characters, with an optional trailing CR/LF. A changed password
requires a fresh program start. The file should be verified after deployment
before testing authentication. Each configuration read accepts at most 64
characters including trailing line endings.

Console behavior

The fresh initialization payload is RMC3 PJLink sample 0.1 loaded.
The console command remains pjlink, with connect, disconnect, status, query,
on and off operations. Connections and reconnects are explicit. Set-command
acknowledgements preserve confirmed feedback until a later query reports the
device's state. PJLink ready means that the greeting was accepted. An incorrect
password is detected by the first command response. Paused commands are ignored.

The stopped program releases its command only when it owns that registration.
Socket callbacks, timer callbacks, and commands use the existing serialization
and stale-socket checks. The protocol supports the original PJLink 1.04 power
commands and authentication behavior.

Desktop validation

From the repository root, use the .NET 10 SDK selected by global.json.

dotnet run --project lab/Rmc3PjLink.Tests/Core/Core.Tests.csproj -c Release
dotnet run --project lab/Rmc3PjLink.Tests/Adapter/Adapter.Tests.csproj -c Release
dotnet run --project lab/Rmc3PjLink.Tests/Events/Events.Tests.csproj -c Release

The core project links the original 18-group test program. The adapter project
retains the original 10 groups and adds runtime-host selection and validation,
high-bit ASCII rejection, bounded configuration reads, credential-log protection,
and invariant console-command casing. Its SDK doubles model adapter control flow.
The recorded result is 18 core groups, 15 adapter groups, and 5 event groups
passed. The event groups verify duplicate subscriptions, removal, and
cross-thread subscription changes while callbacks run.

The test harness uses desktop clock, cryptography and I/O implementations.
The hardware run exercised the genuine Crestron runtime providers, sockets,
timers, and program lifecycle.

Virtual display and TCP checks

From the repository root, run the simulator's 36 tests and the 11 loopback TCP
scenarios with the following commands. Python 3.9 or later is required.
Choose a new results directory for each integration run.

python3 -m unittest discover -s lab/VirtualPjLink -p test_virtual_pjlink.py -v
python3 lab/PjLinkIntegration.Tests/run_integration.py --results /path/to/new-results-directory

The TCP harness links PjLinkSession into a desktop transport driver. The
simulator's README describes authenticated operation, virtual power states,
and controlled fault scenarios. See lab/VirtualPjLink/README.md.

Processor build prerequisites

The dotnet commands above produce desktop test executables. An RMC3 processor
build uses authorized legacy Crestron SIMPL# Pro development tools and
Compact Framework 3.5 reference assemblies.

The recorded production build followed these stages.

1. Compile PjLinkSession.cs, SampleConfiguration.cs, and ControlSystem.cs into
   Rmc3PjLink.dll with the RMC3 compilation symbol, C# 3 language compatibility,
   library output, optimization, deterministic output, and explicit references
   selected with the compiler's nostdlib option.
2. Resolve mscorlib.dll, System.dll, System.Core.dll, and System.Data.dll from
   the Windows CE Compact Framework 3.5 reference set. Resolve
   SimplSharpHelperInterface.dll, SimplSharpPro.exe,
   Crestron.SimplSharpPro.DeviceSupport.dll,
   SimplSharpCustomAttributesInterface.dll, and
   SimplSharpCryptographyInterface.dll from the genuine legacy Crestron SDK.
3. Validate the assembly with Crestron's Windows sandbox validator, then use
   the vendor signer and package creator to generate the processor CPZ with
   its SDK dependencies and program metadata.

The repository supplies the three production sources and desktop test projects.
Reproducing the processor package requires the licensed legacy toolchain and
its configured Windows validation/signing environment. The retained build
record identifies revision 4fdb6275b57179666ad370b20fdebf93a08ea7a7 and an
unsigned assembly of 22,016 bytes with SHA-256
9ff3061b7683e988dfbcebb18b063c8a107dda0c44746118ac74ba39a2e1a1d0.
Signed-package provenance and the hardware-tested source hashes are recorded
in docs/HARDWARE-VALIDATION.md.

Remaining hardware coverage

Physical-display operation, hardware pause/resume, extended-duration behavior,
and configuration persistence across a fresh program load remain pending.
The recorded runtime-file checks cover the two program restarts. The primary
4-Series implementation's hardware status is listed in the root README.
