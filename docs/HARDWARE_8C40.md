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

A complete guarded hardware sweep has now physically characterized every equal
level from `10/10` through `50/50`. Every level was accepted by HP WMI,
acknowledged by EC `0x34/0x35`, produced sustained feedback from both physical
tachometers, and was followed by a verified `FF/FF -> LegacyDefault` restore.

The measured medians are strictly increasing for both fans across the complete
10..50 sequence. The endpoints were approximately:

- `10/10`: CPU 1028 RPM, GPU 1013 RPM;
- `50/50`: CPU 4991 RPM, GPU 4991 RPM.

This establishes the **physically characterized equal-level primitive** as
`10..50` for this exact machine. It does **not** automatically expand the
production backend.

The production 8C40 code envelope is now explicitly promoted to equal-only
`10..50`. This promotion is justified by the complete physical sweep, the
direct level-10 restart from 0 RPM, the large-transition qualification and the
qualification endpoint coordinator PASS at both 10 and 50.

The expanded production configuration is now **fully integrated for bounded
manual equal-only control across 10..50**. The final post-promotion regression
passed both endpoints through the unmodified production backend. Automatic/
adaptive policy remains OFF.

Independent CPU/GPU commands remain unqualified and are explicitly rejected by
both the production BIOS/WMI layer and the production backend.

The following remain intentionally unqualified or pending:

- asymmetric CPU/GPU level commands;
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


## Extended sweep attempt: zero-degree NVML GPU telemetry

A later guarded 10..50 attempt did not reach EC baseline or any fan write. The Windows AC/battery sanity gate passed with AC online and battery at 100%, but both the read-only preflight and the active baseline reported NVIDIA GPU temperature as `0.0 C`, with approximately 1.2 W and 0% GPU utilization.

SafetyGate correctly refused that snapshot because the validated GPU plausibility floor is 10 C. The old NVML wrapper nevertheless considered 0 C syntactically valid, so diagnostics could misleadingly print `Baseline ready: True` even though SafetyGate would never permit control.

The NVML wrapper now rejects 0 C as unavailable/invalid thermal telemetry. The extended hardware gate also waits up to 20 seconds for a SafetyGate-ready baseline before refusing cleanly, and it will not issue any fan write while the GPU thermal sensor remains unavailable. A self-test pins zero-degree GPU telemetry as a fail-closed condition.


## Complete equal-level 10..50 characterization PASS

The final guarded broad sweep completed every equal level from 10 through 50 without a power transition, battery sanity failure, ownership loss, guard-state anomaly or restore failure. The earlier level-21 interruption was therefore not reproduced after reducing EC traffic and adding AC/battery sanity checks.

The RPM mapping is close to 100 RPM per command level across the full range. All adjacent median deltas remained positive for both CPU and GPU, with ordinary sample scatter around the trend. Level 31 showed a first-sample transition lag after the large jump from the low-end sweep, but its median and subsequent samples converged normally.

The broad sweep proves the steady equal-command primitive, not every production transition. Production remains 30..36 until endpoint integration and low-end restart behavior are separately qualified.


## Prepared next gate: large transition qualification

The next physical gate is prepared but not yet counted as a hardware PASS. It intentionally keeps the fixed override active between commands and executes:

`firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware`

Each command must receive exact EC setpoint acknowledgement. Telemetry is sampled at 1 Hz to keep direct EC traffic conservative after the earlier false critical-battery incidents. A transition converges only after two consecutive CPU/GPU tachometer samples are inside a terminal band of max(150 RPM, 5% of the nominal level*100 RPM). Each step has a 12-second convergence timeout.

The gate retains the AC/battery sanity checks, SafetyGate, light-load envelope, Windows idle-sleep inhibition, exact-target fingerprint, conflicting-controller exclusion, control guard checks and a mandatory final FF/FF + LegacyDefault restore. Any failure after a possible write enters the final restore path with CancellationToken.None.

This gate does not alter the production 30..36 range. Its purpose is to qualify large rise/fall dynamics and collect convergence time data before production endpoint promotion or adaptive slew tuning.


## Large transition qualification PASS

The prepared transition gate completed successfully with the fixed override retained between intermediate commands:

`firmware -> 10 -> 30 -> 50 -> 30 -> 10 -> firmware`

The firmware baseline had both tachometers at 0 RPM. A direct `10/10` command therefore also qualified low-end restart from a fully stopped state: both fans spun up, briefly overshot to roughly 1.6-1.7k RPM, and converged to about 1.0k RPM in 6.0 seconds.

