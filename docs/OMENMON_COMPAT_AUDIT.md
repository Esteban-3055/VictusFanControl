# OmenMon compatibility audit for HP 88F8 fan control

This audit compares VictusFanControl's independent HP 88F8 fan-control path with the observable behavior and WMI protocol used by upstream OmenMon and OmenMon-Reborn.

The project does not copy OmenMon implementation code. The WMI class names, command identifiers and payload formats documented here are hardware/protocol compatibility facts required to interoperate with the same HP firmware interface.

## WMI protocol match

The following operations now match the HP WMI request envelopes used by OmenMon:

| Operation | Command | CommandType | Payload | Output method |
| --- | ---: | ---: | --- | --- |
| GetFanLevel | 0x00020008 | 0x2D | 00 00 00 00 | hpqBIOSInt128 |
| SetFanLevel | 0x00020008 | 0x2E | CPU GPU 00 00 | hpqBIOSInt0 |
| FanMode=LegacyDefault | 0x00020008 | 0x1A | FF 00 00 00 | hpqBIOSInt0 |

The common transport is `root\wmi`, class `hpqBIntM`, instance `ACPI\PNP0C14\0_0`, input class `hpqBDataIn`, with signature `SECU`.

## Important difference: CLI fan level vs fan program

OmenMon's direct CLI command `-Bios FanLevel=x,y` sends the WMI SetFanLevel operation directly. It does not inherently require an EC manual-mode write first.

OmenMon's long-running fan-program layer performs additional policy work: it can maintain fan mode, GPU power and the firmware countdown depending on configuration. VictusFanControl intentionally does **not** copy that policy because the target configuration keeps OMEN Gaming Hub responsible for CPU undervolt and VictusFanControl should own only fan cooling.

## 88F8 EC observations

The 88F8 uses the legacy 2022-style EC layout already validated on the target:

- 0x2C / 0x2D: requested fan-rate percentage
- 0x2E / 0x2F: fan-rate readback percentage
- 0x34 / 0x35: CPU/GPU fan-level setpoints
- 0x62: manual fan control flag
- 0x63: manual auto-reset countdown
- 0x95: fan/performance mode
- 0xEC: max-fan latch/status
- 0xF4: fan on/off switch
- 0xB0..0xB1: CPU tachometer, 16-bit little-endian RPM
- 0xB2..0xB3: GPU tachometer, 16-bit little-endian RPM

A public OmenMon-Reborn 88F8 auto-calibration report independently confirms the B0/B2 16-bit tachometers. Its 0/30/70/100 values are percentage-rate calibration values and must not be confused with the WMI `FanLevel=30` command that was previously measured around 3000 RPM on this target.

## Restore semantics

OmenMon's fan-program termination path sends a special `FF,FF` fan-level reset before restoring the previous fan mode. The target 88F8 was already shown to return to HP automatic behavior with `FanMode=LegacyDefault` alone.

VictusFanControl therefore keeps `FF,FF` out of the normal validated 14-50 command range for now. It will not add that special reset write until the simpler LegacyDefault path is revalidated in our own WMI implementation.

## BIOS error ambiguity

OmenMon notes that on some HP models a SetFanLevel call may take effect even when BIOS reports an error. This changes our failure handling:

- every attempted SetFanLevel is considered potentially effective;
- firmware restoration is attempted even if the WMI call throws or returns a failure code;
- successful control additionally requires BIOS GetFanLevel readback plus stable tachometer response;
- a non-zero BIOS return code is still treated as a failure for VictusFanControl until this exact 88F8 proves otherwise.

This is intentionally stricter than OmenMon.

## First-write validation improvements

The first hardware-write test now requires:

- exact validated target fingerprint (88F8 / board 88.58 / Victus 16-d0xxx / SKU prefix 62C37LA / expected RTX 3060)
- complete/fresh/plausible telemetry
- light-load baseline
- fixed level 30,30 only
- exact WMI GetFanLevel readback of 30,30
- two consecutive RPM samples with both fans between 2500 and 4000 RPM
- acknowledgement by 8 seconds
- 15-second maximum custom-control duration
- LegacyDefault restore after **any attempted** fan-level write
- expanded EC before/applied/after snapshots including manual, countdown, mode, max-fan and fan-switch state
- OMEN Gaming Hub undervolt verification before and after the test

## Deliberate non-matches

VictusFanControl does not currently:

- write EC 0x62 manual mode;
- write EC fan-rate registers directly;
- set GPU power or a performance profile;
- use Max Fan;
- send the OmenMon `FF,FF` reset sentinel;
- continuously rewrite the fan mode every program tick.

These omissions are intentional for the HP 88F8 path and reduce interaction with OMEN Gaming Hub and CPU undervolt settings.


## EC robustness lessons incorporated

The compatibility review also covered OmenMon-Reborn's later EC reliability fixes. VictusFanControl now independently applies the relevant safety principles:

- avoid prolonged pure-spin polling when the ACPI EC is busy;
- serialize app-level EC access with `Global\Access_EC`;
- reject incoherent 16-bit tachometer reads rather than trusting a torn low/high-byte pair;
- treat EC failures as missing telemetry, never as a plausible stale numeric value.

These changes affect the telemetry implementation, so the earlier 30-minute soak is no longer sufficient evidence for the current HEAD. A new read-only soak is required before the first write test.
