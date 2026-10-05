# Fan, performance and GUI integration checkpoint

Date: 2026-10-04. Integration branch: `integration/fan-performance-gui`.

## Source commits and completed work

- Adaptive fan dashboard: `e1b199b55ea9e95bc83b77b7a6f7bbff7002e6d1`.
- Qualified performance foundation: `02de09a9d061e798ea6a761f2e83197b311f4459`.
- Merge checkpoint: `82587e3dac09b4a75d0e731e77bef45f1aeb728e`.

The merge preserves both parents and leaves the original feature branches unchanged.
`MainForm` keeps the configurable dashboard and its Automatic snapshot processor.
It constructs `PerformanceControlSurface` and exposes it as Rendimiento.
Both Windows dashboard and performance-settings checks remain in build.yml.
The build and cpu-rapl workflows now include integration branches.
`DashboardSelfTest` includes Rendimiento navigation, four CPU preference sliders,
zero fan commands, unchanged Firmware mode and a performance-page render.

No hardware authority was promoted. No GUI performance session is started.
No changes were made to firmware, physical thresholds, RAPL write semantics,
NVML ownership semantics, Guardian release logic or qualified presets.

## Latest experiment evidence

Source: `VFC-WMI-Diagnostico_2026-10-04_182005_7405f3.zip`.
SHA256: `4a6bb8b4c4b5a7c33ac26bbc50ac9605a0badaca25a663fd39eec103a46161db`.
Matches its supplied checksum. Captured source commit: `e1b199b`.

- HottestPerformanceCoresAverage, N=3; adaptive descent enabled.
- Rise EMA 8 s; normal rise confirmation 3 s; normal up step 1.
- Brief-load fall EMA 6 s; down confirmation 4 s; down step 1.
- Sustained-load criterion 1200 s; sustained fall EMA 20 s/confirmation 16 s.
- 123 decisions, 8 commands, levels 30..35.
- Peak level 35 at 21:21:06.627 UTC; level 30 at 21:21:59.612 UTC (~53 s).
- CPU demand max 88 C; CPU protection max 97 C; GPU max 50 C.
- CPU 97 C opened one confirmation observation; no sustained-load cooling occurred.
- Decision interval 201.177 s, not a complete five-minute observation.
- Normal stop, exit code 0, release requests accepted, no retained lease,
  no unknown native completion and no ACPI events listed in experiment summary.
- `FirmwareRestorationVerified=false`: independent firmware handoff remains unproven.

This establishes correct configuration and observed brief-load actuation, not
production Automatic authorization, acoustic qualification or a 20-minute load test.
The final step to level 30 takes longer than intermediate decrements: confirmation
is not a fixed time per step when filtering/quantization changes the requested level.
Do not promise a four-second physical RPM descent.

## Local verification

The .NET SDK CLI cannot start in this container (Process.GetStat/StartTime failure).
The cached SDK Roslyn compiler was invoked directly with .NET 8 reference assemblies.
The merged core compiles. Local software-only checks passed:

- WMI fan experiment self-test.
- Adaptive policy self-test.
- SafetyGate self-test.
- All 17 `Performance/*SelfTest.cs` suites (0 failures), including CPU recovery,
  GPU journals/session/transition/ownership, source coordination and Guardian authority.

These checks use fixtures and do not access the user's hardware. They do not replace
Windows GUI rendering, PowerShell 5.1 checks or native-clock/lifecycle tests.
The new dashboard test and complete solution build still require Windows CI.

Publication of the integration branch was rejected by automatic approval review:
explicit authorization to publish this new branch/destination is required.
Do not call the merged GUI Windows-validated before the resulting CI completes.

## Next implementation sequence

1. Publish this branch after explicit authorization; run both Windows workflows,
   inspect failures and dashboard-performance-ui.png before further changes.
2. Inspect Guardian host/options and qualified CPU/GPU lifecycle factories. Program.cs
   currently exposes fixture/qualification entry points, not a product GUI launcher.
   Add a bounded GUI qualification entry point, keeping production defaults closed.
