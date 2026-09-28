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

Equal levels `30/30` through `36/36` are now physically qualified.
The production 8C40 envelope is therefore equal-only `30..36`.

Independent CPU/GPU commands remain unqualified and are explicitly rejected by
both the production BIOS/WMI layer and the production backend. The old 88F8
`14..50` range is still not portable evidence.

The following remain intentionally unqualified:

- asymmetric CPU/GPU level commands;
- levels below 30, including minimum stable level and restart from 0 RPM;
- levels above 36, including maximum/saturation level;
- watchdog/service recovery;
- process double-death recovery;
- Modern Standby custom-control lifecycle;
- automatic/adaptive fan policy.

## EC safety rules

The normal controller performs no arbitrary EC writes. EC is observation and
acknowledgement only.

Do not write `0x62` or `0x63`. Their values have changed across otherwise
valid firmware/control transitions (including observations such as 0x00,
0x03 and 0x07 for 0x62, and 0x00/0xF0 for 0x63). Their semantics are therefore
treated as unknown read-only diagnostics, not ownership or safety inputs.

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


## Adjacent fan-level qualification: 30-32

A bounded active qualification was run with a full HP firmware restore after
every individual level:

`30/30 -> FF/FF + LegacyDefault -> 31/31 -> FF/FF + LegacyDefault ->
32/32 -> FF/FF + LegacyDefault`.

All three commands were accepted by HP WMI, acknowledged by EC 0x34/0x35,
retained sane MaxFan/FanSwitch guards, produced physical feedback from both
tachometers and returned to `FF/FF` after each restore.

Observed six-sample medians:

| Equal level | CPU median RPM | GPU median RPM | CPU sample range | GPU sample range |
|---:|---:|---:|---:|---:|
| 30 | 3003 | 3009 | 2978-3041 | 2998-3024 |
| 31 | 3091 | 3096 | 3084-3111 | 2965-3125 |
| 32 | 3199 | 3180 | 3194-3227 | 2818-3203 |

Median step response was approximately:

- 30 -> 31: CPU +89 RPM, GPU +87 RPM
- 31 -> 32: CPU +108 RPM, GPU +85 RPM

The low first GPU samples at 31 and 32 occurred immediately after command
transition and then converged toward the sustained level. They are treated as
settling/transient samples rather than steady-state calibration points.

This evidence promotes the **production** range to equal-only `30..32`.
It does not qualify asymmetric commands or any level outside that interval.

The next production-path hardware gate should exercise level 32 through the
actual `SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend` route
before using the expanded range for later curve development.


## Production-path validation at upper bound 32/32

The promoted upper bound was exercised through the production control route and passed.

- Commanded equal level: `32/32`
- EC acknowledged: `32/32`
- Six post-ACK samples retained valid safety, ownership and fan feedback
- CPU fan during supervision: approximately 3175-3232 RPM
- GPU fan during supervision: approximately 3185-3208 RPM after settling
- Final coordinator authority: `Firmware`
- Final EC setpoint after restore: `FF/FF`
- MaxFan/FanSwitch guards remained `0x00/0x00`

This closes production-path qualification of the equal-only `30..32` envelope. No level above 32 is production-qualified yet.


## Partial upper-range qualification and EC-read harness correction

Level `33/33` completed successfully in the first upper-range run. HP WMI accepted the command, EC `0x34/0x35` acknowledged `33/33`, both tachometers converged near 3300 RPM, and the subsequent `FF/FF + LegacyDefault` restore was verified.

Observed six-sample result at level 33:

- CPU median: approximately 3300 RPM (3267-3332)
- GPU median: approximately 3297 RPM (3019-3312; the first sample was still settling)

Before any `34/34` write occurred, the broad diagnostic EC snapshot failed because the EC output buffer did not become full. Therefore level 34 was **not written** in that run. The system had already verified firmware restore after level 33.

The qualification harness has been corrected to use only the narrow production-relevant EC evidence (setpoint, MaxFan/FanSwitch guards and both tachometers), with bounded high-level retries. It resumes at 34 rather than rewriting 33. Production remains capped at equal-only `30..32` until 34-36 are reviewed.


## Upper fan-level qualification: 33-36

Equal levels `33/33`, `34/34`, `35/35` and `36/36` have now completed bounded physical qualification. Every level was accepted by HP WMI, acknowledged at EC `0x34/0x35`, produced sustained feedback from both physical tachometers, retained sane MaxFan/FanSwitch guards, and was followed by a verified `FF/FF + LegacyDefault` restore.

Observed six-sample medians:

| Equal level | CPU median RPM | GPU median RPM | CPU sample range | GPU sample range |
|---:|---:|---:|---:|---:|
| 33 | 3300 | 3297 | 3267-3332 | 3019-3312 |
| 34 | 3406 | 3385 | 3363-3417 | 3180-3395 |
| 35 | 3486 | 3494 | 3461-3529 | 3262-3534 |
| 36 | 3605 | 3605 | 3575-3623 | 3252-3611 |

The progression is monotonic in the medians. Approximate step deltas are:

- 32 -> 33: CPU +101 RPM, GPU +117 RPM
- 33 -> 34: CPU +106 RPM, GPU +88 RPM
- 34 -> 35: CPU +80 RPM, GPU +110 RPM
- 35 -> 36: CPU +119 RPM, GPU +111 RPM

The first GPU sample remained below steady state at each upper level even after the added settling delay; later samples converged. Median calibration is therefore preferred over the first post-command tach sample.

The production code range is promoted to equal-only `30..36`. The new upper bound `36/36` has also passed the full `SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend` hardware gate, so this expanded envelope is fully integrated.


