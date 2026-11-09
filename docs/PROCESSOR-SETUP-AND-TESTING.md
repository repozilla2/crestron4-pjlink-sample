# Processor setup and testing

This guide explains which package belongs on which processor, where that package comes from, and how to load and test it against the included PJLink simulator. Each step is labeled with its evidence status.

- **Recorded** — performed in the November 7, 2026 RMC3 hardware run ([record](HARDWARE-VALIDATION.md)).
- **Unverified** — standard Crestron practice or a reasonable procedure that this project has not yet exercised on hardware.

## 1. Choose the right project and package

| | RMC3 (3-Series) | 4-Series (e.g. CP4N) |
| --- | --- | --- |
| Source | `lab/Rmc3PjLink/` (3 files) | `src/CrestronSample/` + `src/DisplayControl.Core/` |
| Target runtime | .NET Compact Framework 3.5, C# 3, `RMC3` symbol | `net6.0` |
| Processor package | `Rmc3PjLink.cpz` | `CrestronSample.cpz` |
| Built by `dotnet build CrestronSample.sln` | **No** | Yes, at `src/CrestronSample/bin/Release/net6.0/CrestronSample.cpz` |
| Toolchain | Licensed legacy 3-Series SIMPL# Pro tools: CF 3.5 references, Crestron 3-Series SDK assemblies, vendor sandbox validator, signer and packager (Windows) | .NET 10 SDK + `Crestron.SimplSharp.SDK.Program` 2.22.40 from NuGet |
| Package in this repository | None | None (`*.cpz` is git-ignored) |
| Startup message | `RMC3 PJLink sample 0.1 loaded.` | `PJLink sample loaded. Configure pjlink-host.txt before connecting.` |
| `pjlink-host.txt` rule | Private IPv4 only (10/8, 172.16/12, 192.168/16) | Any dotted unicast IPv4 |
| Hardware evidence | 20/20 checks passed against the simulator (RMC3, PUF `1.8001.0298`) | **None.** Desktop tests only |

The `dotnet run` test projects under `tests/` and `lab/Rmc3PjLink.Tests/` are **desktop executables**. They compile the same sources against desktop clock, file, MD5 and SDK test doubles. They produce no processor package and cannot be loaded onto any processor.

`CrestronSample.cpz` will not run on an RMC3, because 3-Series firmware runs Compact Framework programs, not .NET 6. `Rmc3PjLink.cpz` is not a 4-Series package.

## 2. Obtain the RMC3 package

The hardware-tested package is **not distributed** with this repository. It bundles seven Crestron SDK files (`SimplSharpPro.exe`, `SimplSharpHelperInterface.dll`, `SimplSharpCustomAttributesInterface.dll`, `SimplSharpCryptographyInterface.dll`, `Crestron.SimplSharpPro.DeviceSupport.dll`, `SimplSharpData.dat`, `SimplSharpData.dat.der`). Crestron's Software Development Tools License Agreement allows redistribution only of binary libraries that the tools generate during a build, or that are explicitly labeled redistributable (section 3.4). It forbids distributing the tools or any portion of them through the Internet (section 4.1(a)). [Crestron SDK terms](https://www.crestron.com/Legal/software-license-agreement/Software-Development-Tools-License-Agreement)

Hardware-tested identity, for comparison only:

| Item | Value |
| --- | --- |
| File | `Rmc3PjLink.cpz`, 2,715,107 bytes, 11 entries |
| SHA-256 | `2e8422466e252ace2f93b4405d23a219e35c28e52567e620565e376cbfa6c54d` |
| Signed `Rmc3PjLink.dll` SHA-256 | `8c6120916759780ba4bf208cec138a64a1f3edd94a2270dbed537718e45410b6` |
| Unsigned `Rmc3PjLink.dll` | 22,016 bytes, SHA-256 `9ff3061b7683e988dfbcebb18b063c8a107dda0c44746118ac74ba39a2e1a1d0` |

### Rebuild with your own licensed tools

You need a Crestron Underlying Agreement (dealer, service provider, authorized independent programmer or partner) and registered access to the legacy 3-Series SIMPL# Pro development tools.

**Recorded build.** The tested package came from a private build workspace that is not public. Its compile step ran Roslyn `csc` from .NET SDK 10.0.403 with these options:

```text
-nologo -noconfig -nostdlib+ -langversion:3 -target:library -optimize+ -debug-
-warn:4 -warnaserror+ -deterministic+ -define:RMC3
-reference: mscorlib.dll System.dll System.Core.dll System.Data.dll        (Windows CE Compact Framework 3.5 reference set)
-reference: SimplSharpHelperInterface.dll SimplSharpPro.exe
            Crestron.SimplSharpPro.DeviceSupport.dll
            SimplSharpCustomAttributesInterface.dll
            SimplSharpCryptographyInterface.dll                            (legacy Crestron 3-Series SDK)
PjLinkSession.cs SampleConfiguration.cs ControlSystem.cs  ->  Rmc3PjLink.dll
```

