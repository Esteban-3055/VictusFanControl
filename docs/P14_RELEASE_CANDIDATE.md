# P14 — release-candidate productization

P14 begins only after formal P13 software closure. Its job is to turn the current source tree into an auditable software release candidate without changing the physical-control authorization boundary.

P14 is not a hardware qualification. P15 remains the first post-P13 target-side checkpoint.

## P14.1 — software readiness baseline

Baseline source HEAD: `7395a8c14afcaf352ad5e5be48f66c897f03fd2e`.

Baseline CI: GitHub Actions #1072, run ID `36899789506`, result **SUCCESS**.

P14.1 adds a machine-readable release-readiness contract and a CI invariant that requires the entire safety boundary to remain unchanged:

- exact target remains HP 8C40 / 9D0R1LA / BIOS F.18;
- `control.enabledByDefault=false`;
- `automaticPolicyEnabled=false`;
- Manual execution authorization remains false;
- Automatic execution authorization remains false;
- Candidate V1 remains physically unvalidated and unauthorized for production;
- M9C and M9D qualification-construction gates remain false;
- P13 software closure remains present and successful.

No publish package is created in P14.1.

## Planned P14 sequence

P14.2 centralizes the software-RC version and defines a deterministic `win-x64` publish layout. P14.3 adds packaging plus SHA-256 manifest generation. P14.4 uploads and verifies the package as a CI artifact. P14.5 performs the final software-RC audit and formally closes P14.

No P14 step may silently authorize Manual or Automatic execution.


## P14.2 — versioned deterministic win-x64 publish layout

P14.2 centralizes the release-candidate version as `0.4.0-rc.1` in `Directory.Build.props`. Per-project `<Version>` overrides are removed so the GUI, core, watchdog and diagnostic projects cannot silently drift to different product versions.

The release publish layout is intentionally narrow:

```text
p14-publish/
  app/       VictusFanControl.App framework-dependent win-x64 publish
  watchdog/  VictusFanControl.Watchdog framework-dependent win-x64 publish
```

The end-user RC excludes the standalone core CLI and the Modern Standby qualification probe from this publish layout. The core assembly is still included transitively where required by the GUI/watchdog.

Publish settings are fixed to `Release`, `win-x64`, framework-dependent, multi-file, no PDB/debug symbols, no ReadyToRun and deterministic compilation. This is a fixed/auditable publish layout; P14.3 is responsible for the final package container and SHA-256 manifest.

PawnIO module binaries are deliberately not introduced by P14.2. They remain pinned external inputs and will be brought into the final package only by the separately verified P14.3 packaging step.

The build and verification scripts refuse to perform fan-control operations. They only invoke `dotnet publish` and inspect the resulting files.


### P14.2 formal closure

Implementation/fix source HEAD: `7bc405b4103014e52cf606a6064c71d748123ad7`.

Full GitHub Actions validation: **#1076**, run ID `36901860532`, **SUCCESS**. The workflow compiled the entire solution, ran all prior safety/watchdog/P10-P13 invariants, ran the P14.2 static invariant, then built and verified the actual framework-dependent `win-x64` GUI/watchdog publish layout.

This closes P14.2 only. It does not create the final distributable archive, does not package PawnIO modules, does not generate the P14.3 SHA-256 manifest, and does not authorize Manual or Automatic hardware execution.


## P14.3 — deterministic package and SHA-256 manifest

P14.3 turns the verified P14.2 publish layout into a deterministic software-RC ZIP. External PawnIO module input is pinned in `release/p14-external-inputs.json` to PawnIO.Modules 0.2.11 and archive SHA-256 `43608cb89bc84247fef1368a139013f7d043e17db6d6c8dfc9b46bf0905a81f4`.

The network fetch is separated from packaging: `fetch-p14-external-inputs.ps1` downloads and verifies the pinned archive; `package-p14-rc.ps1` accepts only that verified archive as an input. The GUI package receives `IntelMSR.bin` and `LpcACPIEC.bin`; the watchdog package receives only `LpcACPIEC.bin`.

`PACKAGE-MANIFEST.json` records SHA-256 and size for every payload file. The ZIP is created with files sorted by normalized relative path, a fixed UTC ZIP timestamp (`2000-01-01T00:00:00Z`) and no-compression entries to minimize host-dependent byte variation. A separate `.sha256` file authenticates the complete ZIP.

CI builds two packages independently from the same publish tree and pinned external archive, verifies both manifests, and requires the complete ZIP SHA-256 values to be identical. P14.3 still does not upload a retained GitHub artifact; that is P14.4.


### P14.3 formal closure

Implementation source HEAD: `9ca006d98b369654ff1fd6ffc3fa7cb1c8745250`.

Full GitHub Actions validation: **#1078**, run ID `36903426730`, **SUCCESS**.

CI independently built the RC ZIP twice from the same source and pinned external input, verified every manifest entry, and obtained the identical ZIP SHA-256 `603ac7b2ca6816fe00002598410983ba031529ec168a70c5ec59041e5a71756c` for both builds. The pinned PawnIO.Modules archive hash was verified before extraction.

This closes P14.3 only. The ZIP is reproducible and integrity-manifested, but GitHub Actions does not yet retain it as a downloadable workflow artifact; artifact publication/retention is P14.4. No physical fan execution was performed.

