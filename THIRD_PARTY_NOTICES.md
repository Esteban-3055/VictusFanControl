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

## hyperx-cloud-flight-wireless

The coarse Cloud Flight battery voltage-to-percentage map is adapted from
https://github.com/srn/hyperx-cloud-flight-wireless (MIT). Native Windows HID
transport is implemented independently. HeadsetControl is a protocol reference;
its source and polynomial are not incorporated.

MIT License

Copyright (c) 2020 Søren Brokær

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## OmenMon / OmenMon-Reborn

OmenMon and OmenMon-Reborn were used as hardware-behavior research references while documenting HP OMEN/Victus firmware and the 88F8 investigation.

VictusFanControl does not copy OmenMon source code. If future versions adapt GPL-covered source, the distribution and licensing of the combined work must be reviewed before release.
