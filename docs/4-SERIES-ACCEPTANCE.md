# 4-Series acceptance worksheet

Prepared November 12, 2026 (America/Los_Angeles). **Status: NOT RUN.**
This worksheet concerns the `net6.0` program in `src/`. It does not inherit
the RMC3 port's hardware pass. First use an isolated simulator bench and the
existing [processor setup guide](PROCESSOR-SETUP-AND-TESTING.md).

## Record before loading

| Field | Value |
| --- | --- |
| Operator and local start/end time | PENDING |
| Processor model, hardware revision, serial identifier (private record) | PENDING |
| Installed firmware, OS, and verified .NET 6 compatibility | PENDING |
| Repository commit and clean working-tree state | PENDING |
| Build OS, .NET SDK/runtime, Crestron package versions | PENDING |
| CPZ size and SHA-256; production-source SHA-256 values | PENDING |
| Test slot, prior program ownership and restorable backup | PENDING |
| Toolbox/transfer tool versions; confirmed model-specific load and stop procedure | PENDING |
| Simulator version, source hash, reachable host and permitted processor address | PENDING |
| Actual application directory and configuration presence (never password contents) | PENDING |

Use the target model's official firmware and commissioning documentation.
Do not copy a firmware threshold or console command from another processor
without verifying applicability. Keep backup material and credentials private.
Preserve continuous timestamped console and simulator event records.

## Bench sequence

For each row record PASS, FAIL, BLOCKED, or NOT RUN, plus an evidence location.
Wait for command completion before the next command. Query timing during
transitions follows the endpoint's documented behavior. All rows start NOT RUN.

| ID | Procedure | Acceptance evidence | Result |
| --- | --- | --- | --- |
| F01 | Build and inspect the exact 4-Series CPZ, transfer it, verify transferred bytes, then load the owned test slot | Matching artifact hash; fresh startup; intended model/firmware/slot recorded; no unexplained new error entries | NOT RUN |
| F02 | Configure host/password privately and restart; repeat restart once | Intended runtime files survive restart; no automatic connection; explicit connect reaches selected host | NOT RUN |
| F03 | Unauthenticated simulator: connect, query, on, query through Warming/On, off, query through Cooling/Off, disconnect | Ordered replies; acknowledgments do not invent a new observed state; queries establish all available states | NOT RUN |
| F04 | Authenticated simulator: repeat F03, then repeat with a deliberately wrong lab credential and restore it | First-command authentication works; rejection closes session; fresh correct connection recovers; no credential/digest logging | NOT RUN |
| F05 | Use fragmented CRLF responses; repeat a power cycle | Complete ordered transactions; no false framing failure. Record that TCP can merge simulator writes | NOT RUN |
| F06 | Suppress greeting, then separately suppress response and attempt another command while pending | Timeout near 30 seconds plus measured scheduling delay; pending guard; Unknown state after close; explicit recovery | NOT RUN |
| F07 | Delay an old response past timeout; recover on a fresh connection before the delayed attempt | No old response changes fresh state; fresh query succeeds before and after old delayed-send window | NOT RUN |
| F08 | Break connection during a pending power request; separately close an idle connection | No automatic replay; cleared state; closure diagnostic appropriate to local transaction state; explicit query on new connection works | NOT RUN |
| F09 | Stop gracefully during a pending query; repeat during a pending set on the simulator | No new application requests after stop; test command released; simulator sees peer closure; restart requires explicit connect | NOT RUN |
| F10 | If an official pause/resume mechanism exists for this target, pause during a transaction, then resume | Peer closed; commands suppressed while paused; resume uses a fresh explicitly requested connection. Otherwise mark BLOCKED with reason | NOT RUN |
| F11 | Send simulator ERR4 and separately invalid ASCII; reconnect as required | Device error does not invent feedback; invalid bytes close session; recovery succeeds; no credential text in diagnostics | NOT RUN |
| F12 | Perform 25 explicit connect/query/disconnect cycles, then review error log and final state | Every requested query has one matching completion; no unexplained errors or residual test connections; graceful final stop | NOT RUN |
| F13 | On the isolated bench, verify configuration persistence through a fresh program load using the backed-up test slot | Actual survival/replacement behavior recorded; recover from private backup if needed; no assumptions from restart tests | NOT RUN |

F09 and F10 exercise real lifecycle behavior but cannot force the exact desktop
thread interleavings. F12 is bounded repeatability coverage, not a soak test.
Observed peer closure establishes socket behavior visible to the simulator;
it does not prove internal callbacks have drained.

## Physical display and next decisions

After the simulator pass, repeat normal and authenticated operation on a real
PJLink-capable display. Record make/model, device firmware, standby network
settings, wake behavior, actual transition times, and external-control changes.
Mark unsupported states or features explicitly. Simulator success does not
establish compatibility with a particular display.

Stop the run for an unexpected controlled device, unexpected power action,
repeated completion, post-stop request, stale feedback affecting the new
connection, lost configuration without a recoverable backup, unexplained error,
or inability to identify the deployed artifact. Preserve evidence before
changing code or repeating a failed case. Resume after the cause and revised
acceptance check are recorded.

Advance the **tested model/firmware/SDK combination** after required simulator
rows pass and remaining BLOCKED rows have an explicit disposition. Advance a
specific display only after its separate pass. Keep UI freshness and automated
recovery work behind those results. Human review of the concurrency teach-back
is a separate qualification gate.
