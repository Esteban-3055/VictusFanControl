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