A Windows wrapper then ran Crestron's sandbox validator, signer and package creator on that DLL, and wrote `ProgramInfo.config` itself. The wrapper is not published. With the same reference binaries, this compile reproduces the unsigned DLL hash above.

**Standard route (unverified for this project).** In Crestron's supported 3-Series environment (Visual Studio 2008 with the SIMPL# Pro plug-in), create a SIMPL# Pro project named `Rmc3PjLink`. Add the three files from `lab/Rmc3PjLink/` and define the `RMC3` conditional-compilation symbol. Then build to produce a validated, signed CPZ. Report any validator error with its exact text.

Every rebuild yields a **new artifact**. Signing and `CompiledOn` metadata change the CPZ hash, so a rebuilt package cannot inherit the hardware result above. Record your own package hash and repeat the tests in section 5.

## 3. Prepare the Mac and simulator (Recorded)

Requirements: macOS with Python 3.9 or later, OpenSSH (`ssh`, `sftp`), and `shasum`. From the repository root, run the following.

```bash
python3 -m unittest discover -s lab/VirtualPjLink -p test_virtual_pjlink.py -v   # expect 36 tests OK
shasum -a 256 lab/VirtualPjLink/virtual_pjlink.py
```

The hardware run used simulator SHA-256 `71a53b95a0f7ba0d144e4a8fc589f6b4b4a2c9d1c093e7e1880c8e26e8968c95`.

Find the Mac's address on the processor's network.

```bash
route -n get PROCESSOR_IP          # note the "interface:" line, e.g. en0
ipconfig getifaddr INTERFACE       # this is MAC_LAN_IP
```

## 4. Access and identify the processor

1. **Discover the processor (Unverified).** Use Crestron Toolbox Device Discovery, or the address assigned by your DHCP server. The recorded run used a known address and verified its identity after connecting.
2. **Connect (Recorded).** Open an SSH console and SFTP session with a processor administrator account. Enter the password interactively, never in a script or log.

   ```bash
   ssh ADMIN_USER@PROCESSOR_IP
   sftp ADMIN_USER@PROCESSOR_IP
   ```

   Accept the host key only after you have checked it, and treat a changed key as a stop condition. The console may need one Return before the `RMC3>` prompt appears.
3. **Identify (Recorded).** Record the output of each command.

   | Command | Purpose |
   | --- | --- |
   | `ver -v` | Model, firmware/OS, PUF |
   | `ipconfig /all` | Address and MAC |
   | `progregister` | Programs registered per slot |
   | `progcomments:1` | Slot 1 program identity |
   | `proguptime:1` | Slot 1 running state |
   | `err plogcurrent` | Baseline error log |
   | `timedate` | Processor clock |

   The recorded run used firmware PUF `1.8001.0298`, OS `1.8001.6192.21873`. Other firmware is untested. On this firmware, `help <command>` was rejected and `<command> ?` gave the usage text.
4. **Select the slot.** Choose a slot whose current program you own, or an empty slot. The commands below use slot 1. Before replacing anything, confirm that the slot's program is stopped and is yours, and check for a collision: `pjlink status` must return `Bad or Incomplete Command`.

## 5. Back up, transfer and load (RMC3, Recorded)

On the recorded RMC3, SFTP path `/Program01` is slot 1. It corresponds to console path `\SIMPL\app01` and is the program's application directory. Confirm it on your device with `pwd`, `ls -la /`, and `ls -la /Program01` before transferring anything.

1. **Back up.** Copy every file in the slot directory to a private local directory, then compare file counts and sizes.

   ```text
   sftp> lcd /path/to/private/backup
   sftp> get -p /Program01/*
   ```

   Keep the slot's original CPZ, if you have it, as the recovery package.
2. **Stop the current program.** Run `stopprogram -p:1` and wait for `Program Stopped:1`.
3. **Transfer and verify.**

   ```text
   sftp> put Rmc3PjLink.cpz /Program01/Rmc3PjLink.cpz
   sftp> get /Program01/Rmc3PjLink.cpz Rmc3PjLink.uploaded.cpz
   ```

   Run `shasum -a 256` on both local files. Do not load if they differ.
4. **Load once.** Run `progload -p:1 -n`. The `-n` option leaves the IP table unchanged. Allow up to 180 seconds. Expect loader output naming `Rmc3PjLink.dll` and exactly one `RMC3 PJLink sample 0.1 loaded.`. Then `progcomments:1` should identify `Rmc3PjLink`. The loader consumes the staged CPZ, and the slot keeps the extracted files.
5. **Stage runtime files.** Create a host file containing `MAC_LAN_IP` with a trailing newline, and optionally a password file containing 1–32 ASCII letters or digits.

   ```text
   sftp> put pjlink-host.txt /Program01/pjlink-host.txt
   sftp> put pjlink-password.txt /Program01/pjlink-password.txt
   ```

   Download both files again and compare them byte for byte. Keep the password file private, and never commit it or paste it into logs.
6. **Restart to read the password.** The program reads the password only at startup. Run `stopprogram -p:1`, then `progreset -p:1`, and wait for a fresh startup message. An invalid password file stops startup with `PJLink configuration could not be loaded.`. Fix the file and restart again.

