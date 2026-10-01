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
