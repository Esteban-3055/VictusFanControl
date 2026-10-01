# P15 — first target-side checkpoint

P15 starts only after P14.5 software-RC closure. It is the first post-P13 target-side validation and is deliberately split so that no two physical gates are open at the same time.

Exact target: **HP Victus 15-fa1xxx / HP 8C40 rev. 63.43 / SKU 9D0R1LA#AKH / BIOS F.18**.

## Non-negotiable sequence

1. P15A — ordinary startup / no-write physical checkpoint.
2. Formally close P15A evidence and re-block its execution authorization.
3. Only then prepare and separately authorize P15B — one-shot Manual equal 30/30 with strong restore.
4. Only after P15B closure may later lifecycle/Automatic physical work be considered.

Manual and Automatic user-facing execution gates remain compile-time false during P15A. Candidate V1 remains physically unvalidated and production-unauthorized. M9C/M9D qualification gates remain closed.

## P15A — startup / no-write

P15A validates the **exact audited P14.5 RC**, not an ad-hoc rebuild. The required GitHub artifact is the RC retained by Actions #1084 from source HEAD `eebcdd5e833256466c1ae023c35f7cef8d40d6ec`. The outer artifact wrapper SHA-256 is `588058a8b57c0ca1bb41649288682e0981be845ab747654472d3d10c88d572b5`; its inner RC ZIP SHA-256 is `704983caa20abb21c3520ffbd165ad69f9843139b6b2fa47d9e9e2448a32ef68`.

The physical harness launches `app\VictusFanControl.App.exe` from that verified RC using only `--modules-dir`. It does **not** use any legacy hardware-test mode. Normal post-M9 startup is expected to start the already-installed Manual M4 watchdog service and construct the production backend, but Manual/Automatic control remains closed, no watchdog lease ownership may be acquired, the durable journal must remain absent, and EC setpoint reads must remain firmware-owned `FF/FF` before, during and after the GUI session.

The test requires: exact target fingerprint; local HEAD equal to upstream on the dedicated P15 branch; clean committed source except preserved untracked `logs/`; initial M4 baseline Manual/Stopped/PID0/LocalSystem; exact installed watchdog executable/module hashes matching the physically qualified M9B/M9C evidence (`ec10242...` / `c38fd116...`); expected service command-line contract; ordinary GUI startup; production watchdog-backed 8C40 backend selection; startup mode Firmware; Manual gate false; Automatic gate false; telemetry recovery to Healthy after three complete snapshots; two strict `FF/FF` observations before startup, five strict `FF/FF` observations while the GUI is running, normal tray Exit, two strict final `FF/FF` observations, unchanged watchdog process identity while running, and journal absence throughout.

The operator must not select Manual or Automatic during P15A. The harness itself contains no fan command, no firmware-restore command, no service-control command, no watchdog lease acquisition, no power transition and no `git clean`. Normal GUI startup is allowed to start the already-installed Manual watchdog service; the harness never starts/stops/reconfigures it directly. Evidence records every successful EC setpoint read, any detected journal/non-firmware evidence, service command line/process identity and binary/module hashes instead of pre-claiming absence for observations that were not made.

Current state: **P15A remains formally closed/re-blocked. The P15B inherited-running watchdog correction has passed full CI and is formally closed, with both P15B physical gates still CLOSED pending a fresh separate authorization.**

### P15A preparation closure

Final hardened preparation HEAD: `8779b60ee994b5fd9b34fb753c90908048974fc8`.

GitHub Actions validation: **#1090**, run ID `36916449876`, **SUCCESS**. The full branch workflow passed PowerShell syntax validation, all historical M5-M9/P10-P14 invariants, the P15A static invariant, P15A evidence-packaging self-test, Windows PowerShell 5.1 compatibility, warnings-as-errors build and the existing RC packaging/artifact audit path. This run performed no target-side physical execution.

The earlier hardening commit triggered Actions **#1089** (run ID `36916292385`) and failed at the global PowerShell syntax check because one diagnostic string used `$required:`, which PowerShell parses as an invalid drive-qualified variable reference. The interpolation boundary was corrected to `${required}:`; no later CI step and no hardware operation ran in #1089. The failure is retained as evidence.

