# VictusFanControl

Experimental adaptive fan-control project for HP Victus laptops, starting with the validated HP **88F8** development target.

> **Current development status: v0.3 pre-control validation.** The normal GUI/controller path is still read-only and HP firmware remains authoritative. The repository now contains explicit, separately invoked BIOS/WMI validation commands for restoring HP Auto and for a tightly bounded first fan-write test.

## Development target

The write-validation allowlist is intentionally narrower than Product ID alone:

- HP Victus 16-d0515la family
- system product: `Victus by HP Laptop 16-d0xxx`
- SKU prefix: `62C37LA`
- HP motherboard Product ID `88F8`, board version `88.58`
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- normal reference configuration: external monitor connected, RTX 3060 intentionally active

No unique serial numbers are stored in this repository.

## Telemetry architecture

```text
PawnIO + official signed modules
├─ Intel MSR -> CPU package temperature + RAPL package power
└─ ACPI EC  -> CPU/GPU fan RPM and read-only 88F8 diagnostics

NVIDIA NVML
└─ GPU temperature + GPU power + GPU utilization

Windows API
└─ total CPU utilization
```

VictusFanControl talks to the installed PawnIO driver directly through `DeviceIoControl`. It does not link against `PawnIOLib.dll`.

The EC reader uses bounded retry/backoff, the shared `Global\Access_EC` mutex and coherent repeated reads for the two-byte tachometers.

## Known target fan observations

| Requested WMI fan level | CPU fan | GPU fan | Notes |
|---:|---:|---:|---|
| 14 | ~1,400 RPM | ~1,400 RPM | Stable low-speed point |
| 30 | ~3,000 RPM | ~3,000 RPM | Tracks target closely |
| 50 | ~4,330 RPM | ~4,670 RPM | Physical fan ceilings differ |

These values are specific to the validated machine. An `88F8` Product ID by itself is not treated as sufficient authorization for writes.

The future normal controller will expose one shared physical RPM target for both fans, while retaining independent tachometer feedback and safety overrides.

## Requirements

- Windows 11 x64
- .NET 8 SDK or newer SDK capable of building the net8.0 target
- Administrator terminal
- PawnIO 2.2+ installed
- NVIDIA display driver with NVML

## Development setup

Install the pinned official signed PawnIO modules:

```powershell
.\scripts\setup-pawnio-modules.ps1
```

Probe the telemetry backends:

```powershell
.\scripts\probe-backends.ps1
```

Run the tray GUI:

```powershell
.\scripts\run-gui.ps1
```

The GUI remains read-only. Closing or minimizing it hides it to the tray; choose **Exit** to stop it.

## Validation before fan control

The first write-capable commands are intentionally not integrated into the GUI/controller. They exist only as explicit validation tools.

Before the bounded `30,30` test is considered eligible, the latest telemetry/EC implementation must pass a new soak and suspend/resume regression on the target hardware.

See:

- `docs/PRE_CONTROL_CHECKLIST.md`
- `docs/TELEMETRY_ROBUSTNESS.md`
- `docs/POWER_LIFECYCLE_GUI.md`
- `docs/OMENMON_COMPAT_AUDIT.md`
- `docs/FIRST_FAN_WRITE_TEST.md`

## License

MIT. See [LICENSE](LICENSE).

Third-party components and their licenses are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
