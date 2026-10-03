# HP 8C40/F.18 WMI control tachometer proof

## Evidence and scope

Actual target capture `Victus-WMI-Evidencia_2026-10-02_224321_d57ef7.zip`,
SHA-256 `3fba1c4a4df7b35648256c922b1e22980dbf56512642f91a9732d9c6f14425f4`,
has 56 verified manifest entries, 130 successful 20008h/2Dh RPM calls and
39 successful 20008h/26h Max Fan bit reads. RPM query median is 356 ms,
maximum 414 ms; these include cold discovery and are not a cached-client
production benchmark. AC and 100% battery were stable during this 266-second
capture; historical System power events precede it.

Ten sequential WMI -> EC -> WMI comparisons span 2.94--4.08 seconds each.
CPU and GPU each fall inside the observed combined WMI bins in 8/10 cases.
CPU mismatches are 13 and 16 RPM above the bin; GPU mismatches 4 and 15 RPM.
Timing variation is a possible explanation, not established measurement
identity. All EC baselines are FF/FF with raw guards 00/00. All Max Fan bits
are zero; this neither validates its active state nor replaces the raw guard.
The capture validates read-only availability only, not command response,
ownership, restoration, suspend/resume, or a cause of the earlier power glitch.

The actual target GM2D AML divides each physical tachometer by 100. The app
therefore treats level L as nominal 100*L RPM and interval [100*L,100*L+99].
WMI mediates firmware access through the Windows ACPI stack; firmware still
accesses EC internally. No equivalent full ownership/raw-guard getter was
identified. Keep direct narrow reads of 34/35 and EC/F4. The on-demand EC
probe remains available as diagnostics; it is never a production RPM fallback.

## Production implementation

`Hp8C40FanHardware.ReadEcStateAsync` acquires a new read-only WMI 2D sample
first, then reads narrow EC ownership/guards without holding the EC mutex
across WMI. It never reads B0--B3. Current-speed admission derives levels from
that freshly acquired snapshot instead of adding a synchronous WMI call.
Pre-write and post-WriteIntent checks re-read EC ownership/guards while reusing
only this fresh pre-command RPM baseline (age must remain <3000 ms). They do
not add redundant WMI calls or count as response confirmation. An expired
baseline refuses dispatch instead of refreshing it inside WRITE_INTENT.
A final guard snapshot carries the original RPM query identity into response
proof; all confirmation reads independently invoke a new WMI query.

`HpWmiFanProofReader` has no result cache. It shares the native read admission
slot with periodic telemetry and its quiescence path. Admission plus awaited
native acquisition is bounded to 3000 ms; age starts before connection and
invocation. Native WMI cannot be forcibly canceled: a timed-out or canceled
caller stops waiting while the worker retains the slot until it finishes.
No replacement query overlaps, and late completion cannot update a cache or
satisfy a later command. Errors preserve fail-closed behavior and never open
an EC fallback. Read-only shared admission does not encompass existing fan
writes, whose route and restore protocol are unchanged.

Each command needs two consecutive qualifying queries started after command
completion, with strictly increasing sequence identity, fresh acquisition age,
and current ownership/raw guards. Both fans must qualify in each sample.
The 8-second active-time response budget also bounds waiting inside a query.
No success is accepted after that deadline. Level deadband and low-speed
floor semantics remain; interval bounds make their application conservative.

For a required increase: current minimum >= baseline maximum + 150 RPM.
For a decrease: current maximum + 150 RPM <= baseline minimum, subject to
the existing floor exception only when the entire baseline bin is <=1500.
Thus 2600 -> 2800 nominal guarantees only 101 RPM and is refused; 2600 ->
2900 nominal guarantees 201 RPM. A zero nominal speed cannot prove running
fans; the entire accepted running bin must remain inside the RPM ceiling.

Firmware restoration remains FF/FF -> LegacyDefault with EC setpoint ack
and watchdog release. It does not acquire the RPM query slot. A synthetic
real-coordinator test verifies restoration while a timed-out read still owns
that slot. Windows/firmware may impose additional native serialization; this
software test does not qualify hardware restoration during a real WMI hang.
The installed M4 service, 88F8 backend, fan-write methods and thermal thresholds
are unchanged. P16 lifecycle closure is HEAD b3dde435f7579bf8e2d25831af36e7297254527c,
full Windows CI #1204 / run 37088750108 SUCCESS. All hardware gates stay closed.

## Validation and next physical work

Five fake-transport query cases extend the existing eight telemetry cases.
Ten additional 8C40 backend cases cover fresh and duplicate/pre-command
proofs, timeout, coarse increases/decrease and real-coordinator restoration
with a blocked native read. Existing ownership, WRITE_INTENT/Commit, lease,
transient, spin-up and cancellation regressions remain in the backend suite.
Only test constructors inject a portable clock; production keeps Windows
active-time accounting. Full same-head Windows CI and a separate closure CI
are required before this becomes a software baseline.

Before physical P16: first validate ordinary-app read-only telemetry on the
new build, review acquisition ages/quiescence, then prepare a fresh one-shot
authorization on exact source/CI identity. The prior generation stays consumed.
The final physical sequence remains Firmware -> Manual -> Apply 30 -> Apply 40
-> Apply 30 -> Firmware -> tray Exit. Response and restore evidence require
an audit and a separate re-block/closure before any permanent Manual promotion.
Automatic remains closed. No physical test is performed by this implementation.
