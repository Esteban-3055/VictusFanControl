# VictusFanControl

Experimental adaptive fan-control project for HP Victus laptops, starting with HP board **88F8**.

> **Current development status: v0.2 read-only telemetry.** There is still no fan-control write path.

## Development target

- HP Victus 16-d0515la family
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- HP motherboard Product ID `88F8`, board version `88.58`
- normal reference configuration: external monitor connected, RTX 3060 intentionally active

No unique serial numbers are stored in this repository.

## Telemetry architecture

LibreHardwareMonitor is being removed as a runtime dependency.

```text
PawnIO + official signed modules
├─ Intel MSR -> CPU package temperature + RAPL package power
└─ ACPI EC  -> CPU/GPU fan RPM

NVIDIA NVML
└─ GPU temperature + GPU power + GPU utilization

Windows API
└─ total CPU utilization
```

VictusFanControl talks to the installed PawnIO driver **directly through DeviceIoControl**. It does not link against `PawnIOLib.dll`.

See [docs/TELEMETRY_BACKENDS.md](docs/TELEMETRY_BACKENDS.md).

## Known 88F8 fan observations

| Requested level | CPU fan | GPU fan | Notes |
|---:|---:|---:|---|
| 14 | ~1,400 RPM | ~1,400 RPM | Stable low-speed point |
| 30 | ~3,000 RPM | ~3,000 RPM | Tracks target closely |
| 50 | ~4,330 RPM | ~4,670 RPM | Physical fan ceilings differ |

The future normal controller will expose one shared physical RPM target for both fans, while retaining independent tachometer feedback and safety overrides.

## Requirements

- Windows 11 x64
- .NET 8 SDK or newer SDK capable of building the net8.0 target
- Administrator terminal
- PawnIO 2.2+ installed
- NVIDIA display driver with NVML

## Development branch setup

Install the pinned official signed PawnIO modules:

```powershell
.\scripts\setup-pawnio-modules.ps1
```

Probe all telemetry backends:

```powershell
.\scripts\probe-backends.ps1
```

The previous command `list-sensors.ps1` remains as a compatibility entry point during the transition.

## Baseline capture

Once the backend probe reports all required backends ready:

```powershell
.\scripts\run-baseline.ps1 -Scenario idle -Minutes 15
```

CSV output includes CPU/GPU temperature, power, utilization and both fan tachometers.

## Safety

This branch remains read-only. ACPI EC register reads use the standard READ transaction, which writes only the read command/register address to the EC command/data ports; no fan set-point register value is written.

Actual fan control remains blocked on the safety-supervisor milestone in [docs/SAFETY.md](docs/SAFETY.md).

## License

MIT. See [LICENSE](LICENSE).

Third-party components and their licenses are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