3. Implement a semantic named-pipe client: HELLO, ENABLE_SESSION, STATUS,
   DISABLE_SESSION, SHUTDOWN. Preserve target, PID/start-time, nonce, bounded frames,
   request identity, timeouts and serialized commands. No raw hardware IPC.
4. Provide CPU AC/Battery presets from validated persisted preferences through an
   explicit validated session configuration contract. Current ENABLE_SESSION only
   carries CPU/GPU enable flags: do not invent raw register payloads or silently
   assume slider values already reach the Guardian.
5. Keep GPU at qualified AC 210..1850 and Battery 210..1200 MHz for the first binding.
   CPU defaults are AC 35/60 W, Battery 8/15 W. Verify current physical CPU baseline;
   reject preferences outside the downward-only envelope visibly.
6. Add enable/disable actions, independent domain states and release/error details.
   Save/defaults/navigation/startup must not enable hardware. GPU is ActiveUnverified:
   a successful Set is not proof of the installed arbitrary locked range.
7. Close via semantic release; Guardian owns parent loss and display/standby cleanup.
   Do not duplicate RAPL restore or GPU Reset in MainForm. Do not blindly reapply on resume.
8. Software-only client/GUI fixtures: bad identity, malformed/mismatched replies,
   partial domain failure, timeout, save failure, close while starting, lost Guardian.
9. Physical GUI qualification: explicit enable in AC -> Battery -> AC -> Modern Standby
   -> resume -> disable. Inspect independent journals, CPU restore/GPU reset evidence,
   process exit and mutex availability. Then test owner loss separately.
10. Qualify Automatic through its final backend with Firmware return, app exit and
    telemetry loss. Preserve the experiment's unresolved independent Firmware proof.
11. Test performance + Automatic together, then sustained load and cooling. Only
    promote the relevant authorizations after reviewing the corresponding evidence.

## Final visual reference specification

Reference archive: `Imagenes objetivo final VictusFanControl.zip`, nine 1672x941 PNGs.
This is the final appearance target, not a source of literal hardware settings.

Shared frame: dark blue-black background, slightly lighter rounded cards, thin slate
borders, white primary text, muted blue-grey secondary text, vivid blue selection.
Fixed title bar with logo/device identity; left icon/text navigation; optional top
subtabs with blue underline; fixed bottom status bar. CPU chart red, GPU chart blue.
Adapt at 100/125/150 percent DPI and smaller windows; keep apply/save actions reachable.
Use reusable drawn WinForms components before considering a framework migration.

| Image suffix | Screen | Layout |
| --- | --- | --- |
| 1 | Inicio | Four summary cards, four sensor cards, two wide RPM cards |
| 2 | Ventiladores / Control | Four mode/navigation cards; manual controls left, curve right |
| 3 | Ventiladores / Telemetria | Temperature row, load/power row, RPM and energy cards |
| 4 | Rendimiento / CPU RAPL | PL1/PL2 controls left, quick profiles right |
| 5 | Perfiles | Profile list left; metadata and CPU/GPU/fan preferences right |
| 6 | Curvas | Variable selector and numeric point table left; chart right |
| 7 | Reglas y seguridad | Vertical icon/title/explanation/status rows |
| 8 | Monitorizacion | Large history charts left; live value column right |
| 9 | Configuracion | General/application/integration/advanced tabs; settings groups |

Use real levels (10..50 where applicable), not mislabeled percent or nominal RPM.
Show selected CPU demand aggregation and distinguish Package/max protection readings.
Curves edits the Automatic configuration; do not create another hardware mode just
because the mockup has a fourth card. Mockup safety text is inconsistent with current
thermal behavior: show actual rules and retain protected critical limits.
Do not invent PCH protection, arbitrary GPU sliders, blind automatic reacquisition,
firmware restoration proof, or enabled production sessions from mockup illustrations.
Device model/version/power/frequency/status values must come from the real application.
Screens without references should use the same components, with their layout designed
when implemented. The current dashboard is a checkpoint, not the final pictured GUI.