Measured terminal-convergence times were approximately 8.6 s for 10->30, 9.3 s for 30->50, 10.2 s for 50->30 and 7.7 s for 30->10. All steps retained exact EC ownership, sane guards and safe telemetry. The final FF/FF + LegacyDefault restore was verified. No battery/power transition anomaly recurred.

This closes the previously pending low-end restart-from-zero and large direct transition questions for the qualification path. Production remains 30..36 until the coordinator/backend endpoint integration gate passes.

## Prepared endpoint coordinator qualification

A new qualification-only integration gate is prepared for endpoints 10 and 50. It uses the real SafetyGate, FanControlCoordinator and Hp8C40FanControlBackend logic, while injecting the already-characterized 10..50 equal command envelope and qualification-only WMI writer. This avoids changing production defaults before the endpoint route is physically exercised.

The sequence is independent endpoint admission and restore: `firmware -> 10 -> firmware`, then `firmware -> 50 -> firmware`. Each endpoint must pass backend EC/tach acknowledgement, continuous coordinator supervision, two-sample terminal RPM convergence and verified FF/FF restore. Production remains equal-only 30..36 until this gate and a later explicit promotion regression pass.


## Endpoint coordinator qualification PASS

The qualification-only endpoint integration gate passed both physically characterized endpoints through the real control architecture while leaving production defaults unchanged:

`SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend logic -> qualification WMI writer -> EC/tach ACK -> terminal convergence -> restore`.

Endpoint `10/10` started from a firmware baseline of 0/0 RPM. Custom authority was granted, the backend acknowledged the command, both fans converged to approximately 981/1012 RPM in 4.5 seconds, and the coordinator restored Firmware authority with EC setpoints `FF/FF`.

Endpoint `50/50` also started from a 0/0 RPM firmware baseline. Custom authority was granted, the backend acknowledged the command, both fans converged to approximately 4991/4968 RPM in 14.9 seconds, and the coordinator again restored Firmware authority with EC setpoints `FF/FF`.

AC/battery sanity remained normal (AC online, battery 93%). No ownership loss, guard anomaly, telemetry safety drop or power-transition event occurred.

This closes endpoint qualification through the coordinator/backend logic. Production remains equal-only 30..36 until an explicit 10..50 promotion commit is made and the resulting production configuration passes a final post-promotion regression at both endpoints.


## Production promotion to equal-only 10..50

The code-level production envelope has now been promoted from `30..36` to
`10..50` for the exact HP 8C40 target only. No HP 88F8 production constants or
behavior were changed.

The promotion is deliberately narrow:

- `Hp8C40TargetProfile.MinimumValidatedFanLevel/MaximumValidatedFanLevel`
  now resolve to the physically qualified 10/50 bounds;
- `Hp8C40BiosFanControl` inherits those bounds and still rejects asymmetric
  CPU/GPU commands;
- `Hp8C40FanControlBackend` production capabilities and command validation
  inherit the same equal-only 10..50 envelope;
- GetFanLevel remains current/effective-speed telemetry and is **not** command
  acknowledgement;
- command acknowledgement remains HP WMI success + exact EC 0x34/0x35 setpoint
  + physical response from both tachometers;
- restore remains `FF/FF -> LegacyDefault -> verified FF/FF`;
- unknown/external fixed overrides are never blindly cleared;
- EC 0x62/0x63 remain read-only diagnostics and are never written;
- watchdog recovery and automatic/adaptive policy remain OFF for HP 8C40.

CI self-tests pin the production BIOS and backend boundaries to 10 and 50 and
continue to assert asymmetric and out-of-range rejection.

### Required final post-promotion hardware regression

A dedicated script, `scripts/test-8c40-post-promotion-endpoints.ps1`, executes
two independent production-path gates:

`firmware -> 10 -> firmware`

then

`firmware -> 50 -> firmware`.

It invokes the existing real `Hp8C40IntegratedCoordinatorTest` with the public
`Hp8C40FanControlBackend(modulesDirectory)`. It does **not** inject a
qualification command envelope and does **not** use the qualification-only WMI
writer.

Each endpoint must retain SafetyGate permission, exact EC ownership, sane
MaxFan/FanSwitch guards, physical feedback from both tachometers, continuous
coordinator supervision, two consecutive terminal-band RPM samples using
max(150 RPM, 5% of level*100 RPM), and a verified return to Firmware/FF/FF. The production
gate also performs AC/battery sanity checks because the earlier broad-range
hibernations were traced to transient critical-battery telemetry.