### 4-Series (Unverified)

Use firmware with .NET 6 program support. CP4N release notes give `2.8003.00037` or later. Load `CrestronSample.cpz` with Crestron Toolbox, or with SFTP and `progload`. Find the slot's application directory on your device, because this project has not recorded the 4-Series path. Stage `pjlink-host.txt` (and optionally `pjlink-password.txt`) there, then restart the program. Follow sections 4–6 and record every observation as a first-run result.

## 6. Run the test sequence

Start the simulator on the Mac. Use ten-second transitions so the polling steps below are practical. The default is three seconds.

```bash
python3 lab/VirtualPjLink/virtual_pjlink.py \
  --bind MAC_LAN_IP --port 4352 --allow-client PROCESSOR_IP \
  --warmup-seconds 10 --cooldown-seconds 10 \
  --log /path/to/new-results/sim-normal.jsonl
```

If you created `pjlink-password.txt`, add `--password-file /absolute/path/to/pjlink-password.txt`. Otherwise, add nothing. Wait for the `listening` event. If macOS asks, allow incoming connections for that Python process.

Issue each command only after the previous command's result appears. Results are printed asynchronously after the prompt.

| Console command | Expected output (RMC3, Recorded) |
| --- | --- |
| `pjlink connect` | `PJLink ready. Use pjlink query to read power feedback.` |
| `pjlink query` | `PJLink QueryPower returned 0.` (Off) |
| `pjlink on` | `PJLink PowerOn returned OK.` |
| `pjlink status` | `Socket <client status>. Ready True. Pending False. Power Off.` (still Off until a query) |
| `pjlink query` (warming) | `PJLink QueryPower returned 3.` |
| `pjlink query` (after ~10 s) | `PJLink QueryPower returned 1.` (On) |
| `pjlink off` | `PJLink PowerOff returned OK.` |
| `pjlink query` (cooling) | `PJLink QueryPower returned 2.` |
| `pjlink query` (after ~10 s) | `PJLink QueryPower returned 0.` (Off) |
| `pjlink disconnect` | `Disconnect requested.`, then `pjlink status` gives `Socket disconnected. Ready False. Pending False. Power Unknown.` |

The simulator closes connections that are idle for 30 seconds by default. If the session closes, reconnect explicitly with `pjlink connect`.

### Fault cases

Start a fresh simulator process for each case with `--scenario NAME --idle-seconds 120 --fault-delay 35`. Each fault fires once, and recovery is an explicit `pjlink connect` to the **same** simulator process.

| Scenario | Expected processor behavior |
| --- | --- |
| `device-error` | `PJLink QueryPower returned ERR4.`, session stays ready |
| `no-greeting` | `Timed out waiting for the PJLink greeting.` after about 30 s |
| `no-response` | `Timed out waiting for the PJLink command response.`; a second query while pending prints `The session must be ready with no command pending. Use pjlink status.` |
| `late-response` | Response timeout; a new connection works after the old reply arrives |
| `disconnect` | `PJLink connection closed.` diagnostic, cleared state |
| `reject-auth` (needs password) | `PJLink authentication was rejected.`; next connection authenticates |
| `non-ascii` | Session closed on byte 0x80; reconnect succeeds |

For a wrong-password test, run a `normal` simulator with a different password file. Expect rejection on the first command.

### Lifecycle

Run `stopprogram -p:1`. `pjlink` should now return `Bad or Incomplete Command`. Run `progreset -p:1` and expect one fresh startup message and no automatic connection. Then reconnect explicitly and query. Compare `err plogcurrent` with the baseline and account for each new entry.

## 7. Troubleshooting

| Symptom | Check |
| --- | --- |
| `pjlink` unknown | Program stopped or failed to start: `progcomments:1`, `proguptime:1`, `err plogcurrent` |
| `PJLink configuration could not be loaded.` | Password file too long, not alphanumeric, or over 64 characters including CR/LF |
| Host rejected on connect | RMC3 accepts only canonical private IPv4: no leading zeros, spaces, or hostnames, and a single line |
| Greeting timeout with simulator running | Wrong `--bind` address, `--allow-client` mismatch, macOS firewall prompt declined, or wrong `pjlink-host.txt` |
| `PJLINK ERRA` / authentication rejected | Simulator and processor passwords differ. Restart the program after changing the file |
| Load fails with a dependency or signature error | Package not built for 3-Series, or not validated and signed by the vendor tools |

## 8. Recovery

1. Stop the test program with `stopprogram -p:1`.
2. Upload the backed-up original CPZ to the slot directory and run `progload -p:1 -n`. If you have no CPZ, restore the backed-up files and their original program from its source project.
3. Delete `pjlink-host.txt` and `pjlink-password.txt` from the slot if they are no longer needed.
4. Stop the simulator with Control-C, which should log `server_stopped`, and close the SSH and SFTP sessions.

Firmware changes, factory resets, and processor reboots are outside this procedure.