## Explicit provisional GUI binding — 2026-10-04

The previous checkpoint's unbound preferences screen is now connected on
`feature/victus-8c40-automatic-final-qualification`. The existing performance
foundation is merged while retaining the subsequent fan acknowledgement,
telemetry continuity and final A1 evidence fixes. General repository publication
has since been explicitly authorized by the owner.

Rendimiento is provisional and keeps CPU AC/Battery PL1/PL2 sliders. CPU limits
remain within 8..44 W PL1 / 8..114 W PL2, with PL2 >= PL1, and the child independently
requires the qualified unlocked 45/115 W baseline. CPU and GPU are independently
selectable; GPU stays at the qualified desired ranges 210..1850 MHz AC and
210..1200 MHz Battery. No arbitrary GPU slider or GPU power-limit writer is exposed.

Saving/defaults remain preference-only. Explicit Apply launches the detached
PerformanceGuardian with an immutable validated configuration. The current-user
bounded named pipe uses HELLO with owner PID/start-time and nonce, semantic ENABLE,
STATUS and SHUTDOWN, serialized requests, request/target/version reply checks and
bounded deadlines. The child rechecks hardware identity, CPU name, module hash,
existing journals and native capability. Both domains use the existing qualified
journal, conflict, source transition, parent-death and display-aware lifecycle
adapters. Applying and releasing from the GUI require Firmware mode/authority;
values cannot be edited while the Guardian process is retained. Exit restores fan authority first, then requests
Guardian shutdown before disposing telemetry; it never kills the hardware owner.

The UI reports CPU/GPU states and directly queried source independently. GPU
`ActiveUnverified` means successful NVML Set; it does not certify the installed
arbitrary range. Lost/stale status is not shown as active. A failed release remains
visible, retains the process reference, and prevents a new session. Retained journals
block a new child instead of being deleted. A host runtime failure or external
cancellation now also attempts domain release before it exits.

Ajustes includes a reversible current-user Windows Task Scheduler logon registration,
interactive token / highest available, no password, no battery stop, no task timeout,
and `--start-minimized --modules-dir` with the current absolute paths. Loading the
checkbox only queries Task Scheduler. Enabling it launches the GUI at subsequent
logon in Firmware with performance disabled. Use the executable, not a `dotnet DLL`
invocation, when registering startup; moving the executable requires registration
again. XML uses escaping for executable/module paths. Microsoft schema reference:
https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-

Removed the separate preview-only curve editor and its static Equilibrado reference
from Ventilación. Automatic curves and tuning have one editable surface in Ajustes.
The read-only preview and real safety/authority diagnostics remain. The 88F8-only
EC probe button is shown only for an 88F8 board, never for this 8C40 target.
The final pictured UI remains future work.

### A1 with limits

Run `scripts/test-automatic-final-qualification.ps1 -WithPerformanceLimits`.
After READY, first save limits and Apply both domains in Rendimiento, wait for CPU
Active / GPU ActiveUnverified, then select Automatic once. Do not change limits
while Automatic runs. Return with Firmware once the script's normal-path counters
are satisfied. Existing thermal protection thresholds are unchanged.

The new switch requires both domains, binds configuration/status into the event
stream, supervises active status during the gate, checks absence of performance
journals after GUI exit, and packages matching Guardian reports/configurations.
It defines a controlled performance-conditioned fan run; it does not prove fan
behavior with unlimited CPU/GPU power. Both release paths still require evidence.
Software fixtures do not promote physical GUI qualification.


## WMI-only fan route

The HP 8C40 GUI now uses HP WMI for fan setters and RPM, with a separate detached
WMI fan guardian. The performance Guardian CPU/GPU domains and limits are unchanged.
Direct EC is prohibited in the GUI fan route; no M4 EC watchdog bootstrap occurs.
The final test now reports accepted WMI release/default requests and retired fan
lease, explicitly without independent firmware ownership proof. See
`HP_8C40_AUTOMATIC_FINAL_QUALIFICATION.md` for the new evidence scope and retest.
