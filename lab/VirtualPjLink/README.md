# Virtual PJLink power device

This Python endpoint runs on the Mac and receives commands from the RMC3 over
the LAN. It implements the PJLink 1.04 power-command subset used by the sample.
Its state machine produces Off, Warming, On, and Cooling feedback. Each process
starts a fresh virtual device, and state persists across connections while that
process runs.

The implementation uses Python 3.9 or later and the standard library. The
protocol framing, power values, and legacy MD5 exchange follow the JBMIA
specification, sections 2, 4.1, 4.2, and 5. Unsupported well-formed Class 1
commands receive `ERR1`. [PJLink 1.04 specification](https://pjlink.jbmia.or.jp/english/data/5-1_PJLink_eng_20131210.pdf)

## Run the local checks

From this directory, run the following commands.

```bash
python3 --version
python3 -m unittest -v test_virtual_pjlink.py
```

The tests use loopback TCP connections and a deterministic clock for transition
boundaries. They cover the official authentication fixture, raw command and
response bytes, all four states, timeout and recovery scenarios, frame bounds,
redacted logs, and starting and stopping the CLI as a subprocess.

## Start a virtual display for the RMC3

Use the Mac's current LAN address in place of `MAC_LAN_IP` and the processor's
current address in place of `RMC3_LAN_IP`. Confirm both addresses before
connecting. The Mac can use Wi-Fi.

```bash
python3 virtual_pjlink.py \
  --bind MAC_LAN_IP \
  --port 4352 \
  --allow-client RMC3_LAN_IP \
  --warmup-seconds 10 \
  --cooldown-seconds 10 \
  --log virtual-pjlink-normal.jsonl
```

The first JSON line has `event` set to `listening` and gives the actual bound
address and port. Keep this Terminal open. If macOS asks whether Python may
accept incoming connections, allow it for this test.

The RMC3 application's `pjlink-host.txt` must contain that same Mac LAN address.
The port README describes runtime configuration and the processor-build
prerequisites. [RMC3 port](../Rmc3PjLink/README.txt)

Issue each RMC3 console command after the prior command's response arrives.
With the normal scenario and ten-second transitions, the following sequence
exercises the virtual display.

| RMC3 console action | Virtual response or state |
| --- | --- |
| `pjlink connect` | Greeting accepted |
| `pjlink query` | Off, value `0` |
| `pjlink on` | Acknowledgement `OK` |
| `pjlink query` during warmup | Warming, value `3` |
| `pjlink query` after ten seconds | On, value `1` |
| `pjlink off` | Acknowledgement `OK` |
| `pjlink query` during cooldown | Cooling, value `2` |
| `pjlink query` after ten seconds | Off, value `0` |
| `pjlink disconnect` | Connection closed |

Power feedback is supplied by queries. Set requests during warmup or cooldown
receive `ERR3`. Repeating an already satisfied on/off request receives `OK`.
The default idle deadline closes a connection after 30 seconds without a
complete command. Each completed response starts the next idle interval.

Press **Control-C** in the simulator Terminal to close its listener and active
connections. The last event should be `server_stopped`. Choose a fresh log
filename for each run because an existing evidence file causes startup to fail.

## Authentication

Omitting authentication options produces the `PJLINK 0` greeting. To exercise
legacy authentication, add `--ask-password` and enter a dedicated lab password
in that Terminal. Use the same password in the processor's private runtime
password file, then restart the program to read it. [Runtime configuration](../Rmc3PjLink/README.txt)

```bash
python3 virtual_pjlink.py \
  --bind MAC_LAN_IP \
  --allow-client RMC3_LAN_IP \
  --ask-password \
  --warmup-seconds 10 \
  --cooldown-seconds 10 \
  --log virtual-pjlink-auth.jsonl
```

Without the two transition options the simulator uses 3-second transitions,
which leaves little time to observe Warming and Cooling.

Automation can use `--password-file /absolute/path/to/pjlink-password.txt`.
Passwords contain 1–32 ASCII letters or digits. A final LF or CRLF in that file
is accepted. A fresh eight-character challenge is generated for each connection.
Authentication is required on the first command, and subsequent commands may
omit the digest. The same connection also accepts its valid digest on later
commands, as allowed by section 5.3 of the specification.

The simulator's JSONL records contain sanitized command meanings and generated
feedback. Passwords, challenge values, authentication digests, and raw incoming
frames are excluded from those records. The logger creates evidence files with
owner-only permissions. The test fixture password `JBMIAProjectorLink` comes
from the public specification.

## Controlled scenarios

Choose a scenario when starting a fresh process. Fault scenarios fire once per
process. Later connections and commands use normal behavior, allowing an
explicit reconnect to demonstrate recovery. `normal` and `fragmented` apply
throughout the run.

| `--scenario` | Behavior |
| --- | --- |
| `normal` | Normal greeting and power exchange |
| `fragmented` | Every outgoing greeting and response is written in small pieces |
| `no-greeting` | First accepted connection receives silence for `--fault-delay` seconds or until the client disconnects |
| `no-response` | First valid power command is processed, and its reply is suppressed while the connection is held |
| `late-response` | First valid power reply is delayed by `--fault-delay` seconds on its original connection |
| `device-error` | First valid power request returns `ERR4` before executing the requested power operation |
| `disconnect` | First valid power request causes a close before command execution |
| `reject-auth` | First authentication attempt receives `PJLINK ERRA`, including a correct digest |
| `non-ascii` | First valid power request is processed and receives a reply containing byte `0x80` |

`reject-auth` requires `--ask-password` or `--password-file`. Invalid credentials
also receive `PJLINK ERRA` in the normal scenario. After rejection, the server
ignores further commands until the client disconnects or the idle deadline
expires.

The default `--fault-delay 35` exceeds the sample's 30-second response timeout.
For a timeout test, start a fresh simulator process with the chosen scenario,
connect from the RMC3, and use `pjlink query` where the scenario needs a command.
Record the RMC3's timeout and disconnection before explicitly reconnecting.
The virtual state persists throughout that simulator process, including when a
power command's response was suppressed.

During a silent hold, `held_data_received` records the byte count of each later
socket read. To test the controller's pending-command guard, wait for the
`no-response` fault's `fault_injected` event before issuing an additional console
query. Require its rejection and no `held_data_received` event before the peer
closes. This observation covers input received during the hold. Input already
buffered with the first command falls outside that observation window.

```bash
python3 virtual_pjlink.py \
  --bind MAC_LAN_IP \
  --allow-client RMC3_LAN_IP \
  --scenario no-response \
  --fault-delay 35 \
  --log virtual-pjlink-no-response.jsonl
```

`--fragment-size` defaults to 2 bytes, and `--fragment-delay` defaults to 0.03
seconds. TCP may combine separate writes. Actual receive fragmentation should
be established from controller observations when that distinction matters.
`--crlf` adds LF after each outgoing CR for framing-tolerance tests.

## Automation interface

- `--port 0` chooses an available port and reports it in the `listening` event.
- `--initial-power` accepts `off`, `on`, `cooling`, or `warming`.
- `--warmup-seconds` and `--cooldown-seconds` control state transition durations.
- `--idle-seconds` controls authentication and idle connection deadlines.
- `--fault-delay` controls silent holds and delayed replies.
- The listener allows up to four active connections. All share the same device
  power state and the same one-shot fault selection.
- Every event includes UTC and monotonic timestamps. Connection events have an
  incrementing `connection_id`. Command events include a per-connection number.
- `feedback_generated` records a result before its configured transmission
  behavior. `response_sent` records completion of the local socket write.
- The CLI handles both Control-C and SIGTERM and emits `server_stopped` after
  the server has closed.

Testing against this endpoint exercises program and network behavior. A physical
display test remains the step for observing actual lamp/display power, device
timing, and manufacturer-specific responses.
