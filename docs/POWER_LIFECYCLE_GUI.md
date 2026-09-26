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


## Real suspend-while-Custom hardware gate

After the integrated coordinator path passed on the physical target, an explicit
hardware-only GUI mode was added for the next lifecycle gate. It is not a fan
policy and cannot start without the exact `88F8-SUSPEND30` acknowledgement
token.

The mode waits for normal `Healthy` telemetry, passes `SafetyGate`, acquires
Custom authority through `FanControlCoordinator`, and applies one bounded
30/30 command through `Hp88F8FanControlBackend`. It then writes a local READY
marker. The PowerShell wrapper requests Windows Suspend from a separate process,
leaving the GUI message pump free to receive `WM_POWERBROADCAST/PBT_APMSUSPEND`.

Inside the real suspend handler the application records:

1. authority and EC 0x34/0x35 immediately on entry;
2. synchronous `BlockCustomAdmissionAndRestoreAsync`;
3. EC 0x34/0x35 after the restore but **before** `NotifySuspend` and before the
   window procedure returns.

A PASS therefore requires the suspend event to arrive while authority is
`Custom` and EC is 30/30, followed by authority `Firmware` and EC FF/FF
inside the same suspend handler. After wake, telemetry must recover to
`Healthy`, authority must remain `Firmware`, and EC must still be FF/FF.

Run only from the physical target:

```powershell
.\scripts\test-suspend-custom.ps1
```

Save unrelated work first: the script intentionally puts Windows to sleep after
the explicit `SUSPEND30` confirmation. Forced process termination remains a
separate gate.
