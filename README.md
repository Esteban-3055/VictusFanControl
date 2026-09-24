# VictusFanControl

Experimental adaptive fan-control project for HP Victus laptops, starting with the validated HP **88F8** development target.

> **Current development status: v0.4 backend integration.** The hardware-validated HP 88F8 fan backend is integrated behind the central safety/authority coordinator. The automatic fan policy is still **OFF**, so launching the GUI does not acquire custom fan authority or issue fan-level commands.

## Development target

Write capability is intentionally narrower than Product ID alone:

- HP Victus 16-d0515la family
- system product: `Victus by HP Laptop 16-d0xxx`
- SKU prefix: `62C37LA`
- HP motherboard Product ID `88F8`, board version `88.58`
- Intel Core i7-11800H
- NVIDIA GeForce RTX 3060 Laptop GPU
- normal reference configuration: external monitor connected, RTX 3060 intentionally active

No unique serial numbers are stored in this repository.

## Current architecture

```text
Telemetry
  PawnIO Intel MSR / ACPI EC
  NVIDIA NVML
  Windows CPU load
          |
          v
Runtime state + SafetyGate
          |
          v
FanControlCoordinator
  Firmware / Custom / Restoring / Faulted
          |
          v
Hp88F8FanControlBackend
  WMI SetFanLevel
  EC setpoint acknowledgement
  dual-tachometer acknowledgement
  FF,FF -> LegacyDefault restore
          |
          v
HP firmware / fans
```

The GUI creates this route, but no adaptive policy currently calls `TryEnterCustomAsync` or `ApplyAsync`. HP firmware therefore remains authoritative during ordinary GUI use.

## Validated target fan observations

| Requested WMI fan level | CPU fan | GPU fan | Notes |
|---:|---:|---:|---|
| 14 | ~1,400 RPM | ~1,400 RPM | Stable low-speed point; restart-from-rest still needs dedicated validation |
| 30 | ~3,000 RPM | ~3,000 RPM | Hardware write/ack/restore test passed |
| 50 | ~4,330 RPM | ~4,670 RPM | Different physical ceilings |

At level 30 the two fans converged near the same physical RPM while EC rate readback was approximately 75% CPU / 68% GPU. The final controller will therefore use one shared physical RPM target with independent per-fan feedback/compensation rather than assuming equal low-level drive implies equal RPM.

## Hardware validations completed

- direct PawnIO/NVML telemetry and both tachometers;
- 30-minute current-reader health soak with 1629/1629 complete samples;
- post-fix suspend/resume behavior tested successfully for 2 cycles (remaining 3 planned cycles were explicitly waived, so lifecycle hardware validation is partial);
- WMI `SetFanLevel(30,30)`;
- EC 0x34/0x35 command ownership;
- stable dual-fan ~3000 RPM response;
- firmware release `SetFanLevel(FF,FF) -> FanMode=LegacyDefault`;
- OMEN Gaming Hub left open and CPU undervolt preserved before/after the bounded write test.

## Safety integration

The v0.4 backend fails closed on:

- target fingerprint mismatch;
- missing/stale/implausible telemetry;
- thermal emergency gate;
- command outside the central 14-50 range;
- existing external fixed-level ownership during admission;
- setpoint acknowledgement failure;
- CPU or GPU tachometer non-response;
- external setpoint overwrite;
- suspend/resume freshness boundary;
- backend exceptions.

Suspend, safety loss and exit return authority to HP when the process is still executing. Forced process termination cannot be protected by managed cleanup and still requires firmware countdown/watchdog characterization before unattended automatic control is enabled.

## Development setup

Requirements: Windows 11 x64, .NET 8 SDK, Administrator terminal, PawnIO 2.2+, and the NVIDIA driver/NVML.

```powershell
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

The GUI reports backend readiness and fan authority, but **automatic control remains disabled**.

See `docs/BACKEND_INTEGRATION_V0.4.md`, `docs/SAFETY.md`, `docs/PRE_CONTROL_CHECKLIST.md`, and `docs/OMENMON_COMPAT_AUDIT.md`.

## License

MIT. See [LICENSE](LICENSE). Third-party components and licenses are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
