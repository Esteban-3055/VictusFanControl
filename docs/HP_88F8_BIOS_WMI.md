# HP 88F8 BIOS/WMI fan control contract

This document records the minimal HP WMI protocol used independently by VictusFanControl. OmenMon is not required at runtime.

## Common WMI transport

- namespace: `root\wmi`
- method class: `hpqBIntM`
- instance: `ACPI\PNP0C14\0_0`
- input class: `hpqBDataIn`
- signature: ASCII `SECU`
- command family: `0x00020008`

## Operations

| Operation | CommandType | Payload | Output |
| --- | ---: | --- | --- |
| Get current fan level | `0x2D` | `00 00 00 00` | `hpqBIOSInt128` |
| Set fixed fan level | `0x2E` | `CPU GPU 00 00` | `hpqBIOSInt0` |
| Release fixed level | `0x2E` | `FF FF 00 00` | `hpqBIOSInt0` |
| Set LegacyDefault mode | `0x1A` | `FF 00 00 00` | `hpqBIOSInt0` |

A successful WMI return code alone is not treated as hardware acknowledgement.

## Hardware semantics established on the target

`GetFanLevel` is current speed-level telemetry. It is **not** an echo of the requested fixed target.

Fixed WMI command ownership is observed at EC:

- CPU setpoint `0x34`
- GPU setpoint `0x35`

The corrected firmware release sequence is:

```text
SetFanLevel(FF,FF)
        ->
FanMode=LegacyDefault
        ->
verify EC 0x34/0x35 == FF/FF
```

A LegacyDefault-only call returned WMI success during testing but left EC setpoints at 30/30, so it is not used as the complete release operation.

The production backend deliberately does not write EC manual/countdown fields. OMEN Gaming Hub was observed maintaining `manual=0x06` and refreshing the countdown while open, and its CPU undervolt remained unchanged throughout the bounded fan test.

## Safety scope

Ordinary fan commands are restricted to 14-50 on the exact validated target fingerprint. `FF,FF` is a dedicated release sentinel and cannot be requested as a normal policy level.

The normal application uses these operations only through `Hp88F8FanControlBackend` and `FanControlCoordinator`. The automatic policy remains disabled in v0.4.
