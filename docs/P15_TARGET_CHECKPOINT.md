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

Current preparation state: **implemented, awaiting CI; physical execution authorization CLOSED**.

## P15B — one-shot Manual 30/30

P15B is intentionally not implemented or authorized by the P15A preparation. Its prerequisite is a formally closed P15A physical PASS. P15B will be a separate review/authorization and must perform exactly one equal 30/30 transaction through the intended production path, then prove strong restore: local `FF/FF`, `LegacyDefault`, stable independent `FF/FF`, watchdog RELEASE and durable journal absence. Automatic execution remains closed throughout P15B.

## Current authorization boundary

- P15A startup/no-write execution: **CLOSED**
- P15B Manual 30/30 execution: **CLOSED**
- User-facing Manual execution: **CLOSED**
- User-facing Automatic execution: **CLOSED**
- Automatic policy: **OFF**
- Default control: **OFF**
