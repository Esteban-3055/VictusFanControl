# Windows power lifecycle and tray application

This milestone remains **read-only**. It adds runtime lifecycle handling before any fan-control write path exists.

## Suspend/resume detection

The WinForms application receives Windows `WM_POWERBROADCAST` messages:

- `PBT_APMSUSPEND`
- `PBT_APMRESUMEAUTOMATIC`
- `PBT_APMRESUMESUSPEND`
- `PBT_APMRESUMECRITICAL`

A second detector watches the telemetry scheduling interval. A gap greater than 10 seconds is treated as a possible missed resume event and forces telemetry recovery/revalidation.

## Runtime states

```text
Starting
Healthy
Suspending
Suspended
Resuming
Recovering
Degraded
Faulted
```

After resume, the application does not immediately declare telemetry healthy. It:

1. waits briefly for Windows/drivers to settle;
2. reconstructs the telemetry reader/backends;
3. primes differential CPU power/load counters;
4. requires three consecutive complete snapshots;
5. only then transitions to `Healthy`.

When actual fan control is implemented, only `Healthy` will be eligible for custom control. Every other state will require HP firmware authority.

## Tray behavior

Closing or minimizing the main window hides it to the notification area. The application continues collecting telemetry. The tray menu exposes the current runtime state, Open, and Exit.

The GUI displays CPU/GPU temperature, power, utilization and both fan tachometers, plus diagnostics and a power/recovery event log.

## Development run

From an elevated PowerShell at repository root:

```powershell
.\scripts\run-gui.ps1
```

Only one GUI instance is allowed per Windows session.
