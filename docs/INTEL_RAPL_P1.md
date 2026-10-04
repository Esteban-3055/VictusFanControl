# CPU package power-limit qualification — bounded P1

This is an experimental CPU-only tool for the exact HP 8C40 / 9D0R1LA /
F.18 / Intel Core i7-13700H. It is **not a production power limiter** and is
not connected to fan profiles, the GUI, GPU control or startup persistence.

Complete the ongoing EC/WMI evidence capture before this separate experiment.
Close VictusFanControl and its fan watchdog. Leave firmware fan control active.
Keep AC power connected. Do not change OMEN/XTU/ThrottleStop power policies
during a run; this probe does not disable HP services or Intel DTT.

## Run from elevated Windows PowerShell in the repository root

```powershell
# Default is read-only: inventory, units, decoded PL1/PL2/Tau/lock and stability.
.\scripts\test-intel-rapl-p1.ps1 -DurationSeconds 90

# Separate explicitly requested WRITE test, after reviewing the read-only ZIP.
.\scripts\test-intel-rapl-p1.ps1 -WriteTest -DurationSeconds 60
```

Both runs display their actions and create a ZIP and SHA-256 under `logs`.
The signed `modules/IntelMSR.bin` and PawnIO 2.2+ must already be installed;
`scripts/setup-pawnio-modules.ps1` installs the pinned official 0.2.11 module.
The script builds with .NET 8 and runs hardware-free fixtures before opening
the physical backend. Software fixtures alone: `-SelfTest`.

## What P1 does

1. Validate administrator privileges, exact board/SKU/BIOS, CPU identity,
   expected physical core count, module/assembly hashes and AC status.
2. Start an independent guardian with no console. It owns the CPU write
   session and a global single-probe mutex. The launcher never writes MSRs.
3. Observe 10 baseline samples. Reject changing raw 0x610 values, lock,
   disabled limits, invalid power ordering/ranges and temperature >=85 C.
4. Compute PL1 and PL2 as 80% of their respective original raw power fields,
   rounded down. Neither limit can increase; PL1 must stay >=10 W and not
   below a nonzero minimum reported by 0x614. Preserve all other fields.
5. Persist the baseline/request journal before the ioctl, read the baseline
   again and write **only MSR 0x610**, once. Require exact immediate readback.
6. Observe for 10–120 seconds; default 60. Stop on changed limits, AC loss,
   missing/invalid temperature, temperature >=90 C, parent exit, Stop/Ctrl+C,
   excessive sampling gap or lease expiry. Do not reapply against OEM writers.
7. Restore owned power fields conditionally, verify readback, then observe
   another 10 seconds without further writes. Cancellation never skips the
   restore attempt. An ioctl error is treated as possibly having written.

The first P1 can start at idle, below 85 C. After the console prints
`IMMEDIATE_READBACK_EXACT_MATCH`, a repeatable CPU workload may be started.
This can establish write acceptance/persistence. A causal effectiveness
comparison needs comparable baseline and limited workloads; idle-to-load
results are explicitly inconclusive. There is no automatic stress workload.

## Interpret evidence

- `READ_ONLY_LOCK_OBSERVED`: writes are unavailable; do not bypass the lock.
- `READ_ONLY_DYNAMIC_LIMITS`: characterize HP/DTT before any write test.
- `WRITE_ACCEPTED_AND_PERSISTED`: exact readback and no sampled register
  changes for this run. This does not prove exclusive/effective MSR control.
- `WRITE_REJECTED_OR_IMMEDIATE_OVERRIDE`: write did not read back as requested.
- `LIMIT_CHANGED_EXTERNALLY`: OEM/another writer changed the register;
  no repeated enforcement is attempted.
- `BASELINE_VERIFIED`: the original raw value was read back after release.
- `EXTERNAL_CHANGE_PRESERVED`: another value is retained; only still-owned
  power fields are eligible for restoration. Original baseline is not claimed.
- `RESTORE_BLOCKED_LOCK`, changed nonpower fields or an I/O error: restoration
  is not confirmed. Preserve the evidence; do not repeat or remove the journal
  to force another write test.

`summary.json` keeps application, restoration and enforcement assessment
separate. `samples.csv` contains UTC, phase, raw register, decoded limits,
lock, CPU package power, temperature, load and AC status. `hardware.json`
records identity, versions and hashes; `write-journal.json` records ownership.
The guardian leaves an unresolved marker in
`%ProgramData%\VictusFanControl\CpuProbe\active-write.json` when release cannot
be fully confirmed. Further P1 writes are blocked; read-only diagnosis remains
available. No journal is blindly replayed at boot.

## Recovery limits

The guardian can attempt release after its launcher dies and at its bounded
deadline. It cannot guarantee restoration after guardian termination, kernel
or PawnIO stalls, power loss, or a new firmware lock. Keep the guardian alive.
Do not use `Stop-Process` on the whole process tree. If it is still running,
the script does not ZIP changing evidence or kill it. A normal reboot is an
operational fallback for unresolved volatile limits; verify a fresh baseline
and review the journal before another experiment.

Restore uses read/compare/write; MSR has no atomic compare-and-swap against
OEM software. There remains a race between final read and write. Any changed
Tau/Clamp/Enable/Lock/reserved field blocks automatic restoration; power values
changed externally are preserved. MSR readback and a power drop alone do not
exclude MMIO RAPL, Intel DTT, thermal throttling or changing workload. The tool
does not bypass locks, write MMIO, access EC, change voltage or modify GPU clocks.

## Sources

- Intel SDM Vol. 3B, RAPL package domain: MSR 0x606 units and 0x610 fields,
  enable/clamp/time windows/lock:
  https://www.intel.com/content/dam/www/public/us/en/documents/manuals/64-ia-32-architectures-software-developer-vol-3b-part-2-manual.pdf
- Pinned signed-module source, whitelist and write ABI (two input cells,
  address/value; no output cells):
  https://github.com/namazso/PawnIO.Modules/blob/0.2.11/IntelMSR.p
- Decoder reused from project branch `feature/intel-rapl-feasibility`, commit
  `a49f82e3a5a083ce4a55570adb56607a197f7d2a`; that branch remains read-only.

## Verification

CI builds the solution with warnings as errors and runs the CPU codec/policy
and engine fixtures without physical I/O. Cases cover preserved fields,
external writes, baseline instability, disabled/locked limits, rejected and
partially successful writes, parent exit, cancellation, AC loss, thermal
abort and failed restoration. Physical acceptance on the Victus remains
unverified until its evidence is returned.
