# OEM shadow reconstruction — 2026-10-07

This is a **new implementation**, based on `0190e4df1f39149210c7e8b010b463d56ba91277`.
The unpublished observer commits described in the earlier conversation were absent
from the restored workspace. They have not been recovered or claimed as verified.
The live base branch was rechecked against GitHub: same HEAD, with successful
`build`, `cpu-rapl`, and `wmi-fan-experiment` workflows. Those results are for the
base, not this new observer. New-branch Windows CI remains pending publication.

## Boundary and hypothesis

The pure `tools/OemShadow` project has no hardware dependencies. It never uses
observed fan levels or power/load to predict demand. The existing production
six-demand maximum policy, GUI, fan ownership and PerformanceGuardian logic are
unchanged. The sole core project change exposes existing internal read backends
to a separate capture assembly.

Seed ranges are nominal levels, **not measured tachometer RPM**:

| State | CPU | GPU |
|---|---|---|
| A | 21–22 | 19–20 |
| B | 25–26 | 23–24 |
| C | 33–34 | 28–29 |
| D | 39–40 | 35–36 |

Actual classification allows ±1 in each range, then needs three seconds of
distinct fan acquisitions. Cached reads cannot confirm a transition. Actual
transition times use the first acquisition that was subsequently confirmed,
not the time at which debounce finished. Unknown breaks transition continuity.

All thresholds and durations are visible in `Parameters` and in each summary.
CPU B requires max(package, hottest core) ≥95°C and TZ01 ≥85°C continuously
for 45 seconds. GPU B/C/D candidates require respectively GPU ≥45/55/75°C
and DTT3 ≥50/55/67°C for 60 seconds; changing candidates restarts that timer.
The maximum ready CPU/GPU demand wins. GPU power/load are diagnostic context.

Cold bootstrap requires CPU ≤75, TZ01 ≤60, GPU ≤45, DTT3 ≤49°C for ten seconds.
Warm starts remain Unknown until sufficient sustained evidence. A committed
change is reported as Transition for ten seconds. Descent is one plateau at a
time, after at least 60 seconds of state residence and 30 seconds of all-source
cold evidence. Descent CPU/TZ01/GPU/DTT3 maxima are 55/45/40/42 (B→A),
65/55/48/48 (C→B), 70/65/55/52 (D→C). These are **unvalidated hypotheses**.

Any missing, nonfinite, implausible, future or ≥3000 ms old required source
returns Unknown and no level ranges, clearing hot/cold/ramp timers. Missing
time cannot earn persistence. Short source outages retain only plateau history
for up to 30 seconds. Frame gaps >5 seconds or nonmonotonic time discard history.
Confidence is a categorical diagnostic label, not a calibrated probability.

## Evidence and replay

Fixtures are derived from the raw reference and thermal CSV in the supplied
`VictusFanControl_ThermalResearchBundle_2026-10-07.zip`. The join is causal:
only the latest thermal row at or before the reference timestamp is eligible.
Every reference row is retained. UTC comparisons preserve 100 ns precision.
Each fixture manifest records raw, compressed and uncompressed SHA-256 hashes.
Historical whole-row timestamps approximate acquisition; true per-query
epochs are unavailable. Fan epochs are the authoritative `fan_sampled_at_utc`.
Delayed phase events and premerged telemetry are not used as transition truth.

```powershell
dotnet run -c Release --project tools/OemShadow -- --self-test
dotnet run -c Release --project tools/OemShadow -- --replay tools/OemShadow/fixtures/v10.jsonl.gz artifacts/oem-v10
dotnet run -c Release --project tools/OemShadow -- --replay tools/OemShadow/fixtures/v11.jsonl.gz artifacts/oem-v11
python scripts/check-oem-shadow-replay.py tools/OemShadow/fixtures artifacts/oem-v10 artifacts/oem-v11
```

Use new output directories. CSV/JSONL files rotate at approximately 16 MiB;
model/metrics survive rotation. Summary writes are atomic and refreshed each
minute. Transition event history is capped at 5000 per series; truncation is
explicitly reported. Frame and unique-acquisition confusion matrices are both
reported. Per-state recall includes Unknown/Transition predictions as misses.
Absent support is null. Unknown duration excludes unobserved frame gaps.
Transition pairing is one-to-one, same from/to pair, within ±120 seconds;
this broad window is not a control qualification tolerance.

## Current results: NO-GO

| Replay | Frames | Known-state accuracy | A recall | B recall | C recall | D recall |
|---|---:|---:|---:|---:|---:|---:|
| v10 | 6049 | 24.24% | 55.52% | 0% | 31.44% | 0% |
| v11 | 1976 | 86.77% | 85.17% | 88.85% | No support | No support |

