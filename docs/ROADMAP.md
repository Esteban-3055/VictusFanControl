# Roadmap

## v0.1 - Telemetry baseline

- [x] Repository structure
- [x] CPU temperature/power/load
- [x] NVIDIA GPU temperature/power/load
- [x] CSV logger
- [x] Sensor inventory command
- [x] 88F8 documentation
- [ ] Collect baseline sessions from the stock HP controller

## v0.2 - Read-only HP fan telemetry

- [ ] Read CPU and GPU fan RPM without fan writes
- [ ] Confirm stable 88F8 RPM source over long sessions
- [ ] Record firmware fan response versus CPU/GPU power
- [ ] Add analyzer for RPM versus temperature and power

## v0.3 - State estimator

- [ ] Temperature smoothing
- [ ] `dT/dt`
- [ ] CPU/GPU power moving averages
- [ ] Workload persistence
- [ ] Unit tests for state transitions

## v0.4 - Safety supervisor

- [ ] Board allowlist
- [ ] Sensor plausibility checks
- [ ] RPM response validation
- [ ] Thermal override
- [ ] Fallback state machine
- [ ] Fault-injection tests

## v0.5 - Experimental control backend

- [ ] HP 88F8 BIOS/WMI fan-level backend
- [ ] Hard clamp to validated board profile
- [ ] Manual test mode only
- [ ] Restore firmware control on exit

## v0.6 - Adaptive controller

- [ ] CPU/GPU power feed-forward
- [ ] Thermal feedback
- [ ] Fast ramp-up
- [ ] Hysteretic / delayed ramp-down
- [ ] Shared thermal-system logic

## v1.0 - Daily-driver candidate

Only after extended testing, crash recovery tests and thermal validation.
