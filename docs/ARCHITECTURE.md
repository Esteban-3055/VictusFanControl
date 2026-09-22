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
Single shared cooling demand
       |
       v
Safety supervisor  <---- RPM feedback / sensor validity / board allowlist
       |
       v
Dual-fan backend
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

- exponentially smoothed CPU/GPU temperature
- `dT/dt`
- short- and medium-window CPU/GPU power
- load persistence
- fan response state

### Control policy

The HP 88F8 development machine uses a thermally coupled heatsink/heat-pipe assembly. The user-facing controller will therefore expose **one cooling target**, not independent CPU/GPU fan curves.

Planned behavior:

- derive one shared cooling demand from both CPU and GPU thermal/power inputs
- low RPM during genuinely low thermal input
- anticipatory ramp when CPU package or GPU power rises
- fast fan-up
- slow, hysteretic fan-down
- command both fans toward the same RPM target during ordinary operation

The implementation may still issue different low-level fan commands internally if calibration shows that this is necessary to make the two physical fans converge on the same measured RPM.

Equal requested level is not assumed to mean equal airflow or equal RPM at every operating point. RPM feedback is authoritative.

### High-load / safety exception

The common-RPM policy is an acoustic/control preference, not a safety constraint. The safety supervisor may allow one fan to exceed the common target if required by temperature, fan saturation, RPM mismatch, or another validated fail-safe condition.

### Safety supervisor

The safety supervisor owns the final decision. The policy may request a common RPM, but the supervisor clamps, overrides or rejects it.

See `SAFETY.md`.
