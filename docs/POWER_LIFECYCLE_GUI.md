# Windows power lifecycle and tray application

Version 0.4 integrates fan authority with the Windows power lifecycle. The automatic fan policy remains OFF, but the lifecycle code is already written for a future Custom session.

## Suspend/resume detection

The WinForms application receives:

- `PBT_APMSUSPEND`
- `PBT_APMRESUMEAUTOMATIC`
- `PBT_APMRESUMESUSPEND`
- `PBT_APMRESUMECRITICAL`

A scheduling-gap detector also forces telemetry recovery when a long gap suggests a missed power notification.

Duplicate Windows resume events are coalesced. Only an **accepted** resume event advances the fan-admission freshness fence, preventing a late duplicate from permanently re-blocking admission after recovery.

## Telemetry recovery

After an accepted resume the worker:

1. waits briefly for Windows/drivers to settle;
2. reconstructs telemetry backends;
3. primes differential CPU power/load counters;
4. requires five consecutive complete snapshots;
5. tags work with a power-cycle epoch so stale work from an older cycle is discarded;
6. drains stale wake signals and waits one normal sample interval before returning to normal sampling;
7. only then transitions to `Healthy`.

The post-recovery delay was added after real hardware showed that an immediate second `GetSystemTimes` sample could legitimately have zero delta and appear as a missing CPU-load value.

## Fan authority lifecycle

When a suspend event is accepted:

```text
telemetry -> Suspended
cancel any in-flight fan acknowledgement
close Custom admission
restore FF,FF -> LegacyDefault if Custom was active
return from WM_POWERBROADCAST handler
```

On resume, admission stays closed until a telemetry snapshot newer than the power-boundary timestamp has passed recovery. An old pre-suspend SafetyGate result cannot be reused to enter Custom mode.

Normal Exit and Windows shutdown dispose the fan coordinator before stopping telemetry so a live Custom session still has a chance to return authority to HP.

## Important limit

A forced process termination cannot execute `DisposeAsync`, a `finally` block or the WinForms shutdown path. Firmware countdown/watchdog recovery must therefore be characterized separately before unattended automatic control is enabled.

## Tray behavior

Closing or minimizing the main window hides it to the notification area. The application keeps collecting telemetry. The tray reports runtime state and fan authority. Choosing **Exit** performs the coordinated shutdown path.

Run from an elevated PowerShell:

```powershell
.\scripts\run-gui.ps1
```
