# Watchdog Gate A - read-only Windows service environment

Status: implementation complete; physical validation pending.

This gate is intentionally read-only. It exists only to prove that the future
independent watchdog can run in Windows Session 0 and access the narrow hardware
paths it will need later.

## What it reads

The service performs one bounded startup probe:

1. Windows service identity, PID and Session ID;
2. exact HP 88F8 target fingerprint;
3. PawnIO plus signed LpcACPIEC.bin;
4. read-only EC control-state snapshot including 0x34/0x35 and tachometers;
5. HP BIOS/WMI GetFanLevel (command type 0x2D).

## What it does not do

Gate A does not call SetFanLevel, ReleaseFanLevelOverride,
RestoreLegacyDefault, RestoreFirmwareAuto, any EC register write, or any
automatic fan policy.

A passing Gate A cannot change the requested fan setpoint.

## Service account strategy

The first physical run uses LocalService, the least-privileged practical
built-in service identity for this experiment.

If and only if the result proves PawnIO or HP WMI access is denied under
LocalService, repeat the identical read-only gate with LocalSystem:

~~~powershell
.\scripts\test-watchdog-gate-a.ps1 -Account LocalSystem
~~~

Do not broaden PawnIO/WMI ACLs before this comparison establishes what the
target actually requires.

## Running

Use an elevated PowerShell:

~~~powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
git pull
.\scripts\test-watchdog-gate-a.ps1
~~~

The default performs three complete service start/stop cycles.

A valid PASS requires every cycle to report:

- Success = True;
- Session = 0;
- exact target matched;
- EC read succeeded;
- HP WMI GetFanLevel succeeded;
- service remained Running after its startup probe;
- service-observed setpoints stayed FF/FF;
- a final interactive EC probe still reads FF/FF, proving the Gate A service did not mutate the fixed setpoint.

The script leaves the Gate A service installed but stopped.

## Result and log locations

~~~text
%ProgramData%\VictusFanControl\Watchdog\state\gate-a.result.json
%ProgramData%\VictusFanControl\Watchdog\logs\watchdog-gate-a-YYYY-MM-DD.log
~~~

## Interpretation

LocalService PASS: retain LocalService as the preferred security context.

LocalService FAIL / LocalSystem PASS: document the exact denied resource and use
LocalSystem only with the watchdog's minimal command surface, service SID,
strict IPC ACL and no arbitrary hardware commands.

Both fail: do not implement the lease yet. Diagnose Session 0 PawnIO/WMI access
first.

See CRASH_WATCHDOG_DESIGN.md.
