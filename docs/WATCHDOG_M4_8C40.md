# HP 8C40 watchdog M4 - target-bound live lease qualification

Status: **M4A CODE/CI/PHYSICAL PASS. Post-physical hardening CI PASS. M4B/M4C CODE/CI PASS / PHYSICAL PENDING.**

M3 physically qualified the LocalSystem restore-only primitive. M4 is the next
separate authorization boundary: durable watchdog lease ownership around a real
8C40 Custom command while awake.

Production remains blocked. M4 uses a qualification-only service and an
internal bounded 8C40 backend path; the public production factory and public
8C40 backend constructor continue to reject watchdog leases.

## Range policy remains unchanged

M4 does not extend the production fan range.

The exact HP 8C40 target remains:

- equal CPU/GPU only;
- minimum level 10;
- maximum level 50;
- no level 0/fan-stop command;
- no asymmetric commands;
- automatic/adaptive policy OFF.

M4A uses equal `30/30` and is physically complete. M4B uses equal `10/10`
and M4C uses equal `50/50`. All three use the same bounded
qualification-only route, exact target binding, restore-only service authority
and fail-closed journal semantics; each endpoint retains its own explicit
acknowledgement token.

## Protocol v2 target binding

The privileged named-pipe lease protocol is promoted to protocol version 2.

Every request and response carries an exact `TargetProfileId`. The server
rejects a Hello whose target id differs from the active
`WatchdogLeaseManager.TargetProfileId` before any lease mutation.

The 8C40 qualification service uses an isolated pipe:

~~~text
VictusFanControl.Watchdog.M4.8C40.v2
~~~

The historical 88F8 path uses the normal v2 control pipe. This keeps the M4
qualification namespace separate while retaining one shared, target-aware wire
codec.

## M4 service authority

`VictusFanControlWatchdogM4` runs:

- as LocalSystem;
- in Session 0;
- demand/manual start;
- exact target `HP-8C40-9D0R1LA-F18`;
- with the HP 8C40 target-aware journal policy;
- with the isolated M4 pipe.

Its hardware adapter implements only:

~~~text
ReadSetpointAsync()
RestoreFirmwareAutoAsync()
~~~

There is no service-side `SetFanLevel` method.

The restore primitive is the one physically qualified in M3:
`FF/FF -> LegacyDefault -> EC FF/FF verification`.

## M4A physical sequence

The bounded controller route is:

~~~text
SafetyGate
  -> FanControlCoordinator
  -> qualification-only real Hp8C40FanControlBackend
  -> target-bound NamedPipe lease client
  -> PREPARE
  -> WRITE_INTENT(30/30)
  -> real WMI SetFanLevel(30/30)
  -> exact EC + dual-tach ACK
  -> COMMIT
  -> four awake safety/ownership supervision samples
     (Probe before EC, Heartbeat only after healthy EC/tach validation)
  -> RESTORE_BEGIN
  -> controller local FF/FF -> LegacyDefault
  -> controller verifies FF/FF
  -> watchdog Release normalizes/verifies restore independently
  -> durable journal deleted
  -> coordinator Firmware authority
~~~

The service also runs the existing owner-process monitor and lease deadline
loop. M4A normal completion does not intentionally kill either process; those
failure domains belong to M5.

## Fail-closed properties

Before any controller WMI fan write, the durable `WRITE_ARMED` transition
must have succeeded.

The service preserves an unexpected fixed setpoint because the lease manager
restores only if EC is firmware-owned or matches a setpoint allowed by the
durable lease.

Named-pipe transport loss while the exact controller is still alive retains the
durable lease. Proven controller death or deadline expiry invokes the
M3-qualified restore primitive.

Normal controller restore does not consider local FF/FF alone sufficient to
delete the journal. `Release` asks the service to normalize/verify the complete
restore primitive and only then clears durable ownership.

## Synthetic gate

Run:

~~~powershell
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m4-8c40-self-test
~~~

The M4 gate pins:

- restore-only service hardware authority;
- equal-only 10..50 8C40 lease policy;
- protocol v2 target identity;
- isolated 8C40 M4 pipe.

Gate C additionally tests wrong-target Hello rejection before journal mutation.

## M4A physical result

M4A was physically executed on 2026-09-28 on the exact target and passed the
bounded awake `30/30` lease cycle.

Observed evidence:

- clean firmware baseline `FF/FF`;
- LocalSystem / Session 0 M4 service, exact target id and isolated v2 pipe;
- PREPARE before authority acquisition;
- real `30/30` command with EC acknowledgement and both physical tachometers;
- four consecutive awake Probe/Heartbeat supervision samples at `30/30`;
- RESTORE_BEGIN followed by local firmware handoff;
- watchdog Release acknowledgement after its independent restore normalization;
- final independent EC probe `FF/FF`;
- no retained durable journal.

