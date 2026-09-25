# Watchdog Gate B - service-only emergency restore

Status: **physical validation PASSED on 2026-09-24**. The first attempt stopped safely during 30/30 arming because of avoidable EC contention; after removing the redundant Custom-time probe, the second attempt passed end-to-end.

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

## First physical attempt: EC contention before Gate B

The first physical attempt did not reach the service restore phase. Custom authority was acquired, but the continuous safety supervisor then reported a backend health/ownership probe failure because it timed out waiting for `Global\Access_EC`. The coordinator failed closed and restored HP firmware authority, so the test ended safely at FF/FF before the Gate B service was started.

The arming harness still contained an unnecessary full `Hp88F8EcControlStateProbe` immediately after `FanControlCoordinator.ApplyAsync`. That reader bypassed the backend's own IO gate and competed for the same global EC mutex used by the production ownership supervisor. The log does not identify the exact mutex holder, so this is not treated as proof that the extra probe was the only possible contender. It was nevertheless an avoidable concurrent EC access at exactly the failure boundary.

The correction is narrow: successful production `ApplyAsync(30/30)` is now the READY acknowledgement because the HP backend returns only after EC setpoint acknowledgement plus two-fan tachometer acknowledgement. No extra full EC snapshot is opened while Custom remains active. The Gate B wrapper now waits until the GUI/controller process is gone before performing the independent orphaned-30/30 EC verification.

No SafetyGate rule, thermal threshold, telemetry watchdog, backend ACK criterion, or firmware-restore behavior was weakened. If the same ownership-probe timeout recurs after this correction, the next investigation is general telemetry-vs-backend EC arbitration rather than relaxing safety.

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


## Physical PASS - 2026-09-24

The corrected second run passed the complete Gate B contract.

Observed sequence:

- initial firmware-owned EC setpoint was FF/FF;
- production coordinator/backend reported READY at Custom 30/30 with
  `ack=backend-ec+tachs`;
- the exact VictusFanControl.App process was force-killed, preventing managed
  cleanup;
- the independent post-kill probe confirmed the orphaned EC setpoint was still
  30/30;
- the LocalSystem service started in Session 0 and independently observed 30/30;
- the service's fixed restore primitive returned success for
  `FF,FF -> LegacyDefault`;
- the service verified EC 0x34/0x35 == FF/FF;
- service-side elapsed restore/verification time was approximately 443 ms;
- a separate parent-process EC probe independently re-confirmed FF/FF;
- the delayed fallback was cancelled only after that independent verification;
- OMEN Gaming Hub undervolt was user-confirmed unchanged.

The service log also showed the expected manual/countdown coexistence behavior:
immediately after restore it observed `manual=0x07 countdown=240`, while the
separate parent probe moments later saw `manual=0x06 countdown=239` with EC
setpoints still FF/FF. This reinforces the prior conclusion that 0x62/0x63 are
externally maintained HP/OMEN-side fields and are not VFC ownership evidence.

Gate B is complete. The next watchdog milestone is Gate C: implement and test
the lease/journal/IPC state machine entirely with fake hardware before wiring it
into the real write boundary.
