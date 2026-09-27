# HP 8C40 / Victus 15-fa1013la port status

## Exact qualified target

- Board manufacturer/product/version: `HP / 8C40 / 63.43`
- System: `Victus by HP Gaming Laptop 15-fa1xxx`
- SKU base: `9D0R1LA`
- BIOS: `F.18`
- CPU: Intel Core i7-13700H
- GPU: NVIDIA GeForce RTX 4060 Laptop GPU
- Sleep model: S0 Low Power Idle / Modern Standby; legacy S3 is unavailable

The port is exact-match and fail-closed. No generic Victus-family fallback is
allowed.

## Physically qualified fan-control primitive

The bounded 30/30 qualification established:

1. Firmware baseline: EC `0x34/0x35 = FF/FF`.
2. HP WMI `SetFanLevel(30,30)` returns and takes physical effect.
3. Active ownership: EC `0x34/0x35 = 0x1E/0x1E`.
4. Physical feedback: CPU/GPU tachometers at `0xB0/0xB2` converged to
   approximately 3003/2994 RPM.
5. Guards remained `0xEC=00` and `0xF4=00`.
6. Restore `SetFanLevel(FF,FF) -> FanMode=LegacyDefault` returned
   `0x34/0x35` to `FF/FF` and firmware resumed dynamic RPM control.

`GetFanLevel` is current-speed telemetry, not setpoint acknowledgement. It
must not replace EC ownership + physical tachometer acknowledgement.

## Current control envelope

Only equal `30/30` is qualified. The old 88F8 `14..50` range is not
portable evidence and must not be exposed by the 8C40 backend until new physical
characterization is completed.

The following remain intentionally unqualified:

- asymmetric CPU/GPU level commands;
- minimum stable level and restart from 0 RPM;
- maximum/saturation level;
- watchdog/service recovery;
- process double-death recovery;
- Modern Standby custom-control lifecycle;
- automatic/adaptive fan policy.

## EC safety rules

The normal controller performs no arbitrary EC writes. EC is observation and
acknowledgement only.

Do not write `0x62` or `0x63`. On the target, `0x62` was observed as
`0x06` and `0x63` behaves as a live countdown maintained by HP/OMEN
components.

## CPU core-temperature extension

The 8C40 port adds read-only physical-core temperature telemetry using Intel
`IA32_THERM_STATUS (0x19C)` under per-core thread affinity, with TjMax from
`0x1A2`. Telemetry records package temperature, every physical-core
temperature, hottest core and core average.

Safety uses:

`effective CPU temperature = max(package temperature, hottest physical core)`

A missing/incomplete physical-core temperature set keeps telemetry/SafetyGate
fail-closed. This gives the future adaptive controller a safer CPU thermal
input without enabling the policy prematurely.

## Lifecycle boundary

The old 88F8 Gate G2 S3 result remains historical validation for that target.
It must not be interpreted as validation for 8C40. The 8C40 requires a separate
Modern Standby qualification sequence before watchdog-backed unattended custom
control can be enabled.
