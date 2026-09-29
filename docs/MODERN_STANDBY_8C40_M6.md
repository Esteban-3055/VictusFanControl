# HP 8C40 M6 - Modern Standby lifecycle qualification

Target: `HP-8C40-9D0R1LA-F18`.

Status: **CODE/CI PASS / NO-WRITE PREFLIGHT PASS / PHYSICAL PENDING**.

M5A through M5E have closed the awake watchdog/crash matrix. M6 is the
lifecycle gate that must be completed before
`WatchdogRecoveryValidated=true` can be considered.

Automatic/adaptive fan policy remains OFF.

## Why M6 is separate

This target uses Windows Modern Standby / S0 Low Power Idle rather than legacy
S3. The prior M0 captures showed that session-display Off is available before
the later suspend notification, and that Windows can emit resume notifications
while the session display is still Off. Therefore the historical 88F8 / S3
Gate G1/G2 path is not reused.

The qualification rules are deliberately display-aware:

~~~text
SESSION_DISPLAY_STATUS Off
  -> close Custom admission synchronously
  -> mark telemetry Suspended
  -> restore HP firmware
  -> verify local FF/FF
  -> verify watchdog Release
  -> require journal absent
  -> require stable independent FF/FF
  -> only then allow the system to remain in Modern Standby
~~~

PBT_APMSUSPEND is retained as an observation and safety-only fallback, but a
fallback boundary cannot satisfy the primary M6 PASS criterion.

## Maintenance-wake rule

A `PBT_APMRESUMEAUTOMATIC` or `PBT_APMRESUMESUSPEND` received while
`SESSION_DISPLAY_STATUS` remains Off does **not** call
`TelemetryWorker.NotifyResume` and does **not** reopen Custom admission.

The only accepted user-facing resume boundary is:

~~~text
SESSION_DISPLAY_STATUS On
  -> revalidate same watchdog PID + creation time
  -> require journal absent
  -> require stable FF/FF
  -> advance coordinator freshness fence to the display-On timestamp
  -> call NotifyResume exactly once
  -> wait for the normal five-snapshot Healthy recovery
  -> require Healthy snapshot newer than display-On
  -> reopen Custom admission
~~~

This prevents a screen-off maintenance wake from reacquiring fan ownership.

## Qualification-only 8C40 backend

Production construction is still fail-closed. The ordinary public HP 8C40
watchdog constructor and `HpFanControlBackendFactory` remain blocked while
lifecycle qualification is incomplete.

M6 has an explicit exact-token qualification factory:

~~~text
Hp8C40FanControlBackend.CreateLifecycleQualificationBackend(...)
token = 8C40-M6-MODERN-STANDBY30
~~~

It is exact-target gated and uses the real HP 8C40 backend plus the already
qualified M4 named-pipe watchdog. It does not enable automatic policy.

## Physical ownership before sleep

M6 first proves a real protected 30/30 state:

~~~text
stable FF/FF
 -> SafetyGate Healthy/light-load admission
 -> watchdog PREPARE
 -> durable WRITE_INTENT 30/30
 -> real WMI SetFanLevel(30/30)
 -> EC 30/30
 -> dual physical tach ACK
 -> watchdog COMMIT
 -> schema-v2 generation-3 OWNED 30/30
~~~

The journal must be bound to the exact GUI PID plus process creation time.

The watchdog must be LocalSystem / Session 0 / exact target and its PID plus
creation time are pinned for the whole normal sleep/resume cycle. A watchdog
restart invalidates normal-lifecycle PASS even if safety is recovered.

## Real Modern Standby evidence

The physical parent does not call `SetSuspendState`. The user initiates sleep
through Windows.

The parent records the armed time and later requires Windows
Microsoft-Windows-Kernel-Power event IDs 506 and 507 in the same test window.
The proactive FF/FF handoff marker must be timestamped no later than the first
506 sleep boundary (with a small logging-timestamp tolerance), and the final
507 must correspond to the accepted display-On wake.

The observed standby interval must be at least 15 seconds so an accidental
immediate wake cannot satisfy the gate.

## Controlled post-resume re-entry

After the display-On gate and five-snapshot Healthy recovery, M6 deliberately
reopens the coordinator fence and performs exactly one controlled 30/30
re-entry.

It again requires exact durable OWNED generation 3, then immediately performs
the normal coordinator firmware handoff and requires:

~~~text
authority = Firmware
local firmware ACK = true
watchdog Release = true
journal = absent
stable independent FF/FF = two consecutive samples
same watchdog process
~~~

No adaptive curve is enabled.

## Torn EC read handling

Recent M5 qualification demonstrated isolated asymmetric 0x34/0x35 reads such
as 255/11 and 255/1 surrounded by FF/FF evidence. M6 never accepts one such
sample as firmware ownership, but it also does not convert one sample into a
false terminal result.

Read-only firmware proof requires two consecutive FF/FF samples. An unexpected
pair must repeat before it is treated as stable conflicting evidence. This does
not add a compensating restore or fan-write path.

## Independent delayed safety fallback

Before the write-capable M6 GUI is launched, the parent arms the previously
qualified exact-target M5C OWNED-30 failsafe with a 300-second delay.

