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
- P15A: formally close preparation after hardened HEAD 8779b60ee994b5fd9b34fb753c90908048974fc8 passed CI #1090. Authorization HEAD 8e91c649056ba868ec94aa1864c7669aa90f4550 passed CI #1092, but the first target-side attempt failed closed before service/EC/GUI/fan execution because the harness did not descend into the deterministic RC's single package-root directory. Authorization was immediately re-closed; correction HEAD 2a56913030c403637618e98112eaf03accde2b31 added a tested package-root resolver and passed full CI #1093. Correction closure HEAD 1b4c5f86e36bc756c3a2e404b52e04e08c1528cc passed CI #1094; fresh authorization HEAD 6c43550001c44a04ad33ec96f8400e363d6c0ad8 passed CI #1095. Its target-side P15A attempt reached Healthy/Firmware and completed the required pre/during FF/FF no-journal checks, then failed closed because the operator confirmation token was not accepted; authorization was immediately re-closed and post-exit proof remains unclaimed. The preserved ZIP (SHA-256 fee6d9c0...) was independently reviewed: all seven embedded evidence-file hashes/lengths match its manifest, with 2 pre-start + 5 runtime FF/FF samples and no journal/Manual/Automatic evidence. The operator checkpoint is hardened to three bounded exact-token attempts; implementation HEAD a714767225f558b3f8934dffcc575e49ad72127b passed full CI #1097; closure HEAD bb84846a57f2e81cab68fe7f1c89399af92094e7 passed CI #1098, and P15A was then freshly authorized from that closed baseline with same-HEAD CI still required. Authorization HEAD 2189402b0d9d96948e44ec3940ad77d17a82b451 passed CI #1099 and subsequently produced a complete target-side P15A PASS. Evidence ZIP cd8e7dfe2fef365e75b886ccca1c6c101ce12030754f21a8f9bc7f305f76c24e independently verifies exact target/RC identity, 2+5+2 FF/FF samples, healthy Firmware startup, stable watchdog identity/integrity, no journal/lease/Manual/Automatic evidence, normal tray Exit and post-exit FF/FF proof. P15A is formally closed and re-blocked; P15B and Automatic remain closed. Failed syntax-check run #1089 is preserved.
- P15B preparation: add a hard-blocked one-shot Manual 30/30 qualification controller and parent harness using the promoted production watchdog factory, FanControlCoordinator and AdaptiveFanProductionController Manual path. The design permits one equal ApplyManualAsync(30), verifies OWNED journal/PID/30-30 evidence, performs three bounded safety-supervision frames, and requires strong FF/FF -> LegacyDefault -> stable FF/FF -> watchdog RELEASE -> journal-absent closure. A delayed independent failsafe, native child ExitCode tracking, evidence packaging and CI invariants are included. Failed software-only CI #1101 (P15A invariant did not yet accept P15B states) and #1102 (PowerShell invariant literal expansion) are preserved; corrected implementation HEAD 111d1e7a817c2468a4d6196447ec335d57c59d65 passed full CI #1103. P15B preparation is formally closed; closure HEAD 0aee1b32f1bc063b31083e42c826e0eb87d35546 passed CI #1104, after which a fresh commit opens only the dedicated P15B harness/controller barriers with same-HEAD CI still required. Initial authorization CI #1105 failed software-only because the closed P15A invariant still forbade the new dedicated P15B gate; that cross-milestone assertion was corrected without hardware execution and the failed run is preserved. Authorization HEAD 6fe617693d4de54bfc709e07728c825f2246072d passed CI #1106, but the first target-side run failed closed before the operator token because P15A had intentionally left the exact qualified M4 watchdog Manual/Running/PID7980 while the original P15B harness required Manual/Stopped/PID0. Journal was absent and no P15B physical operation was reached. P15B was immediately re-blocked. The service-baseline correction now accepts either exact qualified Stopped/PID0 or inherited exact qualified Running/Ready/no-journal, revalidates stable PID/start identity around the operator token, reuses the inherited watchdog without restarting it, and restores/preserves the initial service state after strong restore or fail-closed cleanup. A pure resolver and synthetic PowerShell self-test cover both valid states and rejection cases. Physical P15B remains closed pending CI; user-facing Manual and Automatic remain closed.
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
