# Concurrency qualification and teach-back

Prepared November 12, 2026 (America/Los_Angeles). Reviewed baseline:
`6edf16d55d5470b3dd6a2254ef65ef80d189ac40`.

This is a pre-coding learning exercise applied to an existing implementation.
It records the reasoning, executable desktop checks, and remaining processor
questions. It does not establish instructor approval, course completion, or
native runtime guarantees. Production source is unchanged by this exercise.

## Evidence and applicability

| Evidence | What it establishes | Limit |
| --- | --- | --- |
| [PJLink Class 1 v1.04](https://pjlink.jbmia.or.jp/english/data/5-1_PJLink_eng_20131210.pdf), sections 5.3 and 6 | Commands must wait for the preceding response for guaranteed behavior; later commands on one authenticated connection may omit the digest | Applies to this legacy power subset; newer authentication is out of scope |
| [Crestron TCPClient reference](https://help.crestron.com/SimplSharp/html/fea02e94-4ae9-d1ef-83f9-fb054c80bbe0.htm) | Asynchronous operations complete through callbacks; Dispose disconnects and frees resources; receives use the incoming buffer | Public reference identifies assembly 2.21.121; the project pins SDK 2.22.40. This page does not establish callback draining or ordering |
| [Crestron CTimer reference](https://help.crestron.com/SimplSharp/html/32ac873b-3015-bacc-b204-1dbdc8bfcdde.htm) | A timer can be stopped or disposed | Does not establish that previously dispatched callbacks have returned |
| Production code in `src/` and `lab/Rmc3PjLink/` | Both adapters serialize session calls, check socket identity, and detach before disposal | Code inspection establishes an intended strategy, not its behavior on untested firmware |
| Shared desktop overlap scenarios | Selected real-thread schedules against both production adapters with SDK doubles | Desktop .NET 10 threads and mocked callbacks do not model the processor scheduler |

## Teach-back: why these decisions matter

1. **The state lock defines admission and ordering.** Console, socket, and timer
   work must inspect and mutate the session under the same lock. A stop that
   obtains the lock first closes admission. A receive already inside the lock
   may complete before stop; its feedback must subsequently become Unknown.
2. **The deadline is checked when the response is processed.** The sample uses
   a caller-supplied monotonic clock. A response handled at or after the deadline
   loses even if the timer has not run. The response-first test deliberately
   processes at 29.999 seconds, then queues the timer at 30 seconds. It does not
   assert priority for bytes arriving at the network interface before a deadline.
3. **Socket identity scopes callbacks to a connection.** Once the adapter has
   detached a client, that client's receive and send callbacks cannot change a
   replacement session. A callback from the current socket still needs the lock.
4. **Cleanup has one owner.** Nested inline callbacks can set `closeRequested`;
   only the outermost serialized call detaches the client. Disposal follows
   lock release. Program stop detaches its resources under that same lock and
   disposes them afterward. These paths need separate native verification.
5. **A write can have an uncertain outcome.** A short or failed send completion
   cannot establish whether the peer acted on any bytes. The sample clears
   session state and requires explicit recovery. It never automatically replays
   a power command.
6. **Observation and freshness are different.** `POWR=OK` accepts a request;
   only a valid query supplies observed power. A retained observation is not
   necessarily current after a set or failed query. A future UI needs observation
   time and a freshness policy. That feature is not implemented in this exercise.

## Executable schedules and predictions

The shared file `tests/Shared/AdapterConcurrencyScenarios.cs` is compiled into
both adapter executables. Each scenario uses two real desktop threads. A
test-only diagnostic hook holds the owner inside the production state lock.
The test observes the contender waiting before releasing the owner. Waits and
joins have five-second failure budgets; no sleeps select a desired ordering.
Collection assertions occur after the workers join. Test hooks never enter a
processor package.

| Schedule | Prediction asserted |
| --- | --- |
| Receive holds lock; stop contends | Response completes once, then stop clears state, removes its command, and disposes socket once outside lock; subsequent callbacks start no work |
| Stop holds lock; receive contends | Pending response is ignored; no On observation is published; socket is disposed once |
| Timeout holds lock; response contends | Late response cannot complete; state becomes Unknown; explicit new connection works |
| Pre-deadline response holds lock; timer contends at deadline | Response completes once and clears deadline; queued timer cannot fault an idle ready session |
| Disconnect holds lock; failed send completion contends | Obsolete completion is ignored; socket closes once; replacement connection stays healthy |
| Failed send completion holds lock; disconnect contends | Unknown outcome is reported; cleanup happens once; recovery sends only the explicitly requested query |

These selected schedules supplement the existing inline, stale-callback,
timeout-boundary, and protocol-framing tests. They do not exhaust all schedules,
prove deadlock freedom, or establish hardware behavior. The test relies on
desktop ThreadState reporting a blocked Monitor entrant as WaitSleepJoin.

## Qualification gate

- Run both adapter suites and retain their outputs with the exact input hashes.
- Use a deliberate negative control to show the overlap assertions can fail.
- Review unfamiliar variants with Keith before treating the explanations as
  expert-qualified rules. These six scenarios are disclosed development cases
  and must not later be presented as a blinded assessment.
- Execute [4-Series acceptance](4-SERIES-ACCEPTANCE.md) on the chosen target.
- Reopen the rule if firmware observations contradict a desktop prediction.

Human expert assessment and 4-Series execution remain pending until their
separate evidence is recorded. Desktop results are in the dated validation
record linked from the README.
