# Changelog

## Unreleased — post-M9 software RC preparation

- P10: synchronize repository documentation/profile state after HP 8C40 M9 production-watchdog promotion.
- P11: add the coordinator-only adaptive production adapter with independent Manual/Automatic execution authorization, equal-only commands, no-retransmit behavior and deterministic fake-backend tests.
- P12: add HP 8C40 Candidate V1 as shadow-only / physically unvalidated.
- P13: complete the WinForms Firmware/Manual/Automatic control surface, safe 10..50 manual preference, live no-write adaptive preview, candidate visualization, and tray/status reporting while both execution gates remain closed.
- P14.1: pin the final P13 source/CI baseline and add a software-release readiness contract that keeps every physical execution gate closed.
- P14.2: centralize version 0.4.0-rc.1 and close the fixed win-x64 GUI/watchdog publish layout after static and generated-output CI verification (#1076).
- P14.3: close pinned-input deterministic RC packaging after CI #1078 produced two identical verified ZIPs (SHA-256 603ac7b2ca6816fe00002598410983ba031529ec168a70c5ec59041e5a71756c).
- P14.4: retain the RC ZIP + `.sha256` + external attestation in GitHub Actions; #1080 retained artifact 11186116708 and its downloaded wrapper matched GitHub digest eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2. No physical gate was opened.
- P14.5: formally close the final software-only RC audit from source HEAD eebcdd5e833256466c1ae023c35f7cef8d40d6ec / CI #1084 SUCCESS after downloading and verifying both retained artifacts, all 61 package-manifest payload files, and the separate PASS audit evidence; P15 remains unopened.
- P15A: formally close preparation after hardened HEAD 8779b60ee994b5fd9b34fb753c90908048974fc8 passed CI #1090. The harness consumes the exact audited P14.5 RC and proves ordinary Firmware startup with CLOSED Manual/Automatic gates, Healthy telemetry, strict FF/FF before/during/after, journal absence, expected production backend, and physically qualified M4 executable/module hashes. A later dedicated authorization commit opens P15A startup/no-write only, contingent on SUCCESS of that exact authorization HEAD; P15B and Automatic remain closed. Failed syntax-check run #1089 is preserved.
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
