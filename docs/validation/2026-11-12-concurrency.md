# Desktop concurrency qualification — November 12, 2026

**Result: all seven final suites passed.** Production source is byte-for-byte
unchanged from `6edf16d55d5470b3dd6a2254ef65ef80d189ac40`.
Completion time: 2026-11-12 10:00:57 America/Los_Angeles (18:00:57 UTC).

This run qualifies six disclosed development scenarios against each adapter's
desktop test double. It does not qualify 4-Series firmware execution, a physical
display, or an independent human assessment.

## Environment and results

Linux x64, glibc 2.41; .NET SDK 10.0.403; desktop runtime 10.0.14;
Python 3.12.14; Crestron SDK 2.22.40. Production targets remain
`netstandard2.0` (core), `net6.0` (4-Series adapter), and legacy Compact Framework
3.5 (RMC3 production port). Desktop tests target `net10.0`.

| Check | Result |
| --- | --- |
| Final primary solution Release build, single node | PASS; 0 warnings, 0 errors |
| Primary protocol | 18/18 groups |
| Primary adapter, including six overlap scenarios | 19/19 groups |
| RMC3 protocol in desktop harness | 18/18 groups |
| RMC3 adapter, including the identical six overlap scenarios | 21/21 groups |
| RMC3 event subscription tests | 5/5 groups |
| Python virtual display | 36/36 cases |
| Actual loopback TCP, RMC3 protocol in desktop transport | 11/11 scenarios |
| Production source restoration | All six production C# files match their pre-mutation SHA-256 values |
| Generated 4-Series CPZ | 40 entries; ZIP CRC check passed |

The new tests cover both lock acquisition orders for receive/stop,
response/timeout, and disconnect/failed-send-completion. They verify one-time
socket disposal outside the state lock, cleared state, suppression of obsolete
callbacks, and explicit recovery without replaying an uncertain power write.
See the [teach-back](../CONCURRENCY-QUALIFICATION.md) for exact predictions and
the limits of the thread scheduling technique.

## Negative controls

For each protocol implementation separately, the run temporarily removed
`_deadline = null` from the successful response path in `HandleLine`, retaining
the obsolete command deadline. It rebuilt and ran that implementation's adapter
suite. Each mutant built and failed exactly one group:

`Concurrent response completes before timeout — The subsequent timer must see a completed transaction.`

The primary mutant passed 18/19 groups and the RMC3 mutant passed 20/21 groups.
Each source was restored in a `finally` block. Hash equality was checked before
the final primary build, and again after all seven final suites. Mutated binaries
were replaced by final clean builds before recording the package identity.

This control demonstrates sensitivity to deadline retention. It does not show
that every assertion or every concurrency fault has an effective negative control.

## Reproduction

From a checkout with the pinned SDK and authorized NuGet access:

```bash
dotnet build CrestronSample.sln -c Release -m:1
dotnet run --project tests/DisplayControl.Tests/DisplayControl.Tests.csproj -c Release --no-build
dotnet run --project tests/CrestronAdapter.Tests/CrestronAdapter.Tests.csproj -c Release --no-build
dotnet run --project lab/Rmc3PjLink.Tests/Core/Core.Tests.csproj -c Release
dotnet run --project lab/Rmc3PjLink.Tests/Adapter/Adapter.Tests.csproj -c Release
dotnet run --project lab/Rmc3PjLink.Tests/Events/Events.Tests.csproj -c Release
python -m unittest discover -s lab/VirtualPjLink -v
python lab/PjLinkIntegration.Tests/run_integration.py --results /path/to/new-results-directory
```

Build and test outputs, command arguments, production and changed-test input
hashes, negative-control results, and package identity are retained in the
[machine-readable record](2026-11-12-concurrency.json). Paths are normalized for
portability. The RMC3 source hashes match the published hardware record; that
comparison does not re-execute or independently verify the historical hardware run.

The generated `CrestronSample.cpz` was 6,678,183 bytes, with SHA-256
`8d341c69ac3d3f49c2a64d8ea932ed9c0ee205752d25bd915e8e3d876d1b171d`.
It is not committed or distributed. A rebuild can produce a different package
checksum because of build metadata.

## Remaining qualification

No production fix was required by these checks. The [4-Series worksheet](../4-SERIES-ACCEPTANCE.md)
remains NOT RUN. The processor/firmware/toolchain combination, native callback
behavior, real-display compatibility, and Keith's assessment of unfamiliar
reasoning scenarios remain separate gates.