v11 A→B: actual `14:14:21.2519409Z`, predicted `14:14:58.5394552Z`,
37.2875143 seconds late. B→A: actual `14:18:42.2395310Z`, predicted
`14:19:13.9115781Z`, 31.6720471 seconds late. Both match the broad window,
but two transitions from one session are insufficient for qualification.

v10 has five fully observed transitions, all missed. Source expiry contributes
1602.56 seconds of Unknown over 2491.98 observed seconds; 1353 frames lack fresh
TZ01/DTT3. v11 has 20.60 seconds Unknown over 578.00 observed seconds; ten frames
lack fresh TZ01/DTT3. The incomplete sources explain part of the failure, not
the whole mismatch. B/D prediction failure and slow transitions require new
evidence and calibration. Do not tune on v11 alone and report it as independent
validation. No final control parameters are recommended from these seeds.

Full matrices, precision, support and transition misses are in the checked
summaries under `docs/oem-shadow`. Every summary has `goForFanControl=false`.

## Live capture on the exact target

Target: HP 8C40 rev 63.43, SKU9D0R1LA, F.18, i7-13700H, RTX4060 Laptop.
Live capture is still **PENDING**. It is not executable on this Linux host.

Establish firmware/OEM fan mode, close any other fan controllers, and close all
VFC GUI/Guardian/watchdog/readers normally before capture. Absence of a process
alone does not prove that a prior fan override was released. This also avoids mistaking a
Guardian fan-host process for a Performance-only process. The capture never
changes CPU/GPU performance limits; it is independent of journal recovery.
Do not delete retained CPU/GPU journals. A valid recovery report from the exact
session is required to verify release; the provided Phase=1 files are evidence
of prior ownership, not current hardware state.

Build on Windows with .NET 8 SDK, or use the forthcoming Windows CI artifact:

```powershell
dotnet publish tools/OemShadowCapture/OemShadowCapture.csproj -c Release -r win-x64 --self-contained true -o artifacts/oem-shadow-win
.\artifacts\oem-shadow-win\VictusFanControl.OemShadowCapture.exe --self-test
.\artifacts\oem-shadow-win\VictusFanControl.OemShadowCapture.exe --live-read-only --modules-dir 'C:\ruta\paquete-verificado\Modules' --output 'C:\ruta\nueva-captura' --duration-seconds 3600
```

An exported framework-dependent runtime requires .NET 8 Runtime; its launcher
verifies the file hashes before executing. It is a locally compiled review
build, not a Windows SDK/self-contained build. In elevated PowerShell:

```powershell
.\Start-OemShadow.ps1 -Mode Verify -RuntimeDirectory .\runtime
.\Start-OemShadow.ps1 -Mode SelfTest -RuntimeDirectory .\runtime
.\Start-OemShadow.ps1 -Mode Capture -RuntimeDirectory .\runtime -ModulesDirectory 'C:\ruta\paquete-verificado\Modules' -DurationSeconds 3600
```

Use the existing verified `IntelMSR.bin` (hash
`d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f`).
Duration zero runs until Ctrl+C. Capture creates a new evidence directory;
retain `identity.json`, `terminal.json`, `sources.json`, segments and summary.
Send that folder zipped after mixed normal CPU/GPU use and cooling periods.

Only allowlisted WMI **properties** are read: active `ACPI\ThermalZone\TZ01_0`
and A71D/subsystem8C40103C DTT participants with suffixes 1/2/3. Ambiguous
matches become unavailable. No `EsifExecutePrimitive` or EC accesses occur.
The process installs the existing WMI-only policy before opening backends,
which rejects all HP methods except the qualified 20008h/2Dh envelope.
CPU uses existing IntelMSR temperature/power reads, GPU uses NVML samples.
There are no CPU power, GPU clock or fan control writes in the observer.

Each source group admits at most one pending native read. An ignored provider
timeout does not spawn replacement threads; old cached data expire by epoch.
Clock/sleep discontinuities invalidate pending generations, cached sources,
fan reader and model state. Mutex ownership stays on the acquiring thread.
Backend handles are never disposed during a pending native read. The observer
never acquires fan authority, so its failure needs no software restore write;
it also cannot release any override that predated capture.

## Verification limits

Local Roslyn compilation with warnings as errors passed for pure observer,
core and capture. Pure tests: 382 checks. Capture fixtures verify exact units,
instance filtering, blocked-provider admission, generation invalidation and
rejection of direct EC, 0x2E and 0x1A at the installed isolation boundary.
Replay checker independently reconciles inputs, freshness, hashes, matrices,
Unknown durations, unique acquisitions and authoritative v11 transition epochs.

The local `dotnet build` CLI fails before compilation because this environment
cannot read `Process.StartTime`. This is not a successful SDK build. The new
workflow is prepared for Ubuntu/Windows build, tests, replay and Windows publish,
but has not run. Physical target and standard Windows SDK verification remain
pending. Neither pending item is presented as passed.