## Production-path validation at upper bound 36/36

The equal-only upper bound `36/36` passed the full production route.

- Production backend accepted `36/36`.
- EC acknowledged `36/36` with MaxFan/FanSwitch remaining `0x00/0x00`.
- Immediate physical feedback after backend ACK was approximately CPU 3575 / GPU 3098 RPM; the GPU was still accelerating.
- Six subsequent supervision samples retained valid SafetyGate, ownership and tachometer feedback.
- Supervision RPM stayed approximately CPU 3587-3630 and GPU 3517-3630.
- Coordinator handed authority back to `Firmware`.
- Final EC setpoint returned to `FF/FF`.

This closes full production-path validation of the equal-only `30..36` range. Levels above 36 remain unqualified.

The integrated gate uses the narrow production-relevant EC evidence. Diagnostic fields not read by that path must not be printed as if they were real register values; the final restore display has been corrected accordingly.


## First guarded 10..50 sweep attempt

The first broad-range characterization intentionally started at the known-good `30/30` anchor. WMI accepted `30/30`, EC acknowledged the setpoint, and two physical samples were normal. A later narrow EC control-evidence read reported `MaxFan=0x90` with `FanSwitch=0x00`, so the fail-closed harness immediately restored firmware authority and aborted before any level below or above 30 was attempted.

The restore was verified at `FF/FF` with `MaxFan=0x00` and `FanSwitch=0x00`. Because all previous physical qualifications had reported `MaxFan=0x00`, this single `0x90` sample is treated as an unconfirmed anomalous EC read rather than proof that the guard genuinely changed.

The EC reader now reads the guard pair twice under one mutex lease and rejects disagreement. The broad qualification harness also requires repeated confirmation of any nonzero guard before treating it as persistent. A persistent abnormal guard still causes immediate fail-closed restore; only an isolated unstable sample is retried.


## Second guarded 10..50 attempt: mixed baseline setpoint read

With Windows automatic idle sleep inhibition active, the next broad-range attempt reached the read-only baseline and then stopped before any fan-level write. The narrow EC evidence returned CPU setpoint `144` and GPU setpoint `255` while MaxFan/FanSwitch remained `0x00/0x00`.

Because the qualification harness only permits equal CPU/GPU commands and firmware release is represented by `FF/FF`, the mixed `144/255` pair is not accepted as a valid baseline ownership state. No `SetFanLevel` command was issued in that run.

Ownership setpoint reads are now duplicated under the same EC mutex lease, mirroring the guard-read hardening. The broad qualification harness also re-reads any asymmetric setpoint observation before treating it as persistent. A persistent asymmetric state still fails closed and prevents any hardware write.


## Third guarded 10..50 attempt: hibernation during level 21

With the Windows `ES_SYSTEM_REQUIRED` inhibitor active, the broad sweep successfully characterized equal levels `30` down through `22`. The medians remained monotonic and approximately linear, reaching about 2200 RPM at level 22.

Level `21/21` was accepted by HP WMI and acknowledged by EC `0x34/0x35`, but before the first steady physical sample the machine entered hibernation. After resume, firmware restore was verified at `FF/FF`, while the telemetry snapshot was approximately 33.6 seconds stale. No level below 21 was written, and the later upper sweep was not meaningfully executed because telemetry remained incomplete/stale.

This confirms that `SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_CONTINUOUS)` is not sufficient to guarantee that this machine will not hibernate during the active hardware test. The extended-range harness now classifies a large telemetry-age discontinuity as a power-transition event, restores firmware, and terminates the entire run instead of attempting another sweep segment. A separate power-transition diagnostics collector was added to capture Windows sleep policy, active power requests, recent Kernel-Power/Power-Troubleshooter events, System Sleep Diagnostics and SleepStudy evidence before continuing lower-range characterization.


## Confirmed cause of the broad-sweep hibernations

Windows event evidence now identifies both broad-sweep power transitions as battery-triggered hibernations rather than idle sleep. In both incidents, Kernel-Power logged the critical-battery trigger, then a sleep transition with reason `Battery`, followed by resume from hibernate.

The active power plan has AC idle hibernation disabled and allows system-required requests, so the test's `ES_SYSTEM_REQUIRED` request was not being ignored by the normal idle-sleep policy. The transition instead followed the critical-battery path, which can supersede ordinary idle inhibition.

SleepStudy shows physically implausible battery telemetry around the incidents: the pack was reported near/full, then capacity or percentage collapsed to zero over a very short interval, and returned to full after resume. One incident also contained a brief AC-source drop before the false critical-battery event. This rules out real battery discharge as the explanation.

Because the fan characterization performs repeated direct EC transactions and laptop battery telemetry is also firmware/ACPI-managed, EC/ACPI contention is now the primary working hypothesis. This remains a causal hypothesis rather than proof of the precise kernel/firmware race.

Mitigations now applied to the extended qualification harness:

- normal setpoint and guard reads are no longer duplicated unconditionally, reducing EC transaction volume;
- abnormal/asymmetric evidence is still confirmed at the higher 8C40 qualification layer before failing;
- characterization sampling is reduced to 1 Hz;
- ownership/guard evidence is checked at bounded phase boundaries rather than on every tach sample;
- Windows AC/battery status is checked throughout the active step; AC loss, unknown battery state or battery below the qualification floor causes immediate restore/abort;
- a hibernate-like telemetry-age discontinuity terminates the complete sweep after restore.

Do not disable Windows critical-battery protection as a workaround. The test must coexist with that protection rather than masking a potentially real battery emergency.
