# HP 8C40 watchdog M4 - target-bound live lease qualification

Status: **M4A CODE/CI PASS / PHYSICAL PENDING.**

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

M4A uses only equal `30/30`. Endpoint lease qualification for `10/10` and
`50/50` remains M4B/M4C.

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

## Physical gate after CI

After repository CI is green:

~~~powershell
git pull
.\scripts\test-watchdog-m4a-8c40.ps1
~~~

The active token is:

~~~text
8C40-M4-LEASE30
~~~

This is one short awake `30/30` lease cycle, not a crash test and not a
Modern Standby test.

## What M4A PASS will not authorize

A physical M4A PASS still does not by itself set
`WatchdogRecoveryValidated=true`.

Still pending after M4A:

- M4B live lease at `10/10`;
- M4C live lease at `50/50`;
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

No physical M4A fan write has been executed by CI. The next gate is the single bounded awake 30/30 lease cycle in scripts/test-watchdog-m4a-8c40.ps1.
