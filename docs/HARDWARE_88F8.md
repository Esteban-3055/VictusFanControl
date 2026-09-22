# HP 88F8 hardware notes

## Development target

- Vendor: HP
- Product ID: `88F8`
- Board version: `88.58`
- Platform: Victus 16-d0xxx family
- CPU used during development: Intel Core i7-11800H
- GPU used during development: NVIDIA GeForce RTX 3060 Laptop GPU
- Fan count reported by BIOS: 2

No motherboard serial number is stored here.

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

Interpretation: level 50 is already at or extremely near the physical fan ceiling on the tested machine. A higher numerical set point should not be assumed to produce more airflow.

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

The stock HP fan controller appears, based on user observation, to react to workload as well as temperature. This has not yet been quantified. The purpose of the v0.1 baseline logger is to collect enough data to verify that behavior before designing a replacement controller.
