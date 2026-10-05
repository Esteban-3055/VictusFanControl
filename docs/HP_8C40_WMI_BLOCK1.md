# HP 8C40 WMI Block 1: combined control and integration qualification

This gate is an explicit physical test, not promotion of normal Automatic.
Target: HP 8C40 63.43 / F.18 / 9D0R1LA / i7-13700H / RTX 4060 Laptop.
Fan setters, RPM and release remain WMI-only; direct EC stays prohibited.
CPU sensing/limits use MSR/PawnIO; GPU uses NVML. GPU ActiveUnverified continues
to mean an accepted Set, not independent locked-range readback.

## Run

Close other controller instances, keep AC connected and avoid suspend or settings
changes during the sequence. From elevated PowerShell in the repository:

```powershell
git switch feature/victus-8c40-automatic-final-qualification
git pull --ff-only
.\scripts\test-wmi-block1.ps1
```

The launcher refreshes upstream, checks exact target, clean tracked source (only
untracked logs allowed), HEAD == upstream, successful build/cpu-rapl/WMI CI for
that exact HEAD, no conflicting controllers and no pending fan/performance
journals. It then builds once and opens the real GUI with an explicit token and
unique evidence directory. Startup remains Firmware; neither domain autoapplies.

Follow the console, not the previous A1 instructions:

1. Apply CPU AC 20/40 W and GPU 210..1850 MHz. READY requires active domains on AC
   and three unique fresh healthy low-load samples. The current fan configuration
   is bound to the evidence and cannot change during the test.
2. Manual: select once, Apply 40 once. This creates session A / Guardian A.
3. Automatic: select once, wait for an accepted changed target. The sequence
   records at least one real changed-target decision, not just a held level.
4. Manual: select once, then use the exact different target printed by the console
   (31, or 32 if the last accepted Automatic target is 31). Apply once.
5. Firmware: select once. Only this action releases A.
6. Manual: select once, Apply 31 once without restarting the GUI. This creates B
   with a distinct directory and process identity.
7. With B still Custom, choose tray Exit. Normal shutdown releases fans first,
   shuts down Performance Guardian and stops telemetry.

One ZIP and SHA256 contain separate handoff/rearm/close outcomes, ordered GUI
JSONL events, same-HEAD CI and source metadata, configuration, both guardian
sessions, their native-dispatch JSONL, and the independent performance report.
The aggregate PASS additionally requires exit code zero and journal absence.

Each native audit requires all normal targets to precede exactly one FF/FF then
LegacyDefault release tail. Session A must include Manual 40, a changed Automatic
command and changed Manual return; B must include 31. Both reports must be
CLIENT_RELEASE, accepted release/default, retired lease, Failure=null and
IndependentFirmwareOwnershipVerified=false. Only A's explicit Firmware and B's
tray shutdown may transition through Restoring (two total).

Firmware remains an unconditional escape. An early escape releases but fails the
sequence. Unexpected clicks, loss of admission, limits/source loss, suspend,
uncertain native completion or recovery close the gate. GUI timeout/abort fences
new commands and requests ordinary recovery; the harness never kills a Guardian
or deletes its lease. A retained lease is not proof of restored Firmware and
must not be removed to bypass recovery. Reports may be incomplete on failure;
the raw evidence is retained. Per-action timeout is 240 seconds (30..300 allowed),
and the GUI has a total 16-minute gate deadline.

## Software verification and fixes

- Mode handoffs retain the actual previously accepted target in the planner and
  Manual deduplication state. A same-level handoff cannot invent a fresh session
  or retransmission. Policy EMA/confirmation state still resets for the new mode.
- Faulted authority is checked before same-mode shortcuts. Firmware + Faulted
  returns an explicit blocked recovery state without blind retry.
- WMI restore log messages describe accepted requests and unverified ownership.
- Core tests cover combined order, ranges, changed commands, early Firmware,
  premature Exit, in-flight operations, safety races and retained-target holds.
- The launcher self-test checks startup isolation plus native-order and report
  identity fixtures under PowerShell 7 and 5.1 in Windows CI, without hardware.
- Historical A1 remains isolated; normal Automatic remains closed.

Physical Block1 has not been executed by Codex. This gate does not qualify
telemetry-loss injection, suspend/resume, AC/Battery transitions, sustained
20-minute load or daily automatic startup/recovery. Those remain later blocks.
