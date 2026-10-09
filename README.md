# VictusFanControl v1.1.0 (desarrollo)

[Español](README.es.md) · [Download v1.0.0](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.0.0) · [Installation and operation](docs/PRODUCT_V1.md) · [Validation status](docs/PRODUCT_VALIDATION_STATUS.md)

Windows fan control and CPU/GPU preferences for the exact target **HP 8C40 / 9D0R1LA / BIOS F.18**: HP Victus 15-fa1xxx, board revision 63.43, Intel Core i7-13700H and NVIDIA RTX 4060 Laptop GPU. v1.0.0 enables habitual Automatic on this target. Other models and BIOS versions are outside this release's control authorization; HP 88F8 material remains historical.

## Install

Requires Windows 11 x64, .NET Desktop Runtime 8 x64, the PawnIO driver and NVIDIA/NVML. The ZIP includes the application's PawnIO modules; it does not install those prerequisites.

1. Download **VictusFanControl-1.0.0-win-x64.zip** from Releases and extract all files. Exit older Victus applications normally from their tray menus.
2. Open PowerShell **as administrator using the same Windows account**, in the extracted folder.
3. Run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\Start-ProductGui.ps1 -Mode Install
```

`Install` verifies hashes, copies the release under `%LOCALAPPDATA%\VictusFanControl\releases`, registers the current user's elevated logon task, saves **start minimized + activate Automatic on startup**, and opens the app. Custom curves and CPU/GPU limits are preserved. Pending recovery records block activation and are retained.

Use `-Mode Verify` or `-Mode FinalCheck` to check the package without hardware commands. `-Mode Open` opens without registering a task; a previously saved Automatic-on-start preference still applies. Settings allows disabling Windows startup and saving the minimized/Automatic preferences. Closing the window minimizes it; **Exit** in the tray releases control.

## What v1.0.0 includes

- Seven-page GUI, AC/Battery profiles, one unified fan curve, six adjustable temperature/power/load influences, simulator, live tuning and session diagnostics.
- New AC default: levels **12, 12, 18, 28, 34, 44, 50** at demand **0, 40, 50, 65, 76, 90, 100**. Battery retains its defaults. Only untouched old AC fan defaults migrate; saving backs up the previous preferences.
- Habitual Automatic without the review modes' 5/45-minute expiry, equal CPU/GPU fan commands within 10–50, and both performance limits prepared through Guardian before fans activate.
- Optional Windows startup and one guarded activation attempt after three distinct fresh observations. Suspension, display-off and interruptions require manual reactivation; session restart opens in Firmware.
- Optional experimental TZ01/DTT3 retention, disabled by default: current development supports AC and Battery, up to two extra raw levels for 60 seconds, bounded by the last acknowledged level. Source changes preserve the episode; sensor faults still request Firmware. It can prolong noise; Battery behavior needs a target observation. The published v1.0.0 package retains its original AC-only behavior.
- CPU/GPU recovery preflight that retains journals and provides explicit recovery with exact session IDs.

## Protection and evidence

Automatic begins in Firmware. Admission requires complete fresh telemetry, a stable source, CPU ≤90 °C and GPU ≤82 °C. During operation, CPU ≥95 °C has at most 2000 ms confirmation; CPU ≥99 °C triggers immediate handoff. GPU >82 °C, CPU >60 W, GPU >75 W, stale data or lifecycle faults interrupt control. The watchdog and bounded AC/Battery handoff remain active.

The product uses bounded HP WMI fan commands and watchdog, PawnIO Intel/ACPI readings, NVIDIA NVML and Performance Guardian. WMI acceptance **does not prove independent firmware ownership or exact setpoint readback**. NVML acceptance **does not prove the exact locked GPU clock range**.

Windows/Linux software checks, packaged fixtures and offline replay are verified. Earlier physical evidence remains scoped to the measured builds/configurations. Lower simulated demand **does not establish lower dBA or equivalent cooling**. Representative use, this build's suspend/exit/logon behavior and comparable thermal/acoustic measurements remain listed in `PRODUCT-RELEASE.json`. See [current evidence and limitations](docs/PRODUCT_VALIDATION_STATUS.md).

## Develop and publish

Use .NET 8 SDK and the explicit product release property:

```powershell
dotnet publish src/VictusFanControl.App -c Release -r win-x64 -p:VictusProductRelease=true
```

The pipeline packages current product binaries as **1.1.0.0**. A successful `main` build can publish v1.1.0 after same-commit OEM, WMI and CPU/GPU checks pass. Publication verifies each file against the manifest and attaches SHA-256 and build provenance. Existing releases are never overwritten.

Historical qualification builds retain their RC version and gates, including `control.enabledByDefault=false` and `automaticPolicyEnabled=false`. These describe the legacy harness; the product's separate authorization covers only the exact target above. Earlier plans remain development history.

MIT: [LICENSE](LICENSE). External components: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Instalador y actualizaciones v1.1.0

La rama de desarrollo añade `VictusFanControl-1.1.0-Setup-win-x64.exe` y **Actualizaciones → Buscar actualizaciones**. El EXE conserva preferencias y añade el acceso en el menú Inicio; la actualización verifica el digest de la release estable y libera la sesión antes de instalar. Los artefactos de Actions permiten revisar esta entrega; v1.0.0 sigue siendo la release pública hasta publicar v1.1.0. [Guía](docs/PRODUCT_V1.md).

Automático puede preparar un reintento manual en la misma GUI después de una interrupción, con liberación completa, sensores frescos y límites CPU/GPU confirmados. No rearma por sí solo y no elimina recuperación pendiente.