P15A preparation is formally closed. A separate authorization commit now sets only `startupNoWrite.executionAuthorized=true`. That authorization is based on preparation closure HEAD `1ea5d081422aced1acc3941e2110ba16289cd791` / CI #1091 SUCCESS and still requires a full **SUCCESS on the authorization HEAD itself before any physical execution**. P15B remains unimplemented/closed, and Manual/Automatic user-facing gates remain false.

### P15A payload-layout correction

The first authorized P15A attempt on HEAD `8e91c649056ba868ec94aa1864c7669aa90f4550` / CI #1092 SUCCESS failed closed after the exact P14.5 artifact and inner package hashes had already verified. The harness expanded the deterministic inner RC ZIP correctly, but then looked for `app\VictusFanControl.App.exe` directly under the extraction directory. P14.3 packages intentionally contain one top-level package root named `VictusFanControl-0.4.0-rc.1-win-x64`, so the real GUI is one level deeper.

The failure occurred before the service baseline check, before any EC setpoint probe, before GUI launch and before any fan-control command, firmware restore or watchdog lease acquisition. Because the failure occurred before the harness's evidence-packaging `try/finally`, no P15A evidence ZIP was produced; that absence is recorded rather than reconstructed.

The correction introduces a dedicated no-hardware payload resolver that requires exactly one package root matching the inner ZIP basename and verifies `PACKAGE-MANIFEST.json`, the GUI executable and both app PawnIO modules beneath that root. A CI self-test constructs the same nested ZIP shape so this exact integration mistake cannot silently recur. The correction implementation HEAD `2a56913030c403637618e98112eaf03accde2b31` passed the full workflow in **#1093**, run ID `36922825446`, including the new payload-layout self-test, Windows PowerShell 5.1 compatibility and warnings-as-errors build. No target-side hardware execution occurred in that CI run. P15A execution authorization was then reopened in a fresh, separate commit based on closure HEAD `1b4c5f86e36bc756c3a2e404b52e04e08c1528cc` / CI #1094 SUCCESS. As before, no target-side execution is permitted until that fresh authorization commit itself has a successful same-HEAD workflow.

### Latest P15A physical attempt

Authorization HEAD `6c43550001c44a04ad33ec96f8400e363d6c0ad8` passed full CI #1095 and was executed on the exact target. The audited wrapper and inner RC payload verified, the corrected package-root resolver succeeded, the initial watchdog baseline/integrity passed, the pre-start journal was absent, the read-only EC proof remained Firmware, and the normal GUI reached Healthy with Firmware mode plus CLOSED Manual/Automatic gates. The required runtime FF/FF samples and no-journal checks also passed.

The attempt then failed closed at the explicit operator checkpoint because `Read-Host` did not receive the exact token `P15A-OBSERVED`. Consequently the harness did not observe the requested tray Exit and did not execute the post-exit FF/FF proof. No P15A PASS is claimed. P15A execution authorization was re-closed immediately.

The preserved evidence ZIP was independently reviewed at SHA-256 `fee6d9c00eab589c3c22f29de764d86e22475860caa5793c2cd5e454f3b23613`. Its embedded package manifest SHA-256 is `ed3ed2ba2d4d30a5df6c2a453fa6601b8a999890206838dd27eb284d944e21ce`; all seven evidence files represented inside the ZIP match their manifest hashes/lengths. The package binds source HEAD `6c43550001c44a04ad33ec96f8400e363d6c0ad8`, the exact target, exact audited RC identities, 2 pre-start + 5 runtime FF/FF samples, no journal evidence and no Manual/Automatic request. It contains no post-exit samples and therefore cannot support P15A PASS.

### P15A operator-confirmation hardening

A single non-matching or empty console response should not discard an otherwise healthy no-write run immediately. While P15A remains unauthorized, the harness now permits up to three bounded attempts to enter the exact case-sensitive token `P15A-OBSERVED`. Before each attempt it refreshes the GUI process and fails closed if the GUI has already exited. The token requirement itself is not weakened; after three mismatches the run still fails closed. This change performs no hardware operation. Implementation HEAD `a714767225f558b3f8934dffcc575e49ad72127b` passed the full workflow in **#1097**, run ID `36925940706`, including syntax validation, the P15A invariant, Windows PowerShell 5.1 compatibility, warnings-as-errors build and evidence-packaging self-test. The hardening is formally closed. A fresh separate authorization is based on closure HEAD `bb84846a57f2e81cab68fe7f1c89399af92094e7` / CI #1098 SUCCESS; as before, the authorization HEAD itself must pass the full workflow before target execution.

