# Changelog

## Unreleased — post-M9 software RC preparation

- P10: synchronize repository documentation/profile state after HP 8C40 M9 production-watchdog promotion.
- P11: add the coordinator-only adaptive production adapter with independent Manual/Automatic execution authorization, equal-only commands, no-retransmit behavior and deterministic fake-backend tests.
- P12: add HP 8C40 Candidate V1 as shadow-only / physically unvalidated.
- P13: complete the WinForms Firmware/Manual/Automatic control surface, safe 10..50 manual preference, live no-write adaptive preview, candidate visualization, and tray/status reporting while both execution gates remain closed.
- P14.1: pin the final P13 source/CI baseline and add a software-release readiness contract that keeps every physical execution gate closed.
- Keep default control and automatic/adaptive policy disabled pending separate post-M9 target-side validation.
- Add post-M9/P13 invariants that prevent accidental reopening or UI bypass of physical control gates.

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
