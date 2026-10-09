# Watchdog Gate A - read-only Windows service environment

Status: **physical validation completed on 2026-09-24**.

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

## Physical result

The LocalService comparison reached Session 0 and matched the exact HP target,
but failed before the EC/PawnIO probe could begin:

~~~text
System.UnauthorizedAccessException:
Access to the path 'Global\Access_EC' is denied.
~~~

The failure occurred while constructing the shared EC mutex. No EC snapshot or
HP WMI GetFanLevel call had yet completed.

The identical Gate A was then installed as LocalSystem. All three start/stop
cycles passed:

- Session ID remained 0;
- account was NT AUTHORITY\SYSTEM;
- exact HP 88F8 / SKU / BIOS fingerprint matched;
- PawnIO EC read succeeded;
- HP WMI GetFanLevel succeeded;
- EC setpoints remained 255/255 in every cycle;
- the final independent read-only EC probe still reported 255/255.

Representative observations were approximately 2200 RPM CPU / 2400 RPM GPU and
HP current fan levels 21-24 while firmware owned FF/FF.

## Account decision

Use LocalSystem for the watchdog hardware service on this validated target.

This result does **not** prove that LocalService could never access PawnIO or HP
WMI independently; the shared Global\Access_EC mutex blocked it first. We are
not changing the security descriptor of that shared cross-process mutex merely
to force LocalService compatibility. The safer current design is a very small
LocalSystem service with no ordinary fan-level API, no arbitrary EC/WMI
operations, a service SID, strict IPC ACL, and only the validated emergency
restore primitive.

See CRASH_WATCHDOG_DESIGN.md.
