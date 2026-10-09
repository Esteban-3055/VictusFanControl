# HP 8C40 Modern Standby M0 characterization

Status: **HARNESS PREPARED / PHYSICAL RESULT PENDING**

Target:

- HP Victus 15-fa1013la
- board HP 8C40 / 63.43
- SKU base 9D0R1LA
- BIOS F.18
- Windows sleep model: S0 Low Power Idle / Modern Standby

This gate is intentionally read-only. It exists to characterize the real Windows
power-lifecycle signals on the 8C40 before any watchdog-backed Custom authority
is enabled.

## Why this is a new gate

The historical 88F8 lifecycle proof was built around legacy S3. The 8C40 uses
Modern Standby and therefore must not inherit the old Gate G0/G1/G2 event,
timing or service-execution assumptions.

M0 answers the questions that later watchdog/lifecycle gates depend on:

1. Which registered user-mode power events are observed when this exact system
   enters and leaves Modern Standby?
2. Does the interactive-session display transition provide an earlier and more
   reliable safety boundary than the suspend notification?
3. How do wall time, QueryUnbiasedInterruptTime, GetTickCount64 and QPC advance
   across the real transition?
4. Do SleepStudy/System Power/System Sleep Diagnostics confirm a real Modern
   Standby session rather than only a display-off interval?
5. Which duplicate or maintenance wake notifications occur on this machine?

No answer is assumed in advance.

## Observer architecture

The dedicated project is:

`src/VictusFanControl.ModernStandbyProbe`

It owns an invisible WinForms window and explicitly registers for:

- suspend/resume via `RegisterSuspendResumeNotification` using the window
  recipient path;
- `GUID_SESSION_DISPLAY_STATUS` as the primary interactive-session display
  signal;
- `GUID_CONSOLE_DISPLAY_STATE` as comparison-only diagnostic evidence;
- `GUID_ACDC_POWER_SOURCE`;
- `GUID_LIDSWITCH_STATE_CHANGE`.

The observer records `WM_POWERBROADCAST` events including:

- `PBT_APMSUSPEND`;
- `PBT_APMRESUMEAUTOMATIC`;
- `PBT_APMRESUMESUSPEND`;
- `PBT_APMRESUMECRITICAL`;
- `PBT_POWERSETTINGCHANGE`.

For every captured event it records:

- UTC wall clock;
- `QueryUnbiasedInterruptTime`;
- `GetTickCount64`;
- QPC / `Stopwatch.GetTimestamp()`;
- event source/name/detail.

The first `GUID_SESSION_DISPLAY_STATUS Off -> On` interval is summarized in
the JSON report, but that interval is **capture evidence only**. It is not
classified as a Modern Standby PASS without independent Windows power-report
evidence.

## Non-intrusion rules

M0 does not:

- load PawnIO;
- open the ACPI EC;
- call HP fan-control WMI;
- query NVML;
- instantiate the fan backend;
- acquire a watchdog lease;
- use `SetSuspendState`;
- hold `ES_SYSTEM_REQUIRED`;
- issue any fan command;
- change a Windows power policy.

Power events are buffered in memory. The observer writes only:

1. one small READY marker after all notification registrations succeed;
2. one final JSON report after the display returns or the observer exits.

This avoids continuous diagnostic disk IO during the Modern Standby interval.

## M0A synthetic/CI gate

CI builds the new project and executes:

`dotnet run --project .\src\VictusFanControl.ModernStandbyProbe -c Release --no-build -- --self-test`

The self-test validates the stable event/GUID mappings and JSON report
round-trip without entering a power state.

A green CI result is not physical Modern Standby qualification.

## M0B physical capture

Run from an elevated PowerShell on the exact 8C40 target:

`.\scripts\test-8c40-modern-standby-m0.ps1`

Optional repeated characterization:

`.\scripts\test-8c40-modern-standby-m0.ps1 -Cycles 3`

The wrapper:

1. exact-matches board/system/SKU/BIOS using read-only CIM;
2. builds the complete solution with warnings as errors;
3. runs the M0 synthetic self-test;
4. captures `powercfg /a` and `powercfg /requests`;
5. requires the explicit `8C40-M0` confirmation;
6. starts the read-only observer;
7. waits for the READY marker;
8. asks the user to invoke Windows Sleep manually;
9. captures one registered session-display Off -> On cycle;
10. runs the shared post-transition diagnostic collector.

The collector now produces separate files for:

- System Power Report;
- System Sleep Diagnostics;
- SleepStudy;
- textual power/event diagnostics.

M0 deliberately does not call a private suspend API. The first characterization
must observe the same user-initiated Windows Sleep path that the eventual
product must survive.

## Interpretation rules

Do not close M0 from any single signal.

A useful physical characterization requires at least:

- exact-target preflight;
- notification registration success;
- session-display Off followed by On;
- complete event timeline with no dropped events;
- clock values before/after the transition;
- Windows diagnostic evidence consistent with the intended power transition.

The presence or absence of `PBT_APMSUSPEND` is itself a result to measure; the
gate must not be changed merely to force that event to appear.

Likewise, `PBT_APMRESUMEAUTOMATIC` is not interpreted as proof that the user
has returned. Later production admission will remain a separate decision.

If SleepStudy does not contain a short test interval, that is not automatically
an observer failure. Keep the JSON capture and the other Windows power reports,
then repeat with a longer sleep interval if required.

## What remains blocked

M0 does **not** change any of the current production safety blocks:

- `Hp8C40FanControlBackend` still refuses a watchdog lease;
- the backend factory still refuses watchdog-backed 8C40 control;
- `WatchdogRecoveryValidated` remains false;
- automatic/adaptive policy remains OFF;
- legacy 88F8 Gate D-G hardware harnesses remain refused on the 8C40 target.

After M0 is reviewed, the next implementation step is target-aware watchdog
cleanup and a read-only LocalSystem/Session-0 8C40 gate. No service restore or
watchdog fan ownership should be enabled before that work is complete.
