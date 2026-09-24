# Telemetry backends

## Decision

VictusFanControl does **not** use LibreHardwareMonitor as a runtime dependency from the v0.2 development branch onward.

The selected architecture is:

```text
VictusFanControl
├─ PawnIO device IOCTL interface
│  ├─ signed IntelMSR.bin
│  │  ├─ CPU package temperature
│  │  └─ RAPL package energy -> package power
│  └─ signed LpcACPIEC.bin
│     ├─ HP EC CPU/GPU fan tachometers
│     └─ later: validated EC supervision values
├─ NVIDIA NVML (nvml.dll from NVIDIA driver)
│  ├─ GPU temperature
│  ├─ GPU power
│  └─ GPU utilization
└─ Windows API
   └─ total CPU utilization
```

## PawnIO boundary

VictusFanControl intentionally does not link against `PawnIOLib.dll`.

It opens the PawnIO device and uses the public device IO control protocol directly:

- device type: `41394`
- load signed module: function `0x821`
- execute module function: function `0x841`
- query PawnIO version: function `0x861`

Current development targets PawnIO 2.2 or newer because that release restored the DOS device path used by direct Win32 clients.

## Signed modules

The normal signed PawnIO driver only accepts signed Pawn modules. We use official signed builds from `namazso/PawnIO.Modules`:

- `IntelMSR.bin`
- `LpcACPIEC.bin`

The repository does not build or ship an unrestricted PawnIO driver.

## NVIDIA NVML

NVML is supplied by the NVIDIA display driver. VictusFanControl loads `nvml.dll` dynamically and does not redistribute it.

On Windows, NVIDIA documents NVML under either the DCH driver system directory or the classic `NVIDIA Corporation\NVSMI` driver directory.

## Read-only status

v0.2-dev performs read-only telemetry.

ACPI EC register reads necessarily transmit the standard EC READ command and register address to ports 0x66/0x62. That is protocol traffic required to read a register; VictusFanControl does not issue an EC register-value write in this phase.
