# HP 8C40 M2.5 - legacy 88F8 isolation hardening

Status: **PASS. Code/CI green and physical installed-service audit clean on the exact HP 8C40 target (2026-09-28).**

M2 proved that the exact HP 8C40 target can read the future watchdog
dependencies from LocalSystem/Session 0 without changing ownership. Before M3
grants the service its first restore-only write authority, M2.5 removes or
fences historical HP 88F8/S3 assumptions that could otherwise confuse the new
M-series path.

## Scope

M2.5 is intentionally no-write. It changes startup guards, service isolation,
legacy status validation and interim Modern Standby admission behavior.

It does not issue fan commands, does not restore firmware and does not enable
the 8C40 watchdog lease.

## Legacy GUI harness isolation

The following application modes belong only to the historical
`HP-88F8-62C37LA-F32` target:

- `--suspend-custom-test`;
- Gate D;
- Gate E;
- Gate F1;
- Gate F2;
- Gate G1;
- Gate G2.

Startup now exact-matches the 88F8 profile before MainForm or a write-capable
backend is created. A second MainForm guard preserves the same rule if the form
is ever instantiated from another entry point.

## Interim Modern Standby rule

M0 showed that `PBT_APMRESUMEAUTOMATIC` can occur during a screen-off
maintenance wake. Therefore the old S3 rule "Healthy after resume => reopen
Custom admission" is not valid for HP 8C40.

Until the display-aware M-series lifecycle is integrated, the generic
`ReopenFanAdmissionAfterHealthyAsync` path refuses to reopen Custom admission
when the resolved target uses `ModernStandbyS0LowPowerIdle`.

This is deliberately conservative: after a lifecycle boundary the 8C40 can
remain fenced until restart rather than regain Custom authority during a
maintenance wake.

## Legacy watchdog status isolation

The historical Gate G reader now consumes `TargetProfileId` from Gate D
status and requires exactly `HP-88F8-62C37LA-F32`. A status document from
another target can no longer satisfy the legacy Gate G Ready check.

This is separate from the future M-series reader; the 8C40 path must not reuse
the Gate G reader blindly.

## Installed-service isolation

Historical validation/service registrations use:

~~~text
VictusFanControlWatchdogGateA
VictusFanControlWatchdogGateB
VictusFanControlWatchdog
~~~

The Gate D service was historically configured as automatic start with SCM
restart actions. Although its worker exact-matches 88F8 before opening its
hardware adapter, leaving that registration installed on the 8C40 creates
unnecessary startup/restart noise and ambiguous ownership of the watchdog
namespace.

M-series installation now refuses while any historical service registration is
present.

Use elevated PowerShell:

~~~powershell
.\scripts\cleanup-watchdog-88f8-services.ps1 -AuditOnly
~~~

to inspect only, or:

~~~powershell
.\scripts\cleanup-watchdog-88f8-services.ps1
~~~

to stop, disable and delete the historical service registrations. Their
ProgramData evidence is deliberately preserved.

## Fan-command persistence / redundant-write behavior

The HP 8C40 production backend commands a **fan level**, not an exact RPM.
Physical qualification observed roughly 100 RPM per level in much of the
usable range, but RPM remains feedback rather than the WMI command unit.

For each accepted target change the backend performs one HP BIOS/WMI
`SetFanLevel(cpu,gpu)` dispatch, then verifies ownership through EC
`0x34/0x35` and verifies physical response through both tachometers.

The EC setpoint is persistent ownership state. The backend does not need to
continuously resend the same WMI command. If `ApplyAsync` is called again with
the same target and EC already contains that target, the backend skips
`SetFanLevel` and performs read-only acknowledgement/feedback verification.

A new self-test pins that behavior: two consecutive `30/30` Apply calls must
produce exactly one WMI SetFanLevel call.

## Remaining boundary

M2.5 still leaves these intentionally blocked:

- `WatchdogRecoveryValidated=false`;
- real 8C40 watchdog lease;
- service-side 8C40 restore;
- crash/double-death recovery;
- display-aware Modern Standby custom lifecycle;
- automatic/adaptive fan policy.

After M2.5 CI and local legacy-service cleanup/audit, M3 may qualify one
restore-only LocalSystem transition from a known VFC-owned equal `30/30`
state back to verified firmware `FF/FF`.


## CI result

M2.5 passed the full repository CI after the isolation changes. The dedicated
isolation invariant gate passed, the solution built with warnings as errors,
all historical Gate E/F/G invariants remained green, M0/M1/M2 self-tests
remained green, and the HP 8C40 backend regression confirmed that a repeated
same-owned setpoint does not issue a redundant WMI `SetFanLevel` command.

The physical 8C40 audit was completed with `cleanup-watchdog-88f8-services.ps1 -AuditOnly` and reported `PASS: no historical 88F8 watchdog services are installed.` No cleanup action was required. Historical ProgramData evidence therefore remained untouched.


## Physical installed-service audit

The exact HP 8C40 machine completed the M2.5 local service-registration audit
on 2026-09-28 using:

~~~powershell
.\scripts\cleanup-watchdog-88f8-services.ps1 -AuditOnly
~~~

Result:

~~~text
PASS: no historical 88F8 watchdog services are installed.
~~~

Therefore none of the historical service registrations
`VictusFanControlWatchdogGateA`, `VictusFanControlWatchdogGateB` or
`VictusFanControlWatchdog` is present on the current 8C40 Windows install.
No stop/disable/delete operation was necessary and no ProgramData evidence was
modified.

This closes M2.5. M3 may now be prepared as a separate restore-only
qualification gate.
