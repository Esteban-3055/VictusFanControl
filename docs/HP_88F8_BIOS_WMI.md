# HP 88F8 BIOS/WMI fan control contract

This document records the minimal HP WMI contract used by VictusFanControl.
The implementation is independent and does not require OmenMon at runtime.

## Restore HP firmware fan policy

The previously validated OmenMon operation:

```powershell
OmenMon.exe -Bios FanMode=LegacyDefault
```

maps to the HP OMEN WMI BIOS interface:

- namespace: `root\wmi`
- BIOS method class: `hpqBIntM`
- instance: `ACPI\PNP0C14\0_0`
- input data class: `hpqBDataIn`
- method: `hpqBIOSInt0`
- signature: ASCII `SECU`
- Command: `0x00020008`
- CommandType: `0x1A`
- payload: `FF 00 00 00`
- success return code: `0`

The second payload byte is the fan-mode value; `00` is LegacyDefault.

## Safety

The normal GUI path still uses `DisabledFanControlBackend`; this WMI operation is exposed only through the explicit experimental restore test.

The restore operation is board-gated to HP `88F8`.

Run:

```powershell
.\scripts\test-restore-hp-auto.ps1
```

The script requires typing `RESTORE` before performing the BIOS/WMI call. By default it captures the known 88F8 EC fan-control state before and after the call.

This operation changes only the HP fan-mode command envelope above. It does not issue CPU-voltage or undervolt commands. Undervolt preservation will still be verified during the first real fan-control test.
