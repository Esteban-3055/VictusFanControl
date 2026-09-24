# Architecture

## Design goal

VictusFanControl is being built as an adaptive, observable and fail-safe controller. Fan speed is not intended to be a direct temperature-only lookup.

```text
Hardware telemetry
       |
       v
Runtime state + SafetyGate
       |
       v
Future state estimator
(temp, power, load, dT/dt)
       |
       v
Future adaptive policy
       |
       v
Shared physical RPM target
       |
       v
FanControlCoordinator / safety supervisor
       |
       v
HP 88F8 backend
  EC ownership + dual tach feedback
       |
       v
Per-fan low-level compensation
       |
       v
HP firmware / fans
```

## v0.4 implemented path

Implemented today:

- PawnIO Intel MSR CPU temperature/power;
- Windows CPU utilization;
- NVML GPU temperature/power/load with target-device identity;
- ACPI EC fan RPM and control-state telemetry;
- runtime freshness/freeze/suspend recovery;
- SafetyGate;
- FanControlCoordinator authority state machine;
- hardware-validated HP WMI fan backend;
- EC fixed-setpoint acknowledgement;
- dual-tachometer command acknowledgement;
- verified `FF,FF -> LegacyDefault` restoration;
- lifecycle/exit restore integration.

Not implemented yet: the adaptive estimator/policy that decides when to acquire Custom authority and what RPM target to request. Consequently the GUI does not automatically issue fan commands.

## Shared physical-RPM policy

The HP 88F8 target has a thermally coupled heatsink assembly, so the intended user-facing control is one cooling target rather than independent CPU/GPU curves.

Hardware testing established that equal physical RPM can require unequal internal drive: near 3000 RPM the EC rate readback was roughly 75% CPU and 68% GPU. The final controller will therefore:

- derive one shared cooling demand from CPU and GPU thermal/power inputs;
- convert that demand to one physical RPM target;
- use independent feedback/compensation for each fan;
- ramp up quickly and down more slowly;
- let the safety supervisor break symmetry if a fan saturates or a thermal emergency requires it.

Physical ceiling is fan-specific (about 4330 RPM CPU and 4670 RPM GPU on the development target), so safety never caps the GPU fan merely because the CPU fan has reached its own ceiling.

## Authority rule

Adaptive policy code never writes hardware directly. It requests Custom authority and commands through `FanControlCoordinator`. Any invalid/stale telemetry, lifecycle boundary, command failure, ownership loss or tachometer failure causes rejection or HP-firmware handoff.

See `SAFETY.md` and `BACKEND_INTEGRATION_V0.4.md`.
