# Architecture

## Design goal

The final controller should be adaptive, observable and fail-safe. Fan speed should not be a direct function of temperature alone.

```text
Hardware telemetry
       |
       v
State estimator
(temp, power, load, dT/dt)
       |
       v
Adaptive control policy
       |
       v
Safety supervisor  <---- RPM feedback / sensor validity / board allowlist
       |
       v
Fan backend
       |
       v
HP firmware / fans
```

## v0.1

Only the telemetry path exists:

```text
LibreHardwareMonitor -> TelemetrySampler -> CSV logger / console
```

There are no write-capable fan classes in v0.1.

## Planned components

### Telemetry providers

- `LibreHardwareMonitorReader`: CPU/GPU temperature, power and load.
- `Hp88F8FanTelemetryProvider`: planned read-only RPM and EC state.

### State estimator

Planned calculations:

- exponentially smoothed temperature
- `dT/dt`
- short- and medium-window CPU/GPU power
- load persistence
- fan response state

### Control policy

Planned behavior:

- low RPM during genuinely low thermal input
- anticipatory ramp when package/GPU power rises
- fast fan-up
- slow, hysteretic fan-down
- separate CPU/GPU demand combined with shared-heatsink awareness

### Safety supervisor

The safety supervisor owns the final decision. The policy may request a fan level, but the supervisor clamps or rejects it.

See `SAFETY.md`.
