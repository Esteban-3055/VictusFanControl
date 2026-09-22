# VictusFanControl

Experimental adaptive fan-control project for HP Victus laptops, starting with the HP board **88F8**.

> **Current status: v0.1 telemetry only.** This version does **not** write fan speeds, EC registers, BIOS fan settings, or power limits.

The long-term goal is to build a controller that can make better low-load acoustic decisions than a temperature-only curve by combining:

- CPU and GPU temperatures
- CPU package power and GPU power
- CPU/GPU utilization
- thermal trend (`dT/dt`)
- measured fan RPM
- hysteresis and delayed fan-down logic
- hard safety limits and automatic fallback to HP firmware control

The project is deliberately staged. We first record how the stock HP controller behaves, then add fan control only after the telemetry and safety layer are validated.

## Supported hardware

The first development target is:

- HP Victus 16-d0515la family
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- HP motherboard Product ID `88F8`, board version `88.58`

No unique serial numbers are stored in this repository.

## Known 88F8 fan observations

Measured during development with OmenMon-Reborn diagnostics:

| Requested level | CPU fan | GPU fan | Notes |
|---:|---:|---:|---|
| 14 | ~1,400 RPM | ~1,400 RPM | Stable low-speed point |
| 30 | ~3,000 RPM | ~3,000 RPM | Tracks target closely |
| 50 | ~4,330 RPM | ~4,670 RPM | Both reported 100% fan rate; physical ceiling reached |

These values are **observations for one 88F8 machine**, not universal constants for every HP Victus.

## v0.1 features

- Reads CPU temperature, package power and total load.
- Reads NVIDIA GPU temperature, power and load when exposed by LibreHardwareMonitor.
- Writes telemetry to CSV.
- Can print all detected hardware sensors for debugging.
- Contains no fan-control backend yet.
- Includes an 88F8 hardware profile with the currently verified fan calibration points.

## Requirements

- Windows 11 x64
- .NET 8 SDK
- Administrator terminal recommended for the fullest hardware telemetry access

The telemetry layer uses [`LibreHardwareMonitorLib`](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), currently pinned to stable version `0.9.6`.

## Quick start

```powershell
cd VictusFanControl
.\scripts\bootstrap.ps1
```

List every detected sensor:

```powershell
.\scripts\list-sensors.ps1
```

Record a 15-minute idle baseline:

```powershell
.\scripts\run-baseline.ps1 -Scenario idle -Minutes 15
```

Or run directly:

```powershell
dotnet run --project .\src\VictusFanControl -- --interval-ms 1000 --duration-seconds 900 --output .\logs\baseline-idle.csv
```

Stop an indefinite capture with `Ctrl+C`.

## Development plan

See [docs/ROADMAP.md](docs/ROADMAP.md). The next major milestone is read-only fan RPM telemetry for the 88F8. Actual fan writes are intentionally deferred until the safety supervisor is implemented and tested.

## Safety philosophy

Fan control is hardware control. A software bug must never be able to silently leave the laptop under-cooled.

Future control builds must satisfy the invariants in [docs/SAFETY.md](docs/SAFETY.md), including sensor plausibility checks, RPM feedback, temperature overrides, command range clamping, watchdog/fallback behavior, and a supported-board allowlist.

## License

MIT. See [LICENSE](LICENSE).

## Acknowledgements

- LibreHardwareMonitor for cross-hardware telemetry.
- OmenMon / OmenMon-Reborn as documentation and research references for HP OMEN/Victus firmware behavior. v0.1 does not copy OmenMon source code.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
