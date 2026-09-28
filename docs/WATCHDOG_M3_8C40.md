# HP 8C40 watchdog M3 - LocalSystem restore-only qualification

Status: **CODE/CI PASS / PHYSICAL RESULT PENDING.**

M3 is the first HP 8C40 M-series gate that grants the LocalSystem service a
real hardware write. That authority is deliberately narrower than the future
watchdog: the service can only restore one fresh, known VFC-owned equal
`30/30` state back to firmware ownership.

M3 does not grant ordinary fan-level control and does not enable the watchdog
lease.

## Qualified transition under test

The only active sequence permitted by the M3 harness is:

~~~text
firmware FF/FF
    ->
interactive/elevated VFC armer writes equal 30/30 once
    ->
EC + dual-tach acknowledgement
    ->
fresh durable one-shot handoff
    ->
LocalSystem / Session 0 service claims handoff
    ->
service independently verifies exact target + live armer + stable EC 30/30
    ->
one RestoreFirmwareAuto transaction
    ->
FF/FF release -> LegacyDefault
    ->
EC FF/FF verification
~~~

No second service-side restore is attempted if post-restore EC evidence changes
to an unexpected fixed setpoint.

## Why a handoff record is required

Seeing `30/30` in EC is not sufficient proof that VictusFanControl owns that
state. An external HP/OMEN component could theoretically select the same level.

The M3 armer therefore publishes a short-lived durable record only after it:

- exact-matches `HP-8C40-9D0R1LA-F18`;
- observes a clean `FF/FF` firmware baseline;
- passes the current SafetyGate/light-load/AC-battery checks;
- issues the single equal `30/30` qualification command;
- observes stable `30/30` ownership, valid guards and both physical fans.

The handoff contains:

- schema version;
- exact target profile id;
- random arm run id;
- random nonce;
- armer PID;
- armer process start UTC ticks;
- creation timestamp;
- expected `30/30`;
- proof that the pre-arm baseline was `FF/FF`;
- guard and tachometer values observed at arm time.

The service atomically renames the handoff to a unique `.claimed.<run>.json`
path before using it. Claimed evidence is retained for diagnosis.

## Service-side fail-closed checks

Before its single restore transaction, the M3 service requires all of:

- LocalSystem SID `S-1-5-18`;
- Windows Session 0;
- exact target `HP-8C40-9D0R1LA-F18`;
- signed `LpcACPIEC.bin` present;
- supported handoff schema;
- handoff target id exact match;
- handoff age at most 15 seconds;
- non-empty run id/nonce and valid process identity;
- armer PID still alive with exactly matching start time;
- handoff expected level exactly `30/30`;
- handoff baseline marked `FF/FF`;
- handoff guard values zero and dual-fan RPM plausible;
- two consecutive service-side EC samples at `30/30`;
- service-side `MaxFan=0` and `FanSwitch=0`;
- both service-side tachometers above the conservative floor.

If EC is already `FF/FF`, is asymmetric, is another fixed value, or any guard
is unexpected, the service performs **no restore write**.

## Restore primitive

The service hardware interface contains only:

~~~text
ReadControlEvidence()
RestoreFirmwareAuto()
GetCurrentFanLevels()
~~~

There is no `SetFanLevel` operation on the M3 service interface.

`RestoreFirmwareAuto()` is the existing HP 8C40 qualified primitive:

~~~text
SetFanLevel(FF,FF)
then
FanMode=LegacyDefault
~~~

After that one transaction, M3 polls the narrow EC ownership pair until
`FF/FF` is verified or the bounded timeout expires.

`GetFanLevel` remains informational effective fan-level telemetry and is not
used as restore acknowledgement.

## Interactive armer fallback

The elevated armer stays alive while the service handles the handoff.

If the service fails or no result arrives, the armer performs a local fallback
**only if EC still contains exactly the VFC-owned `30/30` state**.

If EC is already `FF/FF`, fallback issues no write.

If EC changed to any unexpected/asymmetric fixed value, fallback refuses to
clear it and preserves the possible external owner.

This keeps the physical M3 qualification bounded even if SCM/service startup or
result publication fails.

## Synthetic CI gate

Run:

~~~powershell
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m3-8c40-self-test
~~~

The synthetic M3 gate verifies:

- service hardware surface has restore-only authority and no `SetFanLevel`;
- a valid fresh `30/30` handoff restores exactly once;
- stale handoff is no-write;
- wrong target is no-write;
- dead/mismatched armer identity is no-write;
- pre-restore `FF/FF` is a no-write refusal;
- unknown fixed override is preserved;
- invalid control guards are no-write;
- a reported WMI error can still physically reach `FF/FF` but does not PASS;
- successful WMI return without EC `FF/FF` acknowledgement does not PASS.

## Physical gate

After CI is green, run on the exact HP 8C40 machine from elevated PowerShell:

~~~powershell
git pull
.\scripts\test-watchdog-m3-8c40.ps1
~~~

The wrapper first rebuilds and reruns M3/backend/BIOS synthetic gates and
requires a clean interactive `FF/FF` baseline.

Before the active write it requires the exact acknowledgement token:

~~~text
8C40-M3-RESTORE30
~~~

The physical test is a single short transition, not a soak.

## What an M3 PASS does not authorize

Even after a physical M3 PASS:

- `WatchdogRecoveryValidated` remains `false`;
- the production 8C40 backend still rejects a watchdog lease;
- the named-pipe watchdog protocol is not yet authorized for 8C40;
- service-side ordinary fan-level writes remain unavailable;
- GUI death/watchdog death/double-death recovery remains unqualified;
- Modern Standby Custom lifecycle remains unqualified;
- automatic/adaptive fan policy remains OFF.

M4 is a separate boundary. Before real watchdog-backed ownership is allowed,
M4 must add exact target identity to the controller/service handshake and then
qualify bounded live lease ownership.


## CI preparation result

The complete M3 preparation passed repository CI before any physical M3 write
was requested. The M3 synthetic gate passed together with M0, M1/Gate C, M2,
legacy-88F8 isolation, SafetyGate, coordinator, BIOS contracts and both HP fan
backends.

The M3-specific CI cases passed for restore-only interface isolation, exact
fresh 30/30 handoff, stale/wrong-target/dead-armer no-write behavior, external
override preservation, guard refusal, reported WMI failure semantics and
mandatory EC FF/FF acknowledgement.

Physical M3 remains pending and is still required before the service-side
restore boundary can be considered qualified.
