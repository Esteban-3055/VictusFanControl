# Telemetry backends

## Decision

VictusFanControl does **not** use LibreHardwareMonitor as a runtime dependency from the v0.2 development branch onward.

The selected architecture is:

```text
VictusFanControl
├─ PawnIO device IOCTL interface
│  ├─ signed IntelMSR.bin
│  │  ├─ CPU package temperature
│  │  ├─ per-physical-core temperatures (IA32_THERM_STATUS 0x19C)
│  │  ├─ P/E core type + physical-core topology from CPUID
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


## Exact-target telemetry profiles

Telemetry no longer assumes the original HP 88F8 target. The current hardware
fingerprint is resolved through an exact allow-listed target profile before fan
EC locations or strict NVIDIA identity are accepted.

Current profiles:

- HP 88F8 / 62C37LA / BIOS F.32 / RTX 3060 Laptop / 8 physical CPU cores.
- HP 8C40 / 9D0R1LA / BIOS F.18 / RTX 4060 Laptop / 14 physical CPU cores.

An unknown target does not inherit a nearby Victus profile.

## Per-core Intel temperature telemetry

VictusFanControl now reads Intel `IA32_THERM_STATUS (0x19C)` once per physical
core. The sampling thread is temporarily pinned to one representative logical
processor of each physical core before the MSR read, then its previous affinity
is restored.

For hybrid Intel CPUs, CPUID leaf `0x1A` is used to label Performance and
Efficiency cores when supported. CPUID topology leaf `0x1F` (or `0x0B`
fallback) is used to collapse SMT siblings into one physical core.

The exact target profile supplies the expected physical core count. A topology
that does not produce all expected cores is incomplete telemetry and therefore
fails closed for Custom control.

Telemetry exposes:

- package temperature;
- every physical-core temperature;
- hottest physical core;
- average physical-core temperature;
- effective CPU control temperature = max(package, hottest core).

SafetyGate uses the effective CPU control temperature, so a single hot core can
trigger the same conservative CPU thermal handoff even when package
temperature is lower.

This sensor path is read-only. No MSR writes were added.
