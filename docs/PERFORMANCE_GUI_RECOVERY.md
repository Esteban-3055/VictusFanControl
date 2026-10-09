# Explicit GUI Performance recovery

Normal GUI startup continues to refuse existing CPU/GPU journals. A persisted
journal records prior ownership, not current hardware state or proof of release.
Recovery is an explicit, release-only action; it never starts Automatic, writes
fans, or reapplies PL1/PL2 or GPU presets.

Close VictusFanControl normally, including the tray, and close other GPU clock
controllers (Afterburner or scripts running `nvidia-smi -lgc`). Run from an
elevated PowerShell in a newly extracted, verified package:

```powershell
.\Start-ProductGui.ps1 -Mode Verify
.\Start-ProductGui.ps1 -Mode RecoverPerformance `
    -ExpectedCpuSession '<SessionId from cpu-power-session.json>' `
    -ExpectedGpuSession '<SessionId from gpu-clock-session.json>' `
    -ConfirmExclusiveGpuController
```

Both IDs must be nonempty and match each present journal. Missing journals are
already resolved; rerunning after partial recovery never reapplies a preset.
The helper requires elevation, the exact HP 8C40/F.18 target and i7-13700H,
the approved IntelMSR module hash, exclusive production Performance mutex,
and absence of other VictusFanControl processes. It does not kill processes.

Before opening hardware backends it validates both journals and durably copies
their exact bytes into a new `Victus-Performance-recovery-*` desktop directory.
A changed/corrupt/wrong-session journal is refused. A recovery report records
the result; retain the directory on failure. Never manually delete journals to
bypass the gate.

CPU uses the existing recovery executor: observe current RAPL, restore only
still-owned PL1/PL2 fields to the stored release target, preserve other MSR bits
and external changes, durably arm the write, compare again, and read back. A
locked or unresolved still-owned state retains its journal and blocks GPU reset.

GPU has no reliable exact locked-range ownership readback. The user's explicit
exclusive-controller confirmation authorizes one NVML Reset to NVIDIA defaults.
Reset is durably armed first; an unsuccessful or throwing call retains the
journal. A successful NVML acknowledgement permits clearing only the unchanged
armed record. It is not a measured locked-range readback or restored previous
external clock setting.

The 2026-10-06 diagnostic contains earlier successful CLIENT_SHUTDOWN releases,
but no terminal report for the 06:15 session. Its CPU Owned 30/50 W and GPU
ActiveUnverified 210/1802 MHz records cannot prove why the processes ended or
what limits remain in hardware. This recovery entry is covered by fake-backend
fixtures; physical recovery remains pending until its report is reviewed.
