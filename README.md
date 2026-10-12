# VictusFanControl v1.2.3

[Español](README.es.md) · [Stable v1.2.3](https://github.com/Esteban-3055/VictusFanControl/releases/tag/v1.2.3) · [Installation and operation](docs/PRODUCT_V1.md) · [Validation status](docs/PRODUCT_VALIDATION_STATUS.md)

Windows fan control and CPU/GPU preferences for the exact target **HP 8C40 / 9D0R1LA / BIOS F.18**: HP Victus 15-fa1xxx, board revision 63.43, Intel Core i7-13700H and NVIDIA RTX 4060 Laptop GPU. v1.0.0 enables habitual Automatic on this target. Other models and BIOS versions are outside this release's control authorization; HP 88F8 material remains historical.

## Install

Download **VictusFanControl-1.2.3-Setup-win-x64.exe** from the stable release and run it. The ZIP and PowerShell alternative is described below.

Requires Windows 11 x64, .NET Desktop Runtime 8 x64, the PawnIO driver and NVIDIA/NVML. The ZIP includes the application's PawnIO modules; it does not install those prerequisites.

1. Download **VictusFanControl-1.2.3-win-x64.zip** from Releases and extract all files. Exit older Victus applications normally from their tray menus.
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

Automatic begins in Firmware. Admission requires complete fresh telemetry, a stable source, CPU ≤90 °C and GPU ≤82 °C. During operation, CPU ≥95 °C has at most 2000 ms confirmation; CPU ≥99 °C triggers immediate handoff. GPU >82 °C, CPU >60 W, stale data or lifecycle faults interrupt control. The watchdog and bounded AC/Battery handoff remain active.

The product uses bounded HP WMI fan commands and watchdog, PawnIO Intel/ACPI readings, NVIDIA NVML and Performance Guardian. WMI acceptance **does not prove independent firmware ownership or exact setpoint readback**. NVML acceptance **does not prove the exact locked GPU clock range**.

Windows/Linux software checks, packaged fixtures and offline replay are verified. Earlier physical evidence remains scoped to the measured builds/configurations. Lower simulated demand **does not establish lower dBA or equivalent cooling**. Representative use, this build's suspend/exit/logon behavior and comparable thermal/acoustic measurements remain listed in `PRODUCT-RELEASE.json`. See [current evidence and limitations](docs/PRODUCT_VALIDATION_STATUS.md).

## Develop and publish

Use .NET 8 SDK and the explicit product release property:

```powershell
dotnet publish src/VictusFanControl.App -c Release -r win-x64 -p:VictusProductRelease=true
```

The pipeline packages current product binaries as **1.2.3.0**. A successful `main` or rebuilt product-branch build can publish v1.2.3 after same-commit OEM, WMI and CPU/GPU checks pass. Publication verifies each file against the manifest and attaches SHA-256 and build provenance. Existing releases are never overwritten.

Historical qualification builds retain their RC version and gates, including `control.enabledByDefault=false` and `automaticPolicyEnabled=false`. These describe the legacy harness; the product's separate authorization covers only the exact target above. Earlier plans remain development history.

MIT: [LICENSE](LICENSE). External components: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Instalador y actualizaciones v1.1.0

The stable v1.2.3 release separates **Download** from **Install** and keeps a verified installer across application restarts and failed installation attempts. Pending sessions can be recovered from the Updates page before retrying Install. The installer preserves existing startup preferences by default. Stable is the default update channel; save **Include prereleases** to receive previews. [Guide](docs/PRODUCT_V1.md) · [Functional review](docs/PRODUCT_1_1_5_REVIEW.md).

Version 1.1.6 removes the GPU consumption handoff and identifies concrete Automatic wait/admission failures. GPU power still contributes to fan demand; selected CPU power and thermal protections remain. [Changes and tests](docs/PRODUCT_1_1_6_CHANGES.md).

Version 1.1.7 adapts the window to resolution and DPI changes, including 4K → 1440p → 4K, and restores the preferred size. Diagnostics include display geometry. [Implementation and verification](docs/PRODUCT_1_1_7_DISPLAY.md).

Version 1.1.8 filters brief CPU temperature rises before fan demand: a rise must persist for two seconds; ≥99 °C bypasses the filter. Raw sensors and protection handoffs remain independent. GPU temperature, power and load contributions keep their behavior. [Scope and checks](docs/PRODUCT_1_1_8_CPU_SPIKES.md).

Version 1.1.9 can recover pending sessions automatically in the background, including startup and minimized operation. Settings can disable it. Resumption preserves the applied profile and requires fresh readings; a user Firmware request cancels it. CPU and TZ01/DTT3 contribution diagnostics are added without changing thermal behavior. [Details](docs/AUTOMATIC_SESSION_RECOVERY.md).

Version 1.2.0 refreshes Home with two animated fans, temperature/load readings, the applied curve and real-time controller demand. Fans uses the same curve plot size as the editor. Navigation follows the requested order; animation pauses outside Home, when hidden/minimized or when feedback expires. [Interface and checks](docs/HOME_DASHBOARD.md).

Since v1.2.1, Home fans rotate ten times more slowly, with independent acceleration/deceleration inertia and smooth starts. The displayed RPM remains the current reading. Zero RPM gently settles to rest; stale feedback or hidden/minimized windows pause immediately.

Since v1.2.2, both Home animations follow accepted controller demand rather than RPM. Delayed or temporarily unavailable RPM no longer restarts visual inertia; the hub numbers still show reported RPM. Firmware/Manual or expired demand pauses animation.

Since v1.2.3, Home fans rotate three times faster while preserving demand-driven motion and inertia: one visual revolution every 5 seconds at 100 % demand and every 10 seconds at 50 %, once settled. Reported RPM and physical fan control remain unchanged.
