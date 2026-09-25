# Watchdog Gate B - service-only emergency restore

Status: implementation ready; physical validation pending.

Gate B validates the only hardware write authority the future watchdog service
is allowed to have: return an explicitly VFC-owned fixed setpoint to HP firmware
authority.

## Scope

The required physical path is:

~~~text
validated VFC coordinator/backend -> Custom 30/30
        |
        | exact GUI process is force-killed
        v
orphaned EC 30/30
        |
        | start LocalSystem Gate B service
        v
SetFanLevel(FF,FF)
        ->
FanMode=LegacyDefault
        ->
service verifies EC 0x34/0x35 == FF/FF
        ->
independent parent probe verifies FF/FF
~~~

This test deliberately kills the GUI after its existing 30/30 hardware path has
reported READY. That prevents the GUI/coordinator from participating in the
restore, so a Gate B PASS proves the service itself performed the handoff.

## Safety boundaries

Before any 30/30 write, the wrapper:

- builds the entire solution with warnings as errors;
- runs SafetyGate, coordinator, HP backend and Watchdog Gate B synthetic tests;
- requires live telemetry backends;
- requires initial EC FF/FF;
- requires explicit user confirmations;
- installs the Gate B service but leaves it stopped;
- arms an independent delayed fallback.

The delayed fallback is ownership-safe. After its delay it reads EC first:

- FF/FF -> no-op;
- exactly 30/30 -> invoke the already validated CLI HP-auto restore;
- any other fixed pair -> refuse to clear ambiguous external ownership.

The parent wrapper also performs immediate cleanup on any failure after the
hazardous window begins.

## Service restrictions

The Gate B service:

- must run in Session 0;
- must run as LocalSystem;
- must match the exact validated HP target;
- requires EC to be exactly 30/30 before it will write anything;
- refuses Max Fan or unexpected fan-switch state;
- has no API to set ordinary 14..50 fan levels;
- calls only the existing RestoreFirmwareAuto primitive;
- polls EC for FF/FF for up to 5 seconds;
- records a machine-level JSON result and exits.

A BIOS/WMI error followed by physical FF/FF recovery is recorded as hardware
safe but still does **not** pass Gate B; the strict gate requires both the
restore call and EC verification to succeed.

## Run

Use elevated PowerShell, under light load:

~~~powershell
git pull
.\scripts\test-watchdog-gate-b.ps1
~~~

Keep OMEN Gaming Hub open with the normal undervolt. Close OmenMon,
OmenMon-Reborn and any normal VictusFanControl GUI.

The two confirmations are:

~~~text
UNDERVOLT-OK
GATEB30
~~~

After the service restores FF/FF, confirm the OMEN Gaming Hub undervolt with:

~~~text
SAME
~~~

## PASS criteria

A physical PASS requires:

- validated READY at Custom 30/30;
- exact VFC GUI PID force-killed;
- orphaned EC remains 30/30 before service start;
- service runs as SYSTEM in Session 0;
- service sees 30/30 before restore;
- RestoreFirmwareAuto reports success;
- service verifies FF/FF;
- independent parent probe verifies FF/FF;
- delayed fallback is cancelled only after FF/FF verification;
- user confirms OGH undervolt is unchanged.

Gate B does not implement a lease, heartbeat or named pipe. Those remain Gate C
and later work.
