# Windows power lifecycle and tray application

The GUI/controller milestone remains **read-only**. Explicit CLI-only write-validation tools exist separately and are not invoked by the GUI.

## Suspend/resume detection

The WinForms application receives Windows `WM_POWERBROADCAST` messages:

- `PBT_APMSUSPEND`
- `PBT_APMRESUMEAUTOMATIC`
- `PBT_APMRESUMESUSPEND`
- `PBT_APMRESUMECRITICAL`

A second detector watches the telemetry scheduling interval. A gap greater than 10 seconds is treated as a possible missed resume event and forces telemetry recovery/revalidation. Duplicate Windows resume notifications are coalesced with a 10-second debounce while a real newly observed suspend always starts a new power cycle.

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
4. requires five consecutive complete snapshots after a resume (three for ordinary non-resume recovery);
5. tags work with a power-cycle epoch so stale reads/recoveries from a previous suspend/resume cycle are discarded;\n6. only then transitions to `Healthy`.

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


## Post-recovery sampling guard

A hardware test on the validated 88F8 showed that the first ordinary sample after a successful five-sample resume validation could run immediately after the last validation sample. Because Windows CPU utilization is calculated from differential `GetSystemTimes` counters, an effectively zero sampling interval can legitimately produce no CPU-load value.

The worker now drains stale wake permits from the completed power event and waits one normal sampling period before the first ordinary post-recovery read. This prevents a successful recovery from immediately degrading itself solely because two CPU-load reads occurred too close together.
