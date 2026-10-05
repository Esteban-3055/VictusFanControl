# HP 8C40 — GUI Automatic qualification using WMI only

The current GUI route for the exact HP 8C40 / F.18 / 9D0R1LA target follows the
supervised WMI experiment. PowerShell launches the gate; it is not a fan-control
backend. CPU sensing/limits still use Intel MSR/PawnIO; GPU sensing/limits use
NVML/NVIDIA. Fan commands and RPM use HP BIOS WMI exclusively. HP/Windows may
access EC internally; the application does not load or access the EC directly.

## Active route

- Fresh fan RPM: HP `20008h/2Dh`, shared broker, no EC fallback.
- Equal fan commands: `20008h/2Eh`, 30..50, only on changed target.
- No immediate RPM-as-setpoint acknowledgement and no mechanical wait loop.
- A detached WMI guardian durably arms an exclusive lease before the first write.
- Each native HP call holds the same named mutex as the experimental route.
  An in-flight/uncertain marker blocks subsequent calls. Native timeouts do not
  prove cancellation. Stop/lifecycle/admission are rechecked before dispatch.
- Guardian heartbeat follows fresh feedback checks, not a blind timer. Owner
  exit, missing progress, lifecycle gaps or new ACPI 13/15 close admission.
- Recovery sends `FF/FF -> LegacyDefault` through WMI; LegacyDefault is attempted
  even if FF/FF is rejected. Successful recovery retires the guardian lease.
- If guardian recovery fails, the GUI may attempt local release after read
  quiescence. The shared mutex/markers still fence those calls. Local acceptance
  never retires a retained guardian lease or permits re-entry.
- Direct EC access is prohibited process-wide in this GUI and guardian route.
  Historical EC GUI qualification launches are rejected before backend setup.
  Historical CLI tests and the old backend remain available as historical source;
  they are not selected by the current GUI.

WMI return code zero proves accepted requests, **not independent firmware
ownership or completion**. RPM is feedback, not commanded-setpoint readback.
The coordinator's Custom/Firmware states describe the local control lifecycle.
The new evidence gate is `HP-8C40-AUTOMATIC-WMI-NORMAL` and must not be interpreted
as a PASS of the former EC-based final gate.

## Running the physical normal-path test

Close other fan-control instances. Keep AC connected and the existing saved
qualification profile (hottest 3 P-Cores, 30..50, normal +1/-1, 1000 ms,
adaptive descent, rise 8s/3s, short descent 6s/4s, sustained descent 20s/16s,
1200s load qualification, 50%/25W/40W thresholds, 30s pause, 120s cooldown,
85C/78C demand overrides). Thermal protection is unchanged: CPU 95..98.x requires
confirmation bounded by five unique samples / 2000 ms; CPU >=99 and GPU >=87
close admission immediately.

From elevated PowerShell:

```powershell
git switch feature/victus-8c40-automatic-final-qualification
git pull --ff-only
.\scripts\test-automatic-final-qualification.ps1 -WithPerformanceLimits
```

1. Wait for READY.
2. Apply both CPU and GPU limits in Rendimiento; for this retest use CPU AC 20/40 W.
   Wait for CPU Active and GPU ActiveUnverified.
3. Click Automatic once and run representative load.
4. After at least 30 decisions and two accepted changed-target commands, click
   Firmware once. The GUI completes normal cleanup and exits.

Do not select Manual, edit settings, suspend, disconnect AC or close the GUI
during the normal-path gate. A released WMI session is permanently closed;
restart the GUI to start another supervised session. Manual and normal product
Automatic remain closed pending new physical qualification.

The harness requires IntelMSR.bin; it no longer requires LpcACPIEC.bin or launches
an EC/setpoint probe. Existing WMI GUI, experimental or M4 leases block admission.
The fan report is bound to the GUI PID/start time and unique session path. PASS
requires accepted WMI release/default requests, retired fan lease, zero denied EC
access attempts, and `IndependentFirmwareOwnershipVerified=false`. With limits,
the independent performance Guardian must additionally prove CPU/GPU release.
Failure packaging waits for normal cleanup and captures both reports when present.
Never delete a retained lease just to bypass a failed recovery.

## Verification and remaining gates

Hardware-free checks cover changed-target writes, guardian readiness before
writes, intent failure, native rejection/no retry, guardian loss, drained local
release, uncertain-native lease retention, real detached child release and real
owner-process exit. Windows CI runs `--wmi-fan-gui-self-test` using explicit
zero-hardware fixtures. The experimental WMI regression suite remains intact.

Physical WMI normal-path PASS is still pending. Subsequent tests must qualify
app/tray exit while active, telemetry loss, suspend/resume and >=20-minute loaded
operation followed by descent. Software tests do not prove hardware ownership.

## Diagnostic evidence leading to this migration

- `automatic-final-normal_2026-10-04_213320.zip`, SHA256
  `f3b6722eb25532eff092bfa1709211cf1a8a8f20a40ff4f005b714c50173cebb`:
  source 8c3f96f, AC 20/40 W, initial 30/30 confirmed, then EC setpoints FF/FF.
  The former gate initially masked this loss until a second Automatic click.
- `automatic-final-normal_2026-10-04_214835.zip`, SHA256
  `bd1799d2d287e9ff2fec6fb4507fbf567c13c49dab1bf29b69458e9b14d45551`:
  source 146d832, nine decisions, one completed command, then EC 0xEC=0x90
  during the next command acknowledgement. Last ACK sensor samples CPU 77 C /
  GPU 47 C. The error was not a thermal cutoff. The performance report proved
  normal CPU/GPU release. Immediate failure detection and report collection
  worked; the summary still used the generic fail-safe coordinator reason.

Neither run is a physical PASS. These samples do not prove whether 0x90 was a
transient EC read or real firmware activity. The earlier WMI experiment did not
read these guards; this migration adopts that explicitly different evidence model.
