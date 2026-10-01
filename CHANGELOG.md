# Changelog

## Unreleased — post-M9 software RC preparation

- P10: synchronize repository documentation/profile state after HP 8C40 M9 production-watchdog promotion.
- Keep default control and automatic/adaptive policy disabled pending separate post-M9 target-side validation.
- Add a post-M9 invariant that prevents accidental reopening of consumed M9C/M9D qualification gates.

All notable project changes will be documented here.

## [Unreleased]

### Planned
- Read-only 88F8 fan RPM provider.
- Baseline analyzer for OEM fan behavior.
- Safety supervisor state machine.
- Experimental fan backend only after read-only validation.

## [0.1.0] - 2026-09-22

### Added
- Initial repository structure.
- Read-only CPU/GPU telemetry using LibreHardwareMonitor.
- CSV logging and sensor enumeration.
- HP 88F8 development profile.
- Safety, testing, hardware and roadmap documentation.
- Windows CI workflow.