### P15A physical PASS closure

The post-hardening authorization HEAD `2189402b0d9d96948e44ec3940ad77d17a82b451` passed full CI **#1099** (run ID `36926810332`) and was executed on the exact HP 8C40 target. The preserved target evidence ZIP `p15a-startup-no-write_2026-10-01_181544.zip` has SHA-256 `cd8e7dfe2fef365e75b886ccca1c6c101ce12030754f21a8f9bc7f305f76c24e` and was independently reviewed.

The package binds local HEAD and upstream HEAD to the authorization HEAD, contains no tracked source modifications, identifies the exact target and exact audited P14.5 RC, and records a normal Firmware-mode GUI startup with healthy telemetry and CLOSED Manual/Automatic gates. All nine EC setpoint observations are `FF/FF`: 2 before startup, 5 while the GUI is running, and 2 after normal tray Exit. No non-Firmware setpoint, watchdog journal, lease ownership, Manual/Automatic request or power transition was observed.

The M4 watchdog began at Manual/Stopped/PID0/LocalSystem, was started by normal GUI startup, and remained Manual/Running/LocalSystem with the same PID/process-start identity through GUI exit. Installed watchdog/module hashes matched the physically qualified identities before, during and after the session. The application log contains both explicit application shutdown and GUI-exit markers. These observations satisfy the complete P15A startup/no-write contract.

P15A is therefore physically passed and formally evidence-closed. Its execution authorization is re-blocked. This closure does **not** authorize P15B, user-facing Manual, Automatic, the candidate curve, or any other physical gate.

### P15A authorization window

This authorization opens exactly one physical operation: execution of `scripts/test-p15-startup-no-write.ps1` against the exact audited P14.5 RC artifact. It does not authorize any fan-level command, firmware-restore command, watchdog lease acquisition, service reconfiguration, Manual/Automatic mode request or power transition.

The operator must first synchronize the dedicated P15 branch and verify that local HEAD equals the upstream authorization HEAD and that GitHub Actions for that exact HEAD completed successfully. The harness independently rechecks local/upstream HEAD equality, target fingerprint, RC artifact identity, service baseline/integrity and all no-write evidence before launching the normal GUI.

After the physical attempt, successful or failed, evidence must be preserved and this authorization must be re-blocked before any P15B preparation begins.

## P15B — one-shot Manual 30/30

P15B is a separate physical gate whose prerequisite is the formally closed P15A PASS. The preparation is now implemented while all P15B physical gates remain closed.

The controller path is deliberately the intended production stack: `Hp8C40ProductionWatchdogGate.CreateLeaseIfAuthorized` -> `HpFanControlBackendFactory.Create` -> `FanControlCoordinator` -> `AdaptiveFanProductionController`. The qualification controller enables Manual only inside its own hard-blocked test process; the compile-time user-facing Manual gate remains false and Automatic is false everywhere in the P15B path.

A valid physical run may issue exactly **one** `ApplyManualAsync(30)`, which must become one equal CPU/GPU `30/30` transaction. Before the write it requires exact-target identity, AC power, battery >=20%, three consecutive fresh complete SafetyGate-permitted bounded-load samples, firmware-owned `FF/FF`, the exact qualified M4 watchdog installation and an absent durable journal. The parent independently verifies the OWNED generation-3 journal, controller PID+creation time, 30/30 setpoint and stable watchdog process identity before allowing the child to continue.

After three bounded supervision samples, the controller must return through `AdaptiveFanProductionController.ReleaseToFirmwareAsync`. Strong restore means the production backend completes its validated `FF/FF -> LegacyDefault` sequence, observes local `FF/FF`, completes watchdog `RESTORE_BEGIN -> RELEASE`, deletes the durable journal, and the parent obtains two consecutive independent final `FF/FF` observations while the same watchdog process remains alive. Only after that proof may the harness stop the Manual M4 service back to its original Manual/Stopped/PID0 baseline.

