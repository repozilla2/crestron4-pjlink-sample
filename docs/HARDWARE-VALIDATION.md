# RMC3 hardware validation

**All 20 acceptance checks passed on November 7, 2026, America/Los_Angeles.** A subsequent AI-assisted evidence review correlated the Mac CLI run's processor captures, simulator events, command records, and package identities, and verified all 692 evidence checksums.

The tested implementation is the Compact Framework 3.5 port in `lab/Rmc3PjLink`. A physical RMC3 communicated over TCP with the included Python virtual PJLink display on a Mac. The primary 4-Series implementation in `src/` targets `net6.0` and has the desktop/build evidence listed in the main README. [RMC3 port](../lab/Rmc3PjLink/README.txt) · [Primary sample](../README.md)

## Environment and artifact identity

| Item | Recorded value |
| --- | --- |
| Processor | Crestron RMC3 |
| PUF / OS | `1.8001.0298` / `1.8001.6192.21873` |
| Simulator environment | macOS 27.2, build 26B123, Python 3.15.2 |
| Base sample revision | `80e58b24482c14ed364f703c2fedf6aace041094` |
| Retained Windows build-record revision | `4fdb6275b57179666ad370b20fdebf93a08ea7a7` |
| Deployed package | `Rmc3PjLink.cpz`, 2,715,107 bytes, 11 archive entries |
| CPZ SHA-256 | `2e8422466e252ace2f93b4405d23a219e35c28e52567e620565e376cbfa6c54d` |
| Signed application DLL SHA-256 | `8c6120916759780ba4bf208cec138a64a1f3edd94a2270dbed537718e45410b6` |
| Hardware review archive SHA-256 | `013d3b79177df0d4c3ff1a8af17f0bc64320ef0edb6e20b1a73bcb6600c800b3` |

The build-record revision identifies the retained build workspace. These source hashes provide the direct correspondence between the published production files and the hardware-tested package.

| Production file | SHA-256 |
| --- | --- |
| [ControlSystem.cs](../lab/Rmc3PjLink/ControlSystem.cs) | `c214832b6f266919554a61590e735495050ed93a8e848692e125c5f74ddb45a4` |
| [PjLinkSession.cs](../lab/Rmc3PjLink/PjLinkSession.cs) | `69c62b641288b727ae3bd20fd6914b7513bb7adfaa3927ce10a32c6a75e5a429` |
| [SampleConfiguration.cs](../lab/Rmc3PjLink/SampleConfiguration.cs) | `47e2cc6be8673aa145797962befeb3c6a347827dcd5c078af69fab7f3a009f44` |

The unsigned production assembly compiled against Compact Framework 3.5 and genuine legacy Crestron references. Crestron's Windows validator, signer, and package creator produced the CPZ. Package inspection checked its archive contents, dependency identities, and signed-content digests. Windows reported an untrusted root for the vendor's legacy self-signed certificate. Signature and signed-content checks passed against that exact certificate.

## Acceptance results

| Check | Result | Observation |
| --- | --- | --- |
| P01 Package identity | PASS | Pinned size/hash, archive integrity, and supplied source checks passed |
| P02 Processor identity | PASS | Live identity, firmware, route, and simulator address verified |
| P03 Deployment preconditions | PASS | Stopped test-program ownership, command availability, command syntax, and 11-file backup verified |
| P04 Transfer integrity | PASS | Downloaded staged CPZ matched the original package byte for byte |
| P05 Initial load | PASS | One load, one fresh startup payload, and a fresh processor last-start timestamp |
| P06 Configuration and restart 1 | PASS | Recorded configuration-byte equality, fresh initialization, and persistence through restart |
| P07 Unauthenticated power cycle | PASS | Off → Warming → On → Cooling → Off, with acknowledged commands and confirmed feedback |
| P08 Authenticated power cycle | PASS | All four states, successful authentication, and first-command-only digest behavior |
| P09 Fragmented CRLF cycle | PASS | One-byte simulator writes with delays and CRLF responses completed the cycle |
| P10 Device error | PASS | ERR4 completed the command while preserving a usable session, followed by explicit recovery |
| P11 Greeting timeout | PASS | Specific timeout near 30 seconds, peer closure before the simulator's 35-second hold, and explicit recovery |
| P12 Response timeout and pending guard | PASS | Second command rejected while pending, zero held-data bytes, timeout cleanup, and explicit recovery |
| P13 Delayed-response recovery | PASS | Recovery established before the old delayed write, with healthy queries before and after that attempt |
| P14 Socket closure | PASS | Expected diagnostic, cleared state, and explicit reconnection |
| P15 Forced authentication rejection | PASS | Session closure followed by successful authentication on a fresh connection |
| P16 Wrong password | PASS | Rejection followed by a deliberately corrected simulator credential and successful recovery |
| P17 Non-ASCII input | PASS | Transport rejection, cleared feedback, and successful explicit recovery |
| P18 Restart 2 | PASS | Fresh initialization, configuration persistence, five seconds without an automatic connection, then successful explicit authentication/query |
| P19 Processor error log | PASS | Baseline retained and new entries accounted for by the planned lifecycle and platform notices |
| P20 Final state and cleanup | PASS | Program gracefully stopped, command unavailable, and simulator/SSH cleanup recorded |

The journal records **one upload, one initial load, two program restarts, and three graceful stops**. All three power-cycle cases preserve the previous confirmed feedback after a set-command acknowledgment until a query reports the new state. The final recorded program state is **Stopped**.

## Review findings and qualifications

- The execution capture contains 127,388 bytes covered by 517 contiguous receive records. All 224 command-completion excerpts across the preflight and execution captures match their raw byte ranges.
- Sixteen query-result objects pair a prompt-bounded text excerpt with an end offset sampled after later asynchronous feedback arrived. The continuous captures preserve those additional lines. The review used exact command-completion bounds and later observations for the affected checks.
- The old delayed write in P13 ended in `BrokenPipeError` at 35.010 seconds. The recovered connection opened 2.640 seconds earlier and answered correctly before and after that attempt.
- Fragmentation coverage measures simulator write boundaries. TCP may combine writes before a processor receive callback.
- Private backup and credential-byte comparisons are supported by recorded local comparison and SFTP results. The retained local originals support byte-for-byte replay. Authenticated post-restart exchanges corroborate runtime credential use.
- All 13 simulator processes have direct exit-code records of zero. The SSH master has a successful exit record and a subsequent absent-socket check. Collector exit is inferred from its completed cleanup and reviewed return path.

## Further validation

| Coverage | Status |
| --- | --- |
| Physical PJLink display and its power transitions | Pending |
| Primary `net6.0` program on a 4-Series processor | Pending |
| Hardware program pause/resume | Pending a verified mechanism for this firmware |
| Extended-duration reliability | Pending |
| Configuration persistence across a fresh program load | Pending |

The completed run establishes configuration persistence across the two program restarts. Desktop adapter tests exercise pause/resume handlers. The optional repeatability exercise adds bounded repeated-operation coverage when performed and recorded as a subsequent run.