## P14.4 — retained GitHub Actions artifact and external identity

P14.4 retains the deterministic RC output in GitHub Actions without placing the final artifact/container digest inside the RC payload.

Identity is deliberately split into three layers:

- **RC payload identity:** SHA-256 of `VictusFanControl-0.4.0-rc.1-win-x64.zip`, authenticated by the adjacent `.sha256` file and by `PACKAGE-MANIFEST.json` inside the RC ZIP.
- **CI/source identity:** `P14-ARTIFACT-ATTESTATION.json` lives beside the RC ZIP, not inside it. It binds the payload SHA-256 to the exact source HEAD, repository, ref, Actions run ID/number, pinned PawnIO input hash and the closed hardware-execution boundary.
- **GitHub transport identity:** `actions/upload-artifact@v4` supplies an artifact ID and SHA-256 `artifact-digest` for GitHub's retained wrapper. That digest exists only after upload and is therefore kept outside the RC payload/attestation to avoid self-reference.

The retained artifact has a source-bound name `VictusFanControl-0.4.0-rc.1-win-x64-<sourceHead>`, contains exactly the RC ZIP, its `.sha256`, and the external attestation, and uses a documented 30-day retention period. The workflow performs a final stage verification after all self-tests and only then uploads the artifact with missing-file behavior set to error.

The implementation commit deliberately leaves the machine-readable P14 state at the formally closed P14.3 boundary until a real Actions run has completed and the retained artifact has been queried/downloaded and verified. Only a separate closure commit may then record P14.4 as CI-validated. P14.4 does not enable Manual or Automatic execution and performs no target-side hardware test.

### P14.4 formal closure

Implementation source HEAD: `1c7ee997b5bbac02eb89e1c7d3f91e7130828c1d`.

GitHub Actions validation: **#1080**, run ID `36907848705`, **SUCCESS**.

The run retained artifact ID `11186116708`, named `VictusFanControl-0.4.0-rc.1-win-x64-1c7ee997b5bbac02eb89e1c7d3f91e7130828c1d`, with 30-day retention. GitHub reported wrapper digest `sha256:eb42b9c7d8a5d29e7c30a4f35f19f7e07fecacb83dee26aaf638093cbf2826b2`.

The retained artifact was downloaded and independently inspected. It contains exactly three files: the deterministic RC ZIP, its adjacent `.sha256`, and `P14-ARTIFACT-ATTESTATION.json`. The downloaded wrapper SHA-256 exactly matched GitHub's reported artifact digest. The inner RC ZIP SHA-256 was `e0ceb47c4b3be61c98f2d494b652fb546bd0d7624b7e5f452e66627414376d65`; the adjacent `.sha256` matched it; the external attestation bound that digest to source HEAD `1c7ee997...`, run #1080 and the pinned PawnIO.Modules 0.2.11 archive; and all 61 payload entries in `PACKAGE-MANIFEST.json` rehashed with no size/hash/list mismatches.

This closes P14.4 only. The closure commit necessarily changes repository metadata that is included in subsequently rebuilt RC payloads, so later same-head ZIP/artifact digests are expected to differ. Their final identity remains external evidence; no digest is recursively embedded into the artifact it authenticates. P14.5 remains open. No physical fan execution was performed and no Manual/Automatic gate was opened.

### P14.4 closure CI correction history

The first formal-closure commit `8c9f47bbb3fabe04b8db911fa3adfd0b5c5bd6f4` triggered GitHub Actions **#1081** and failed closed in the pre-existing M9D invariant before any P14.4 upload step. The cause was not a fan-control or lifecycle regression: the closure edit had reserialized the full HP-8C40 profile through a JavaScript numeric parser, losing precision in historical 64-bit creation-tick values. The M9D invariant correctly detected the changed watchdog creation ticks.

The failed run is retained as evidence. The correction restores the profile byte values from the last green P14.4 implementation HEAD and applies only the narrow P14.4 textual metadata edit, preserving all historical 64-bit evidence exactly. No hardware execution occurred in #1081 and its P14.4 artifact upload steps were skipped.

## P14.5 — final software RC audit

P14.5 is the last software-only checkpoint before P15. It re-audits the exact HP 8C40 target contract, all prior P13/P14 closure evidence, version/publish/package contracts, pinned PawnIO input, retained-artifact provenance, compile-time Manual/Automatic gates, Candidate V1 authorization state, M9C/M9D qualification-gate closure, default-control state and automatic-policy state.

The same CI run first retains the source-bound RC through the P14.4 artifact path. After that upload returns its artifact ID and GitHub SHA-256 digest, P14.5 writes a separate `P14-FINAL-SOFTWARE-RC-AUDIT.json` that binds those values to the exact source HEAD/run and to the inner RC ZIP/hash files. The audit JSON is uploaded separately as `VictusFanControl-P14.5-final-audit-<sourceHead>`.

The separation prevents self-reference: neither the RC payload nor the final audit evidence embeds its own GitHub wrapper digest. The implementation state remains `P14_5_FINAL_SOFTWARE_RC_AUDIT_IMPLEMENTED_AWAITING_CI` until both retained artifacts have been downloaded and independently verified. P15 remains unopened.