The physical run exposed one diagnostic-only defect: after a successful Release,
the subsequent normal pipe EOF could be logged as if a durable lease were still
retained merely because the controller process remained alive briefly. The
lease was in fact already cleared. The server is now hardened to query active
lease ownership before emitting the retention message, and the synthetic Gate D
test pins both sides: an active OWNED lease must still be retained across live
transport loss, while post-Release EOF must report that no lease remains.

The installer and M4A harness are also hardened to refuse an existing
`lease.json` instead of deleting durable ownership evidence during setup.

## What M4A PASS does not authorize

A physical M4A PASS still does not by itself set
`WatchdogRecoveryValidated=true`.

Still pending after M4A:

- M4B physical live lease at `10/10`;
- M4C physical live lease at `50/50` after M4B is reviewed;
- M5 GUI/controller death, watchdog death and double-death failure domains;
- M6/M7 Modern Standby proactive release/reacquisition lifecycle;
- explicit hibernation/race gates;
- automatic/adaptive policy.

Production watchdog construction stays blocked until the intended qualification
boundary is explicitly promoted.


## M4A CI preparation result

M4A preparation passed the complete repository CI after the target-bound protocol migration was reconciled with the historical 88F8 Gate G invariants and client self-tests.

The green run includes PowerShell syntax validation, historical Gate E/F/G0/G1/G2 invariants, HP 8C40 legacy-88F8 isolation, warnings-as-errors solution build, M0, M1/Gate C, M2, M3, M4 self-tests, SafetyGate, FanControlCoordinator, HP BIOS contracts and both HP backend self-tests.

The protocol-v2 migration pins TargetProfileId in both request and response. Historical 88F8 Gate G remains explicitly bound to the 88F8 target id, while M4A uses the isolated 8C40 pipe and exact 8C40 target id.

CI does not execute physical fan writes. M4A physical evidence now exists from the bounded awake 30/30 run on 2026-09-28. The next physical gates remain M4B at 10/10 and M4C at 50/50 after their code preparation and CI review are complete.


## M4B/M4C endpoint preparation

The bounded M4 qualification runner accepts only the three explicit gates:
`10`, `30` and `50`. Arbitrary qualification levels are rejected.

The public qualification CLI maps exactly:

~~~text
--8c40-m4-lease10  + 8C40-M4-LEASE10  -> M4B
--8c40-m4-lease30  + 8C40-M4-LEASE30  -> M4A
--8c40-m4-lease50  + 8C40-M4-LEASE50  -> M4C
~~~

User-facing wrappers are:

~~~powershell
.\scripts\test-watchdog-m4a-8c40.ps1
.\scripts\test-watchdog-m4b-8c40.ps1
.\scripts\test-watchdog-m4c-8c40.ps1
~~~

All wrappers share `test-watchdog-m4-8c40.ps1`. The shared runner preserves
the existing build/self-test preflight, clean FF/FF baseline, refusal to delete
or overwrite a retained durable journal, exact LocalSystem/Session-0 target
service checks, PREPARE/WRITE_INTENT/COMMIT ordering, four awake
Probe/Heartbeat supervision samples, RESTORE_BEGIN/Release, independent final
FF/FF verification and mandatory absence of the journal on normal completion.

This endpoint preparation does not enable the public production watchdog and
does not enable automatic/adaptive policy.


## M4B/M4C endpoint preparation CI result

The endpoint-preparation commit `5f723b74ba4c9740d78937deed8a38f0969228ff`
passed the complete repository GitHub Actions run **#579** on 2026-09-28.

The green run includes PowerShell syntax validation, Gate E/F/G0/G1/G2
invariants, legacy 88F8 isolation, warnings-as-errors build, M0, Gate B/C,
M2/M3/M4 self-tests, SafetyGate, FanControlCoordinator, HP BIOS contracts and
both HP backend self-tests.

Therefore M4B and M4C are code/CI prepared. Their physical endpoint evidence
remains intentionally separate and pending.


## M4B physical attempt 1 - fail-closed before write

The first physical M4B attempt on 2026-09-28 did not dispatch a fan write.
The exact target, LocalSystem/Session-0 service, clean FF/FF baseline and PREPARE
all succeeded. The first custom command then failed inside the backend's
pre-write admission path and was classified as a no-write
`FanControlAdmissionException`.

The watchdog rollback completed: no durable lease remained, the service stayed
Ready, and the corrected post-close diagnostic reported that no active durable
lease remained for the still-live controller process. Therefore this attempt is
not an M4B endpoint qualification PASS and does not qualify 10/10.

The diagnostic path is hardened before retrying M4B:

- first-command failures now report the exact pre-write admission stage and
  original exception type/message;
- the watchdog log emits successful PREPARE, WRITE_INTENT, COMMIT and
  RESTORE_BEGIN transition acknowledgements;
- qualification failures print the nested exception chain and a read-only EC
  control-state snapshot;
- the PowerShell harness performs an additional independent read-only EC
  setpoint probe on the failure path.

No control authority or fan range is expanded by this diagnostic hardening.
