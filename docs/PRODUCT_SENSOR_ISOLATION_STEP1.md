# Sensor isolation — step 1

This change is based on the installed v1.2.8 source commit. It removes TZ01/DTT3 acquisition and experimental platform retention from the product. It does not establish the cause of a Windows power-source event.

## Behavior

- Product startup no longer creates the auxiliary reader loop or attaches an auxiliary fan policy. Automatic activation and processing do not wait for these sources.
- Existing `experimentalPlatformRetention: true` files remain readable. Import, copy and save normalize the retired preference to false while preserving the other settings.
- The former Advanced retention page now explains the active sensors and has no enable control. Commands from older sessions cannot enable the retired feature or replace a saved curve.
- Both the application and packaged launcher reject the former live platform experiment before hardware acquisition. Historical models and offline replay evidence remain available for research.
- Diagnostics identify the configured sensor routes and report no active platform retention.

## Remaining sensor routes

CPU temperature/core temperature and package energy use Intel MSR/PawnIO/RAPL. GPU temperature/power/utilization use NVIDIA NVML. CPU utilization uses GetSystemTimes; source detection uses GetSystemPowerStatus. These routes were already present and were preserved.

HP fan acquisition/control still uses the qualified HP WMI interface. The product installs the existing no-direct-EC boundary before constructing telemetry. No EC replacement or fallback was introduced.

The CPU/GPU curves, smoothing, source profiles, freshness limits, thermal handoff rules and fan command bounds are preserved. No fan polling interval was changed in this step.

## Validation and comparison

Build the app and run `--product-sensor-policy-self-test`, `--telemetry-coordination-self-test` and the offline replay tests. The sensor-policy fixtures import an old enabled flag, preserve other settings, reject retired live entries before runtime construction and exercise the real controller with fake fan IO and no auxiliary samples.

For the eventual hardware comparison, close the previous application normally, record the candidate identity and use the same profiles and workload. Record Kernel-Power events, HP WMI acquisition durations and Automatic interruptions. Do not deliberately disconnect power or run the old platform collectors. A single successful session cannot establish causality or prove the issue resolved.

## Following steps

Isolating slow HP WMI operations from independent telemetry and correcting delayed AC/Battery reconciliation are separate changes. They have not been implemented in this step so their effects can be compared separately.

This local candidate has no current-target physical qualification. Earlier platform-retention documents describe historical behavior and are superseded by this document for the product. Installation results are recorded separately from these software checks.

## Local validation results

| Check | Result |
| --- | --- |
| Product Release build | Passed; zero warnings/errors |
| Sensor policy and real controller with fake fan IO | Passed |
| Sensor UI at minimum and large sizes; Automatic activation fixtures | Passed; renders inspected |
| Telemetry coordination/lifecycle fixtures | Passed |
| Offline platform, retention and experiment models | Passed; 20,344 checks, no hardware IO |
| Automatic qualification/package invariants | Passed |
| Complete product GUI suite | Passed on local v1.2.9, including update retry, protections, recovery, startup and lifecycle |
| Same update test against the installed original v1.2.8 assembly | Initially failed; retry fixture was reading real Windows recovery records. Isolated runtimes now default to an empty fixture inventory, while recovery tests inject their own records. Production recovery admission is unchanged. |

The complete GUI run also exposed a blocking wait in the existing invalid-record recovery fixture. Its wait now pumps the UI message loop; the recovery tests then pass. This adjusts only the fixture, not production recovery behavior.

The protection-page minimum-size fixture now supplies explicit display geometry and opens before sizing, so the host's 200-percent DPI does not invalidate its 1040-by-660 render assertion.

The historical quiet-curve evidence retains its original source hashes. Original v1.2.8 source snapshots are stored as text fixtures instead of rebinding old results to the modified code.
