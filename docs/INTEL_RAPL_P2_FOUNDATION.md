# Intel RAPL P2 — CPU power limiter foundation

P1 physically qualified the exact HP 8C40 / 9D0R1LA / F.18 / i7-13700H
target for bounded MSR 0x610 power-limit control. The qualifying 20 W / 40 W
run accepted the exact requested raw value, retained it for 90 samples,
showed comparable ~100% CPU-load windows near 40 W and later near 20 W, and
restored the 45 W / 115 W baseline exactly.

P2 does **not** promote unrestricted production writes.

## P2A scope

- Add an evidence classifier that can find ordered comparable high-load
  windows inside the limited phase instead of relying only on an idle
  pre-write baseline and the last ten samples.
- Freeze the physical P1 evidence identity in
  `release/intel-rapl-p1-qualification.json`.
- Add a production-domain `CpuPowerLimiter` state machine with
  Unsupported / Disabled / Applying / Active / Recovering / Failed states.
- Keep the backend abstract. P2A contains no new physical MSR write path and
  is not connected to GUI, startup, profiles, fan control or Automatic.
- Applying is single-shot. Verification never blindly reapplies after an
  external change.
- Release is conditional through backend restore planning; an external value
  is preserved rather than overwritten.

## Remaining gate before production hardware connection

A production RAPL backend must retain the P1 exact-target checks, durable
journal-before-write behavior and conditional field ownership semantics.
More importantly, it needs a detached guardian/recovery owner suitable for
the main application lifecycle. P1's experimental guardian is evidence for
the recovery model, not automatic authority to reuse the write path from the
GUI.

Until that gate is implemented and qualified,
`productionHardwareWritesAuthorized=false`.
