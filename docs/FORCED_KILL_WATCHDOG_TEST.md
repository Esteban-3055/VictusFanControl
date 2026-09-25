# Forced-process termination / EC countdown-watchdog hardware gate

Status: **physical characterization completed on 2026-09-24**.

Automatic fan policy remains OFF.

## Purpose

This gate measures the failure mode that managed cleanup cannot protect: the
VictusFanControl GUI is force-killed while it owns a validated 30/30 fixed fan
command. The test deliberately prevents `DisposeAsync`, `finally`, WinForms
shutdown handling and the normal coordinator restore from running.

The observer does not write EC 0x62 or 0x63. It records:

- EC 0x34 / 0x35 fixed setpoints;
- EC 0x62 manual flag;
- EC 0x63 countdown;
- CPU/GPU tachometer RPM.

An independent delayed PowerShell restore is armed before the kill so a bounded
external `FF,FF -> LegacyDefault` cleanup remains available even if the parent
test shell fails.

## Physical result

The GUI reached validated READY with authority `Custom` and EC 30/30. The
wrapper then force-killed only `VictusFanControl.App`; managed cleanup could
not execute.

Immediately after death, EC remained 30/30 and both fans converged to roughly
3000 RPM. The countdown fell normally from 238 toward 211, then jumped:

```text
T+28.1 s  set=30/30  countdown=239   (previous sample: 211)
```

The same pattern repeated:

```text
T+58.0 s  set=30/30  manual=0x07  countdown=210
T+59.1 s  set=30/30  manual=0x06  countdown=239
```

The harness therefore classified the run:

```text
EXTERNAL_COUNTDOWN_REFRESH_WITH_SETPOINT_30_30
```

Natural FF/FF recovery was not observed during the 59.1-second characterization
window.

The identity of the refreshing component was **not** proven. OMEN Gaming Hub was
open in the validated coexistence configuration, and the behavior is consistent
with an HP/OMEN-side controller maintaining the firmware manual/countdown state.
VictusFanControl itself was already dead.

## Cleanup result

The parent harness executed the already validated release path:

```text
SetFanLevel(FF,FF)
FanMode=LegacyDefault
```

BIOS/WMI reported success and EC 0x34/0x35 returned to FF/FF. The independent
300-second failsafe was then cancelled because the final read-only verification
confirmed firmware ownership.

OMEN Gaming Hub CPU undervolt was manually checked after the run and was
unchanged.

## Engineering conclusion

EC 0x63 must **not** be treated as the crash watchdog for VictusFanControl in
this configuration. A dead VFC process can leave its last fixed fan setpoint
installed while another component refreshes the countdown.

This does not make the fixed-level backend unsafe for bounded supervised tests;
the explicit restore path is proven. It does mean unattended automatic control
cannot be enabled until a second, independent liveness owner exists outside the
GUI/controller process.

See `CRASH_WATCHDOG_DESIGN.md`.
