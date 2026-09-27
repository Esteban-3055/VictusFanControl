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


## Integrated production-path qualification

The exact-target production route has now been exercised successfully on the
8C40 target:

`SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend -> HP WMI ->
EC setpoint acknowledgement -> dual tachometer acknowledgement -> restore`.

Observed baseline before Custom authority:

- EC setpoint `FF/FF`
- CPU fan approximately 3189 RPM
- GPU fan approximately 2715 RPM

After the production backend requested equal `30/30`:

- EC setpoint became `30/30`
- CPU fan was approximately 3007 RPM at acknowledgement
- GPU fan was approximately 2974 RPM at acknowledgement
- six consecutive post-ACK supervision samples retained valid ownership,
  telemetry and physical fan feedback

The coordinator then restored firmware authority. The final EC setpoint was
`FF/FF` and the coordinator authority was `Firmware`.

This validates the integrated backend only for equal `30/30`. It does not
expand the validated command range.

## Telemetry qualification

A three-minute health soak completed with:

- 154/154 complete snapshots
- zero incomplete snapshots
- zero missing-value streak
- 14/14 physical-core temperatures
- Intel recoveries: 0
- EC recoveries: 0
- NVML recoveries: 0

The exact 8C40 target therefore has a physically exercised continuous
telemetry baseline for package temperature/power, physical-core temperatures,
GPU telemetry and both fan tachometers.

## EC 0x62 / 0x63 terminology correction

The integrated restore produced a useful new observation. Before Custom,
registers 0x62/0x63 were observed as 0x00/0. After the verified FF/FF restore,
they were observed as 0x03/240 while ownership had already returned to
firmware and MaxFan/FanSwitch remained sane.

Therefore the historical labels "manual" and "countdown" are too strong for
the 8C40 target. The generic and 8C40 diagnostic paths now call them
`Diagnostic62` and `Diagnostic63`.

They remain read-only diagnostics and are not part of the production ownership
decision. Production ownership continues to depend on:

- EC 0x34/0x35 setpoint
- both physical tachometers
- MaxFan 0xEC
- FanSwitch 0xF4

No EC register-value write to 0x62 or 0x63 is permitted.

## Next qualification: per-core thermal behavior

The next read-only hardware gate sequentially loads one representative logical
processor of each physical core for four seconds while recording package and
all 14 physical-core temperatures. It performs no fan command.

Purpose:

- verify that each discovered physical core reacts coherently to targeted load;
- characterize whether E-core DTS values are independent or shared/clustered;
- validate the hottest-core signal before it is used by a future adaptive fan
  policy.

The harness aborts before the SafetyGate emergency point if effective CPU
temperature reaches 90 C.


## First per-core thermal characterization result

The first sequential single-core characterization completed successfully with
all 14 physical cores detected (6 P + 8 E) and no thermal abort.

Observed effective CPU temperature stayed at or below approximately 69 C,
well below the 90 C characterization abort threshold.

Targeted P-core loading produced a clear response in the selected core:

- minimum target delta: approximately +13 C
- maximum target delta: approximately +22 C
- examples include C0 +16 C, C3 +21 C and C5 +22 C

This is consistent with the P-core temperature path being useful as a
per-core hotspot signal.

Targeted E-core loading behaved differently:

- target E-core deltas were only approximately +5 to +8 C
- in several runs another core's reported delta exceeded the target E-core
  delta by a large margin
- earlier idle/read-only snapshots also showed repeated equal temperatures
  across subsets of E cores

Therefore VictusFanControl must not yet interpret the eight E-core values as
eight proven independent physical thermal sensors.

This does **not** weaken the current SafetyGate decision. The production safety
aggregate remains:

`effective CPU temperature = max(package temperature, hottest reported physical-core-context temperature)`

The first characterization provided direct examples where hottest-core exceeded
package temperature (for example, a P-core peak around 69 C while package was
around 68 C), and E-core-targeted runs where the effective peak exceeded package
because another reported core context was hotter. Keeping the maximum is
therefore the conservative behavior.

The characterization harness has now been extended with an E-core
same-snapshot similarity matrix. It computes pairwise mean absolute temperature
difference and exact-match percentage across the full multi-load run. Candidate
shared/clustered readout groups are reported only when a pair remains nearly
identical across at least 95% of frames.

That next read-only pass is intended to distinguish "independent E-core sensor"
from "shared/clustered temperature readout" behavior without changing the fan
control envelope.


## E-core readout clustering characterization

A second full 14-core run completed and collected 123 E-core same-snapshot
frames. One transient Windows CPU-load sample was unavailable during C6
loading; it recovered on the first bounded retry. No Intel MSR, EC, NVML or
core-temperature dropout was observed in that event.

The E-core similarity matrix shows two very strong readout families:

- C06-C09: pairwise mean absolute differences approximately 0.04-0.19 C,
  with approximately 86-97% exact same-degree matches.
- C10-C13: pairwise mean absolute differences approximately 0.03-0.15 C,
  with approximately 92-98% exact matches.
- Between the two families: approximately 2.53-2.61 C mean absolute
  difference and only approximately 23-24% exact matches.

The intentionally strict 95%-exact-match graph reported C06/C07/C08 and
C10/C11/C12/C13. C09 narrowly missed that graph threshold, but its 0.09-0.19 C
mean difference from C06-C08 is still much closer to the first family than to
C10-C13.

The practical interpretation is therefore two four-context E-core thermal
readout clusters, C06-C09 and C10-C13. This is consistent with shared or
clustered E-core temperature reporting behavior, but it is **not** proof of the
physical DTS sensor topology inside the processor.

VictusFanControl will keep the eight raw E-core-context values for diagnostics,
but the future control policy must not count them as eight independent thermal
sensors. The conservative CPU safety/control aggregate remains:

`max(package temperature, hottest reported physical-core-context temperature)`

No weighting or averaging change is required before fan-curve development.