It may act only if an exact schema-v2 OWNED 30/30 journal remains. It has no
ordinary fan-target authority and no direct HP/WMI restore authority; it can
neutralize the exact journal-bound controller and invoke only the qualified M4
recovery service.

If it takes over, the hardware may be recovered safely but the M6 run cannot
PASS.

The fallback is cancelled only after journal absence plus stable independent
FF/FF have been proven.

## Cleanup ordering

Abnormal cleanup is safety-first:

~~~text
terminate exact qualification controller if still alive
 -> if journal remains, start/allow qualified M4 recovery
 -> wait for journal removal
 -> independently prove stable FF/FF
 -> only then cancel delayed fallback
 -> restore M4 Manual/stopped baseline
 -> only then collect power/sleep diagnostics
~~~

Power-report collection is intentionally deferred until firmware safety has
been independently proven.

## No-write preflight

Run first:

~~~powershell
.\scripts\test-8c40-modern-standby-m6-preflight.ps1
~~~

This stage performs no fan write, no watchdog lease, no service mutation, no
fault injection and no sleep request. It validates:

- exact HP 8C40 fingerprint;
- absent durable journal;
- M4 service is Manual/stopped if currently installed;
- complete M5/M6 static and synthetic regressions;
- M0 Modern Standby observer self-test;
- SafetyGate/coordinator/backend regressions;
- current Windows power capability output;
- two consecutive independent FF/FF samples.

Physical M6 must not be run until this preflight passes.

## Physical harness

After no-write preflight PASS:

~~~powershell
.\scripts\test-8c40-modern-standby-m6.ps1
~~~

Explicit token:

~~~text
8C40-M6-MODERN-STANDBY30
~~~

The harness will tell the operator when the real 30/30 state is READY. The
operator then presses Enter in the parent shell, immediately chooses
**Windows Start -> Power -> Sleep**, keeps the machine asleep for about one
minute, then wakes it normally.

For the first M6 cycle, do not close the lid and do not manually kill any VFC
process.

## Code/CI preparation result

The final preparation head
`adecb6af9331d93ad4f7ac766f1e145e1aee2a33` passed GitHub Actions
**#654** (run `36625953717`) on 2026-09-29.

The green run includes:

- PowerShell syntax validation;
- M5A/M5B/M5C/M5D/M5E invariants;
- M6 lifecycle invariant;
- M6 no-write/physical-harness invariant;
- the entire M5/M6 invariant set under Windows PowerShell 5.1;
- warnings-as-errors solution build;
- M0 Modern Standby self-test;
- Gate B/C and HP 8C40 M2/M3/M4 self-tests;
- SafetyGate and FanControlCoordinator self-tests;
- HP BIOS-contract and backend self-tests.

M6 physical evidence remains pending.

`WatchdogRecoveryValidated` remains **false** and automatic/adaptive policy
remains **OFF**.


## Final preparation head before no-write preflight

The safety-first cleanup ordering was locked in commit
`b312c11f3b7034f4b8ee507084e53a77d66657c1`.

GitHub Actions **#656** (run `36626491883`) completed successfully on
2026-09-29. The run passed the complete M5/M6 invariant set, Windows
PowerShell 5.1 compatibility, warnings-as-errors build, Modern Standby M0,
Gate B/C, M2/M3/M4, SafetyGate, FanControlCoordinator and HP backend tests.

This is the exact code baseline for the M6 no-write preflight. Physical M6
remains blocked until that local preflight passes.


## Physical attempt 1 - fail-closed before valid M6 resume qualification

The first M6 physical lifecycle attempt on 2026-09-29 did **not** qualify M6.

The initial write boundary was valid: the exact LocalSystem / Session 0
watchdog was pinned, the GUI reached real watchdog-backed Custom 30/30, the
backend reported WMI + EC + dual-tach acknowledgement, and the durable journal
was exact schema-v2 generation-3 OWNED 30/30.

At the session-display Off boundary the hardware was restored safely, but the
pre-sleep marker was deliberately rejected because `wasCustom=False`. The
marker still showed verified local firmware acknowledgement, watchdog Release,
journal absence and stable independent FF/FF. Service timestamps show the
RESTORE_BEGIN / RELEASE sequence had already started before the marker was
published, so the required causal proof that SESSION_DISPLAY_STATUS Off itself
captured an actively Custom-owned boundary was not established.

The same armed test window also contained Windows power-transition evidence
that is not acceptable for a Modern Standby M6 PASS:

- Kernel-Power 524 critical-battery trigger;
- Kernel-Power 42 with sleep reason Battery;
- Kernel-Power 507 reporting Resume from Hibernate.

The GUI correctly refused the display-On resume gate because the proactive
display-Off handoff had not been verified. Cleanup independently proved stable
FF/FF, cancelled the 300-second delayed fallback only after journal absence +
FF/FF, and restored the M4 service to Manual/stopped.

This attempt is recorded as **FAIL_CLOSED / PHYSICAL PENDING**. A final FF/FF
state is not treated as lifecycle qualification.

Before any second physical M6 attempt, the harness is hardened to reject
critical-battery or hibernate evidence in the armed window and to surface the
persistent VFC application log so the exact pre-Off authority transition can
be diagnosed. No production watchdog promotion or automatic policy change is
authorized by this attempt.