Both endpoint runs have now passed physically on the promoted code. The exact
HP 8C40 target therefore has a **fully integrated bounded production envelope
of equal-only 10..50** for manual/coordinator-driven control. This statement
does not qualify watchdog-backed unattended control, Modern Standby recovery,
asymmetric fan commands or automatic/adaptive policy.


## Final post-promotion production regression PASS

The final promoted-code regression completed successfully through the real
production route with no qualification envelope injection:

`SafetyGate -> FanControlCoordinator -> Hp8C40FanControlBackend -> HP WMI ->
EC 0x34/0x35 acknowledgement -> dual tachometers -> terminal convergence ->
FF/FF + LegacyDefault restore`.

Preflight completed with all telemetry backends ready, 14/14 physical-core
temperature contexts present, valid NVIDIA NVML telemetry and automatic policy
still disabled. The hardware-test AC/battery sanity gate reported AC online and
battery 100% for both endpoint runs.

### Production endpoint 10/10

The production backend accepted equal `10/10`. EC ownership became `10/10`
with MaxFan/FanSwitch remaining `0x00/0x00`. Both tachometers decelerated from
the firmware baseline and reached two consecutive terminal-band samples around
the nominal 1000 RPM endpoint.

- backend acknowledgement observed at approximately CPU 2311 / GPU 2089 RPM
  while the fans were still decelerating;
- terminal convergence: CPU 1022 RPM / GPU 970 RPM;
- convergence after backend ACK: approximately 5.4 s;
- coordinator restore returned authority to `Firmware`;
- final EC ownership: `FF/FF`.

### Production endpoint 50/50

The second independent production run began again from firmware ownership.
The production backend accepted equal `50/50`, EC ownership became `50/50`
with MaxFan/FanSwitch `0x00/0x00`, and both tachometers accelerated
monotonically toward the terminal band.

- backend acknowledgement observed at approximately CPU 2250 / GPU 2175 RPM
  while the fans were still accelerating;
- terminal convergence: CPU 4968 RPM / GPU 4968 RPM;
- convergence after backend ACK: approximately 11.4 s;
- coordinator restore returned authority to `Firmware`;
- final EC ownership: `FF/FF`.

The post-restore tachometers were still near the just-commanded fan speed at
the instant of the final EC read. This is expected inertia/firmware transition
behavior and is not an ownership failure: the authoritative restore evidence
is the verified `FF/FF` setpoint plus coordinator `Firmware` authority.

This closes the 10..50 production-range promotion for the exact
HP 8C40 / 9D0R1LA / BIOS F.18 target. The next development boundary is the
8C40-specific Modern Standby/watchdog lifecycle and fail-closed safety loss
behavior under Custom. Automatic/adaptive policy remains disabled.


## Modern Standby M0 observer prepared

The next 8C40 lifecycle step is now instrumented as a dedicated read-only M0
characterization gate. The new observer is intentionally separate from the
production GUI/backend and from the historical 88F8 S3 Gate G harness.

`scripts/test-8c40-modern-standby-m0.ps1` exact-matches the HP 8C40 target,
builds the solution, runs the synthetic M0 observer self-test, records
`powercfg /a` plus active power requests, then launches an invisible
read-only window observer. The observer explicitly registers for Windows
suspend/resume notifications and session-display, console-display, AC/DC and
lid power-setting notifications.

The observer records UTC, QueryUnbiasedInterruptTime, GetTickCount64 and QPC
for each event, keeps transition events in memory, writes only one READY marker
before the test and one JSON report after the display returns, and performs no
PawnIO/EC/HP fan WMI/NVML/watchdog/fan-control/sleep-inhibition operation.

The shared power-transition collector now emits distinct System Power Report,
System Sleep Diagnostics and SleepStudy files. A display Off -> On capture is
not itself considered a Modern Standby PASS; the JSON timeline and Windows
reports must be reviewed together.

This preparation does not change the production state: 8C40 watchdog recovery
remains unvalidated/disabled and automatic/adaptive policy remains OFF. The
legacy 88F8 Gate D-G hardware harnesses remain blocked on this Modern Standby
target.


## Watchdog M1 target-aware core

M1 is a code-architecture gate only; it grants no new real-hardware watchdog
authority to HP 8C40.

