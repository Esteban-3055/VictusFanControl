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

Current preparation state: **payload-layout correction CI validated and formally closed; P15A execution authorization remains CLOSED pending a fresh separate authorization commit. P15B and Automatic remain closed.**

### P15A preparation closure

Final hardened preparation HEAD: `8779b60ee994b5fd9b34fb753c90908048974fc8`.

GitHub Actions validation: **#1090**, run ID `36916449876`, **SUCCESS**. The full branch workflow passed PowerShell syntax validation, all historical M5-M9/P10-P14 invariants, the P15A static invariant, P15A evidence-packaging self-test, Windows PowerShell 5.1 compatibility, warnings-as-errors build and the existing RC packaging/artifact audit path. This run performed no target-side physical execution.

The earlier hardening commit triggered Actions **#1089** (run ID `36916292385`) and failed at the global PowerShell syntax check because one diagnostic string used `$required:`, which PowerShell parses as an invalid drive-qualified variable reference. The interpolation boundary was corrected to `${required}:`; no later CI step and no hardware operation ran in #1089. The failure is retained as evidence.

P15A preparation is formally closed. A separate authorization commit now sets only `startupNoWrite.executionAuthorized=true`. That authorization is based on preparation closure HEAD `1ea5d081422aced1acc3941e2110ba16289cd791` / CI #1091 SUCCESS and still requires a full **SUCCESS on the authorization HEAD itself before any physical execution**. P15B remains unimplemented/closed, and Manual/Automatic user-facing gates remain false.

### P15A payload-layout correction

The first authorized P15A attempt on HEAD `8e91c649056ba868ec94aa1864c7669aa90f4550` / CI #1092 SUCCESS failed closed after the exact P14.5 artifact and inner package hashes had already verified. The harness expanded the deterministic inner RC ZIP correctly, but then looked for `app\VictusFanControl.App.exe` directly under the extraction directory. P14.3 packages intentionally contain one top-level package root named `VictusFanControl-0.4.0-rc.1-win-x64`, so the real GUI is one level deeper.

The failure occurred before the service baseline check, before any EC setpoint probe, before GUI launch and before any fan-control command, firmware restore or watchdog lease acquisition. Because the failure occurred before the harness's evidence-packaging `try/finally`, no P15A evidence ZIP was produced; that absence is recorded rather than reconstructed.

The correction introduces a dedicated no-hardware payload resolver that requires exactly one package root matching the inner ZIP basename and verifies `PACKAGE-MANIFEST.json`, the GUI executable and both app PawnIO modules beneath that root. A CI self-test constructs the same nested ZIP shape so this exact integration mistake cannot silently recur. The correction implementation HEAD `2a56913030c403637618e98112eaf03accde2b31` passed the full workflow in **#1093**, run ID `36922825446`, including the new payload-layout self-test, Windows PowerShell 5.1 compatibility and warnings-as-errors build. No target-side hardware execution occurred in that CI run. P15A execution authorization remains re-closed until a fresh separate authorization commit.

### P15A authorization window

This authorization opens exactly one physical operation: execution of `scripts/test-p15-startup-no-write.ps1` against the exact audited P14.5 RC artifact. It does not authorize any fan-level command, firmware-restore command, watchdog lease acquisition, service reconfiguration, Manual/Automatic mode request or power transition.

The operator must first synchronize the dedicated P15 branch and verify that local HEAD equals the upstream authorization HEAD and that GitHub Actions for that exact HEAD completed successfully. The harness independently rechecks local/upstream HEAD equality, target fingerprint, RC artifact identity, service baseline/integrity and all no-write evidence before launching the normal GUI.

After the physical attempt, successful or failed, evidence must be preserved and this authorization must be re-blocked before any P15B preparation begins.

## P15B — one-shot Manual 30/30

P15B is intentionally not implemented or authorized by the P15A preparation. Its prerequisite is a formally closed P15A physical PASS. P15B will be a separate review/authorization and must perform exactly one equal 30/30 transaction through the intended production path, then prove strong restore: local `FF/FF`, `LegacyDefault`, stable independent `FF/FF`, watchdog RELEASE and durable journal absence. Automatic execution remains closed throughout P15B.

## Current authorization boundary

- P15A startup/no-write execution: **CLOSED AFTER CORRECTION CI PASS; AWAITING FRESH AUTHORIZATION**
- P15B Manual 30/30 execution: **CLOSED**
- User-facing Manual execution: **CLOSED**
- User-facing Automatic execution: **CLOSED**
- Automatic policy: **OFF**
- Default control: **OFF**
