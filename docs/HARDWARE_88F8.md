# HP 88F8 hardware notes

## Development target

- Vendor: HP
- Product ID: `88F8`
- Board version: `88.58`
- Platform: Victus 16-d0xxx family
- CPU used during development: Intel Core i7-11800H
- GPU used during development: NVIDIA GeForce RTX 3060 Laptop GPU
- Fan count reported by BIOS: 2
- Normal development/use configuration: external monitor connected, keeping the RTX 3060 active

No motherboard serial number is stored here.

## Cooling topology / control policy

The tested machine has a thermally coupled CPU/GPU heatsink assembly. For this project the normal control policy will therefore use a **single shared fan-RPM target** derived from the thermal state of both CPU and GPU.

The two physical fans remain independently observable. The backend may compensate each actuator separately to achieve the same measured RPM, because equal command levels do not necessarily produce equal RPM at the top of the range.

The safety layer is allowed to break RPM symmetry when needed for protection.

## Observed fan data

### Level 14

- Requested set point: 1,400 RPM
- CPU fan observed: ~1,392-1,400 RPM
- GPU fan observed: ~1,401-1,417 RPM

### Level 30

- Requested set point: 3,000 RPM
- CPU fan observed: ~2,998-3,003 RPM
- GPU fan observed: ~3,003-3,015 RPM

### Level 50

- Requested set point: 5,000 RPM
- CPU fan observed: ~4,321-4,329 RPM
- GPU fan observed: ~4,657-4,667 RPM
- Firmware-reported fan rate: 100% on both fans

Interpretation: at low and mid range, the same requested level produced nearly identical RPM. Near saturation, the fans diverged substantially because their physical ceilings differ. Therefore a future "same RPM" controller should use tachometer feedback rather than assume equal numerical levels produce equal physical speed.

## External-monitor baseline

The intended daily-use scenario keeps an external monitor connected. In the first 15-minute read-only baseline, the RTX 3060 therefore remained active instead of entering a deep idle state.

This scenario is intentional and should be treated as the reference idle for this project rather than testing without the external monitor.

## EC locations observed during investigation

These addresses are documentation only in v0.1. The application does not access them.

| Register | Observed meaning |
|---|---|
| `0x34` / `0x35` | Fan set-point values |
| `0x2E` / `0x2F` | Reported fan percentage/rate |
| `0xB0` / `0xB2` | 16-bit little-endian fan RPM sources |
| `0x57` | CPU temperature candidate |
| `0xB7` | GPU temperature candidate |
| `0x63` | Manual-control countdown/watchdog |

## Important limitation

The stock HP fan controller appears, based on user observation, to react to workload as well as temperature. This has not yet been fully quantified. The purpose of the baseline logger and upcoming RPM telemetry is to measure that behavior before designing a replacement controller.
