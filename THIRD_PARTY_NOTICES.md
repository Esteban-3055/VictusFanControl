# Third-party notices

## PawnIO

VictusFanControl requires the separately installed PawnIO driver for privileged hardware telemetry.

Project: https://github.com/namazso/PawnIO  
License: GPL-2.0-or-later with the project's stated exception for independent modules that communicate with PawnIO solely through the device IO control interface.

VictusFanControl does **not** link against or redistribute `PawnIOLib.dll`. It communicates with the installed PawnIO device through the Windows device IO control interface.

## PawnIO.Modules

The setup script downloads official signed PawnIO modules from:

https://github.com/namazso/PawnIO.Modules

Modules currently used:

- `IntelMSR.bin`
- `LpcACPIEC.bin`

License: LGPL-2.1-or-later.

The module binaries are not committed to this repository.

## NVIDIA NVML

VictusFanControl dynamically loads `nvml.dll` supplied by the installed NVIDIA display driver. The repository does not redistribute NVIDIA's NVML runtime.

Documentation: https://docs.nvidia.com/deploy/nvml-api/

## OmenMon / OmenMon-Reborn

OmenMon and OmenMon-Reborn were used as hardware-behavior research references while documenting HP OMEN/Victus firmware and the 88F8 investigation.

VictusFanControl does not copy OmenMon source code. If future versions adapt GPL-covered source, the distribution and licensing of the combined work must be reviewed before release.