The durable watchdog core no longer assumes one global 14..50 setpoint range.
It now consumes an explicit target policy. The policies pinned by CI are:

- HP 88F8: independent CPU/GPU 14..50, preserving the already qualified
  historical Gate D-G behavior;
- HP 8C40: equal-only 10..50, matching the physically qualified production
  control envelope.

The durable lease journal is promoted from schema v1 to schema v2 and carries
the exact target profile id. Target mismatches fail closed: the journal is
retained, no ownership inference is made and no restore is issued. Legacy v1
journals have no target identity, so they can be interpreted only after an
exact HP 88F8 match; they are never migrated or assumed to belong to 8C40.

The historical Gate D service remains exact-match HP 88F8-only and now exposes
its target profile id in its status. The HP 8C40 backend/factory watchdog guards
remain unchanged, `WatchdogRecoveryValidated` remains false, and automatic/
adaptive policy remains OFF.

M1 therefore prepares the durable state machine for 8C40 without enabling any
8C40 service-side EC/WMI write. The next gate is M2: exact-target Session-0 /
LocalSystem read-only dependency access on HP 8C40.


## Watchdog M2 PASS - Session-0 read-only dependency access

M2 passed physically on the exact HP 8C40 target on 2026-09-28.

The isolated `VictusFanControlWatchdogM2` service exact-matched
`HP-8C40-9D0R1LA-F18`, ran as LocalSystem in Session 0 and completed two
independent start/stop cycles. Each cycle successfully read the narrow EC
`0x34/0x35` ownership pair and HP BIOS/WMI `GetFanLevel`.

EC ownership remained `FF/FF` in both service cycles and in the final
independent interactive verification. Read-only GetFanLevel observations were
`26/24` and `26/23`; these are current/effective fan-speed levels and are
not command acknowledgements. No `lease.json` was created and no fan-level
write, FF/FF release or LegacyDefault restore was issued.

M2 therefore proves exact-target LocalSystem/Session-0 dependency access only.
It does not authorize recovery writes. `WatchdogRecoveryValidated` remains
false, the real 8C40 watchdog lease and service-side restore remain blocked,
and automatic/adaptive policy remains OFF.

The next boundary is M3: one bounded service-side restore-only qualification
from a known VFC-owned equal `30/30` state, with external-override
preservation and verified return to firmware ownership.

See `docs/WATCHDOG_M2_8C40.md` and
`scripts/test-watchdog-m2-8c40.ps1`.


## M2.5 legacy-88F8 isolation hardening — CODE/CI PASS

After the physical M2 PASS, the repository was audited specifically for
historical HP 88F8/S3 paths that could accidentally reach the HP 8C40 target.

The following hardening is now part of the M2.5 code gate:

- all historical GUI hardware modes (`--suspend-custom-test` and Gate D-G)
  require the exact `HP-88F8-62C37LA-F32` fingerprint before MainForm/backend
  creation;
- MainForm keeps a second exact-target guard so those harnesses cannot be
  reused on HP 8C40 even if invoked outside the normal startup path;
- the generic S3-era Healthy/recovery path does not reopen Custom admission on
  a Modern Standby target. Until the display-aware M-series lifecycle is
  qualified, an 8C40 maintenance wake remains fail-closed;
- the legacy Gate G watchdog state reader now requires the explicit 88F8
  `TargetProfileId` written by the M1 target-aware service status;
- the historical 88F8 EC diagnostic probe now requires the full exact hardware
  fingerprint rather than board product alone;
- M-series installation refuses coexistence with registered historical
  `VictusFanControlWatchdogGateA`, `VictusFanControlWatchdogGateB` or
  `VictusFanControlWatchdog` services;
- `scripts/cleanup-watchdog-88f8-services.ps1` can stop/disable/delete those
  old service registrations while preserving their ProgramData logs and
  journals for forensic history.

This gate deliberately does **not** enable the 8C40 watchdog lease and does not
change `WatchdogRecoveryValidated=false`. Target identity in the named-pipe
lease handshake remains a required M4 boundary before real 8C40 watchdog
ownership is allowed.

The 8C40 backend also has an explicit regression proving that reapplying the
same already-owned setpoint verifies EC/tach feedback without issuing another
WMI `SetFanLevel` command.


M2.5 repository CI passed after this hardening. The local Windows
service-registration audit remains pending on the physical 8C40 target; until
that audit/cleanup is completed, M3 should not be executed.