An independent delayed exact-target 30/30 failsafe is armed before the controller. It is a safety backstop only; if it takes over, the run is safe but invalid. Evidence is preserved on PASS or FAIL_CLOSED and `git clean` is forbidden.

Preparation status: **CI PASS / formally closed**. Final preparation implementation HEAD `111d1e7a817c2468a4d6196447ec335d57c59d65` passed the full workflow in **#1103**, run ID `36930333854`; formal closure HEAD `0aee1b32f1bc063b31083e42c826e0eb87d35546` then passed **#1104**, run ID `36930713902`. Failed software-only runs #1101 and #1102 remain preserved.

A fresh authorization commit now opens only `manual30.executionAuthorized` and the dedicated `Hp8C40P15BManual30QualificationTest.PhysicalExecutionAuthorized` barrier. It does not open P15A again, the user-facing Manual gate, Automatic, Candidate V1, M9C or M9D qualification construction. The authorization is based on closure HEAD `0aee1b32f1bc063b31083e42c826e0eb87d35546` / CI #1104 SUCCESS and explicitly requires a **full SUCCESS on the authorization HEAD itself before target execution**. The first authorization HEAD `cf7ba5cd0ec31aeddd62cc7f86160c9328e3c82d` failed software-only CI #1105 because the legacy P15A invariant still asserted that P15B Manual authorization must remain closed even after P15A closure. No target-side execution was permitted or performed; the P15A invariant was narrowed so post-P15A P15B authorization is accepted while P15A itself stays re-blocked.

### P15B inherited-running service preflight correction

The first target-side attempt on authorization HEAD `6fe617693d4de54bfc709e07728c825f2246072d` / CI #1106 SUCCESS failed closed before the operator token because the M4 service was `Manual/Running/PID7980/LocalSystem`. The durable journal was absent and no conflicting OmenMon/OmenMon-Reborn/VictusFanControl.App process was present. This state is consistent with the completed P15A run, whose normal GUI startup intentionally started the exact Manual watchdog and left it running after GUI exit.

The original P15B preflight incorrectly treated only `Manual/Stopped/PID0` as valid. No EC setpoint probe, watchdog lease, failsafe, P15B controller, fan write or firmware restore occurred in this failed attempt, and no evidence ZIP was produced because the refusal occurred before the evidence-root/physical sequence.

P15B is re-blocked. The correction now implements two accepted baselines: exact qualified `Manual/Stopped/PID0/LocalSystem`, or exact qualified `Manual/Running/Ready/LocalSystem` with journal absent and stable PID/start identity. A pure resolver plus synthetic CI self-test covers both accepted states and rejects Running-without-Ready, retained-journal, wrong start mode and inconsistent PID states. The physical harness revalidates the inherited Running identity before the operator token and again after the token, reuses that exact watchdog process instead of restarting it, and leaves it Running after a successful strong restore. If the initial state was Stopped, the harness starts it only for P15B and returns it to Stopped. Thus no operator service manipulation is required.

Correction status: **CI PASS / formally closed / physical gates CLOSED**. Implementation HEAD `b24308e6b72e50e9a2ae40501c95f77bcfcf9f43` passed full GitHub Actions **#1108**, run ID `36932942781`, including PowerShell syntax, P15A/P15B invariants, the new service-baseline resolver self-test, Windows PowerShell 5.1 compatibility, warnings-as-errors build, and the retained RC regression path. No target-side physical execution occurred in this correction CI. A fresh separate P15B authorization is required.

## Current authorization boundary

- P15A startup/no-write execution: **PHYSICAL PASS FORMALLY CLOSED / EXECUTION RE-BLOCKED**
- P15B Manual 30/30 execution: **SERVICE-BASELINE CORRECTION CI PASS FORMALLY CLOSED / PHYSICAL GATES CLOSED**
- User-facing Manual execution: **CLOSED**
- User-facing Automatic execution: **CLOSED**
- Automatic policy: **OFF**
- Default control: **OFF**
