# VictusFanControl

VictusFanControl is an experimental, fail-closed fan-control application for the exact validated HP Victus target **HP 8C40 / 9D0R1LA / BIOS F.18**.

## Current GUI phase

The normal Windows application now opens the rebuilt product interface: seven pages,
exactly two editable AC/Battery profiles, six independent fan demand curves per
profile, a node graph, CPU PL1/PL2 and GPU clock preferences, and live telemetry.
Editing and saving never grant hardware authority. Apply and Release remain explicit.
Normal Automatic and custom GPU clock execution remain closed pending qualification.

The historical qualification GUI is retained only for explicit test entries.
Blocks 2 and 3 are paused until this GUI phase is complete. See
[the GUI audit and execution boundaries](docs/GUI_PRODUCT_PHASE.md) for current
behavior, deliberate reference adaptations, and validation limits. Older milestone
sections below describe their original qualification state.

## Current target

The active production-development target is:

- HP Victus 15-fa1xxx family, validated SKU prefix `9D0R1LA`
- motherboard `HP 8C40`, board revision `63.43`
- BIOS `F.18`
- Intel Core i7-13700H, 14 physical cores
- NVIDIA GeForce RTX 4060 Laptop GPU
- equal CPU/GPU fan commands only, physically validated from level **10 through 50**

The older HP 88F8 target remains in the repository as historical/legacy support and qualification evidence. It is not the target of the current post-M9 work.

## Current control state

The M9 production watchdog path has been promoted for the exact HP 8C40 target. Normal backend construction now uses the target-bound M4 watchdog lease and the existing fail-closed ownership/restore protocol.

User fan control is still deliberately **OFF by default**:

- `control.enabledByDefault=false`
- `automaticPolicyEnabled=false`
- no automatic curve commands are issued during ordinary startup
- M9C and M9D qualification-only construction/execution gates remain closed
- P13 user-facing Firmware/Manual/Automatic UI is software-complete
- Manual and Automatic execution gates remain closed
- post-M9 manual/automatic hardware validation is a separate later gate

Launching the GUI therefore does not by itself request Custom authority.

## Production safety contract

The supported write path is intentionally narrow:

```text
Telemetry (PawnIO Intel + ACPI EC + NVIDIA NVML + Windows load)
        |
        v
Runtime state + SafetyGate + HP 8C40 thermal confirmation
        |
        v
FanControlCoordinator
        |
        v
HP 8C40 production watchdog lease
        |
        v
Hp8C40FanControlBackend
        |
        v
HP WMI SetFanLevel -> EC setpoint ACK -> dual-tach feedback
```

Core invariants are:

- CPU and GPU fan levels are always equal.
- Validated fan envelope is 10..50; level 0/fan-stop is not used.
- EC 0x62/0x63 are diagnostic read-only; there are no arbitrary EC writes.
- GPU >= 87 C and CPU >= 99 C trigger immediate firmware handoff.
- HP 8C40 CPU 95..98.x C requires five fresh consecutive samples before effective thermal preemption.
- Missing/stale/implausible telemetry, ownership loss, watchdog failure, lifecycle fencing, or backend failure fail closed.
- Unchanged fan targets are not intended to be continuously retransmitted.
- Strong restore is firmware FF/FF + LegacyDefault + stable FF/FF + watchdog RELEASE + journal absence.

## Adaptive policy status

The hardware-independent adaptive policy engine and offline shadow/replay tooling exist and are tested. They consume CPU/GPU temperature, power and load, choose a single equal fan demand, apply bounded slew/decrease confirmation/deadband, and reject duplicate/out-of-order/gapped telemetry.

A production policy curve is **not yet physically validated** and automatic policy remains disabled. See `docs/ADAPTIVE_POLICY_PREPARATION.md` and `docs/POST_M9_SOFTWARE_ROADMAP.md`.

## Development setup

Requirements: Windows 11 x64, .NET 8 SDK, Administrator terminal for hardware/service operations, PawnIO 2.2+, and the NVIDIA driver/NVML.

```powershell
.\scripts\setup-pawnio-modules.ps1
.\scripts\probe-backends.ps1
.\scripts\run-gui.ps1
```

Software-only CI/self-tests do not authorize hardware execution. Physical gates must be opened separately and explicitly.

## License

MIT. See [LICENSE](LICENSE). Third-party components and licenses are documented in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
