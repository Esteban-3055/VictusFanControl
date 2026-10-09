# WMI broker / 5-sample work — frozen baseline

Status: **Step 1 baseline checkpoint**

This document freezes the exact software baseline used for the HP 8C40 WMI fan-telemetry reliability work. It intentionally changes no runtime behavior.

## Source identity

- Repository: `Esteban-3055/VictusFanControl`
- Source branch: `feature/victus-8c40-p16-normal-manual`
- Frozen source HEAD: `05fa77c1fee05650246d68bd529bfb7149b36bad`
- Frozen source tree: `d1bf802a8f1ad0bf52ba10a4ecf7b4a46b9c9f52`
- Source CI: build #1223, run `37103328101`, **SUCCESS**
- Work branch: `feature/victus-8c40-wmi-broker-5sample`

The work branch was created directly from the frozen source HEAD above.

## Critical source blob identities

| Path | Frozen blob SHA |
| --- | --- |
| `src/VictusFanControl/Telemetry/HpWmiFanTelemetryReader.cs` | `8f74ff9b69ae33445296448b5e18586f0d822de9` |
| `src/VictusFanControl/Telemetry/HpWmiFanProofReader.cs` | `b10a3410dea915ac6c0735c6c57c0b94ae0621ac` |
| `src/VictusFanControl/Telemetry/HpWmiFanSamplePublication.cs` | `8be4d49b09dd96018a540d328ff1a3b43aaa6fea` |
| `src/VictusFanControl/Telemetry/HardwareTelemetryReader.cs` | `18ad700b65740d6d205d0aa811673bed6ec4fa4e` |
| `src/VictusFanControl/Telemetry/TelemetrySnapshot.cs` | `683b8e252ef38addcb77d81727d26815130a275c` |
| `src/VictusFanControl/Hardware/Hp/Hp8C40FanControlBackend.cs` | `283995f4785773a1aeb46a0623e63fac7d678f56` |
| `src/VictusFanControl/Control/FanControlCoordinator.cs` | `2638e40b9cd2ccc505beed9f8af229a391d6614b` |
| `src/VictusFanControl/Safety/SafetyGate.cs` | `7c412832fcab83924a68c33b07c056abb2855fd6` |
| `src/VictusFanControl.App/TelemetryWorker.cs` | `0b9f594291e320bb954794fc0f267a0684d2b90b` |
| `.github/workflows/build.yml` | `90fc3d75837775734c5e0435e3eb533e54bc6197` |

## Frozen behavioral invariants

The following behavior is the safety/reference baseline. Later WMI-broker work must not silently weaken it.

### Periodic HP 8C40 fan telemetry

- Exact HP 8C40/F.18 target only.
- Read-only HP WMI `20008h/2Dh`.
- No periodic automatic fallback to direct EC tachometer reads.
- `ReadCached()` stays nonblocking for the normal telemetry path.
- Poll interval: **1000 ms**.
- Fan sample freshness limit: **3000 ms**.
- Periodic logical query timeout: **5000 ms**.
- A native synchronous WMI call is never treated as physically cancelled merely because a logical timeout elapsed.
- Native WMI reads remain single-flight; no overlapping provider calls are allowed.
- Pause/dispose/epoch fences reject pre-lifecycle-boundary results.
- Freshness is measured from the original acquisition start; publishing or filtering must never renew sample age.

### Control / command proof

- A command proof requires a **new real WMI acquisition**; cached periodic data cannot satisfy post-command acknowledgement.
- `RequiredTachConfirmationSamples = 2` remains the command-response criterion.
- The two accepted samples must be distinct/fresh and satisfy existing post-command ordering and directional/continuity checks.
- Tachometer acknowledgement timeout remains **8 s active time**.
- Tachometer acknowledgement poll interval remains **250 ms**.
- Existing EC setpoint acknowledgement, ownership checks and control guards remain independent from RPM filtering.

### Safety and restore

- The HP WMI fan freshness boundary remains **3000 ms** at `TelemetrySnapshot` / `SafetyGate` decision time.
- Five historical samples must never make stale fan telemetry appear fresh.
- A real loss of fresh fan observability must remain capable of causing a fail-closed handoff to HP firmware.
- Watchdog ownership, lifecycle fences, thermal thresholds, EC ownership validation, FF/FF release and LegacyDefault restore semantics are outside the 5-sample filter and must not be weakened.
- Automatic/adaptive execution is not enabled by this work.

## Problem being addressed

A real Manual session has shown simultaneous loss of `cpu_fan` and `gpu_fan`, followed by a WMI proof/status timeout and a correct fail-closed firmware handoff. Earlier P16 evidence also showed a periodic/control-reader coordination gap that was partially corrected by publishing successful control reads to the periodic reader.

The new work targets **acquisition coordination and telemetry robustness**, not safety relaxation.

## Planned architecture

The intended end state is:

1. one HP WMI sample broker owning the native `20008h/2Dh` acquisition lane;
2. control requests prioritized over opportunistic periodic polling without native overlap;
3. a rolling history of at most five valid real samples;
4. stable/filtered RPM derived from that history for presentation and future policy inputs;
5. raw latest acquisition and its original timestamp preserved separately;
6. command proof kept at two fresh post-command acquisitions;
7. 3000-ms safety freshness retained.

## Step 1 acceptance criteria

Step 1 is complete only when:

- the dedicated work branch exists from the exact frozen HEAD;
- this checkpoint is committed on that branch;
- no runtime source file is changed by the checkpoint;
- the branch diff from the frozen source HEAD contains only this baseline document;
- the checkpoint commit is submitted to the normal GitHub Actions workflow.

No hardware test is authorized or required by Step 1.
