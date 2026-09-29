# HP 8C40 M7 - Hibernation lifecycle qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **CODE/CI PREPARED / NO-WRITE PREFLIGHT PENDING / PHYSICAL PENDING**.

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

M7 remains physically pending until a clean hibernation run produces all of the
proof above. A code/CI PASS alone does not authorize production watchdog
promotion or automatic/adaptive fan control.
