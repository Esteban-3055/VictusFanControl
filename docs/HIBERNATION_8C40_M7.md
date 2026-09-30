# HP 8C40 M7 - Hibernation lifecycle qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **CODE/CI PASS / NO-WRITE PREFLIGHT PASS / PHYSICAL PASS**.

M6 has physically closed the display-aware Modern Standby path. M7 is a
separate gate for Windows hibernation. It does not enable automatic/adaptive
fan policy and it does not set `WatchdogRecoveryValidated=true`.

## Why hibernation is separate

Hibernation preserves the Windows kernel/process image across a deeper power
transition than Modern Standby. The safety question is whether a watchdog-owned
Custom 30/30 session is released to HP firmware before hibernation, whether the
durable lease remains clean, and whether the exact GUI/watchdog process
identities resume without stale Custom admission.

M7 deliberately reuses the already-hardened M6 display-aware lifecycle engine:
SESSION_DISPLAY_STATUS Off closes Custom admission and suspends telemetry,
registered PBT_APMSUSPEND is the synchronous completion barrier, and
SESSION_DISPLAY_STATUS On is the only accepted telemetry resume boundary.
The dedicated M7 app mode changes the transition identity and removes the
M6-only requirement that a maintenance PBT resume be observed while the display
is still Off. Windows-level hibernation causality is instead proven by the M7
parent harness.

## Required physical proof

Before hibernation:

- exact HP 8C40 / 9D0R1LA / BIOS F.18 target;
- light-load Healthy telemetry;
- real WMI + EC + dual-tach 30/30 acknowledgement;
- durable schema-v2 generation-3 OWNED 30/30 lease;
- exact GUI PID + creation time and exact LocalSystem watchdog PID + creation time;
- SESSION_DISPLAY_STATUS Off observed while Custom;
- telemetry admission fenced and hardware reads quiesced;
- registered PBT_APMSUSPEND completes verified Firmware restore;
- local FF/FF acknowledgement, watchdog Release, journal absent and stable FF/FF.

Transition proof:

- the versioned harness invokes `shutdown.exe /h` only after explicit operator
  confirmation and after the protected READY state exists;
- Kernel-Power evidence newer than the armed EventRecordID boundary must contain
  a non-battery event 42 followed by event 507 whose message reports hibernate;
- Kernel-Power 524 or a battery-triggered event 42 invalidates the run;
- the hibernation window must be at least 15 seconds.

After resume:

- same GUI PID + creation time;
- same watchdog PID + creation time;
- SESSION_DISPLAY_STATUS On accepted exactly once;
- Firmware + stable FF/FF + journal absent before Custom admission reopens;
- five fresh snapshots recover telemetry to Healthy;
- one controlled watchdog-backed 30/30 re-entry;
- final Firmware restore with watchdog Release, journal absent and stable FF/FF;
- delayed emergency fallback never takes over;
- M4 service returns to Manual/stopped.

## Operator workflow

First run only the no-write preflight:

~~~powershell
git pull
.\scripts\test-8c40-hibernation-m7-preflight.ps1
~~~

The preflight performs no fan write, no watchdog lease, no service mutation and
no hibernation transition. It checks the exact target, Windows hibernation
availability, battery baseline, code/build/self-tests, absence of a retained
journal and stable read-only FF/FF.

Only after that preflight is physically PASS should the write-capable M7
qualification be run:

~~~powershell
.\scripts\test-8c40-hibernation-m7.ps1
~~~

The physical script uses the same bounded 30/30 + delayed-fallback safety model
as M6. After READY and a final explicit prompt, the script itself executes
`shutdown.exe /h`; there is no separate one-off PowerShell power command.

## Gate status

M7 is physically closed on the exact target. This closes the hibernation
lifecycle gate, but does not by itself authorize unattended/adaptive control.
Representative-load, thermal-emergency and any remaining production-race
validation stay separate. `WatchdogRecoveryValidated` remains false and
automatic/adaptive fan control remains OFF.

## Code/CI preparation result

The M7 preparation baseline `f9960cbcf59911077c4ead8bc4e2986d1d18d374`
passed GitHub Actions **#679** (run `36647577868`) on 2026-09-29.

The green run includes PowerShell syntax validation, the existing M5/M6
regressions, both M7 source/harness invariant suites under PowerShell 7 and
Windows PowerShell 5.1, warnings-as-errors solution build, Modern Standby M0,
watchdog Gate B/C and 8C40 M2/M3/M4 self-tests, SafetyGate,
FanControlCoordinator, BIOS-contract and HP backend self-tests.

That preparation baseline is retained as historical code/CI evidence. The
subsequent no-write preflight and physical hibernation qualification both
passed, as documented below. `WatchdogRecoveryValidated` remains false because
representative-load, thermal-emergency and any remaining production-race gates
are still separate; automatic/adaptive policy remains OFF.

## No-write preflight result

The exact target completed the local M7 no-write preflight successfully on
2026-09-29 from repository head
`fa8d6bc1324bc8146735ebfccb8eeaaf4d6c86b5`.

The preflight confirmed HP 8C40 / board 63.43 / SKU 9D0R1LA#AKH / BIOS F.18,
Windows hibernation availability, Modern Standby S0 Low Power Idle, no legacy
S3, 100% reported battery charge, no active power requests, M4 Manual/stopped,
no retained watchdog journal, warnings-as-errors build success, the complete
M5/M6/M7 synthetic regression set, and two consecutive independent FF/FF
samples.

No fan write, watchdog lease, service mutation or hibernation transition was
performed by that stage. It authorized the subsequent write-capable physical
qualification, which has now passed and is documented below.
`WatchdogRecoveryValidated` remains false and automatic/adaptive policy remains
OFF.

## Physical hibernation result

**PASS — 2026-09-29 — repository head
`0a916adc4adfda58076fd093031f55c7430cf10a`.**

The write-capable M7 harness reached real watchdog-backed Custom 30/30 with a
durable OWNED generation-3 lease. Watchdog PID 21968
(start ticks `639263244753105927`) and GUI PID 27320
(start ticks `639263244891936226`) were bound into the proof.

The proactive lifecycle handoff was causally ordered:

- READY at 21:21:40.3047081 -03:00 in Custom 30/30;
- primary SESSION_DISPLAY_STATUS Off at 00:21:45.6820512Z;
- registered PBT_APMSUSPEND completed restore at
  21:21:46.5032823 -03:00;
- restore duration 689.5 ms;
- Firmware authority, local FF/FF, watchdog Release and journal absence were
  all established before the OS hibernation event.

Windows then recorded Kernel-Power 42 at 21:21:47.012924 -03:00 with
Application API causality, followed by Kernel-Power 507 at
21:22:19.8585117 -03:00 with `Resume from Hibernate`. The measured
hibernation evidence window was 32.8 s. The system power report independently
classified Session 70 as type Hibernate, TargetState=5, EffectiveState=5,
requested by `shutdown.exe`, on AC, with 69546 mWh remaining both at entry and
exit. No battery-triggered event or Kernel-Power 524 occurred inside the armed
test window.

After resume, SESSION_DISPLAY_STATUS On was accepted exactly once at
21:22:20.1734446 -03:00 while authority was still Firmware, EC was FF/FF and
the journal was absent. Five fresh Healthy snapshots completed recovery before
one controlled watchdog-backed 30/30 re-entry. The final Release succeeded,
independent and post-test EC probes each produced two consecutive FF/FF samples,
the delayed 300 s fallback never took over, and M4 returned to Manual/stopped.

This is accepted as the physical M7 PASS. M6 does not need to be repeated and
M7 does not need to be repeated merely for documentation.