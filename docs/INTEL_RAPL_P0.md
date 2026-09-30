# Intel RAPL power-limit feasibility (P0)

Target under test: `HP-8C40-9D0R1LA-F18` / Intel Core i7-13700H.

Status: **P0 READ-ONLY probe prepared. No RAPL write path exists in this branch yet.**

## Purpose

Before implementing CPU package power limiting, prove on the exact HP 8C40/F.18 target that:

1. `MSR_RAPL_POWER_UNIT (0x606)` is readable;
2. `MSR_PKG_POWER_LIMIT (0x610)` is readable and can be decoded;
3. `MSR_PKG_POWER_INFO (0x614)` is readable and can be decoded;
4. the package power-limit lock bit can be observed without mutation;
5. the raw 0x610 value can be observed briefly for firmware/DTT changes.

The P0 path is intentionally read-only. It never invokes PawnIO `ioctl_write_msr`.

## Interpretation

- `READ_ONLY_PASS__WRITE_LOCKED`: package limit register is locked. Do not proceed to P1 writes.
- `READ_ONLY_PASS__DYNAMIC_LIMITS_OBSERVED`: 0x610 changed during the short observation. Characterize HP/DTT behavior before any write.
- `READ_ONLY_PASS__WRITE_TEST_CANDIDATE`: 0x610 was readable, unlocked and stable for the short idle observation. This authorizes only preparation of a bounded P1 write gate; it does not prove write persistence.
- `FAIL_CLOSED*`: do not proceed.

## Safety boundary

P0 does not:

- write MSR 0x610 or any other MSR;
- change PL1, PL2, Tau, Clamp or Lock;
- modify fan state;
- create a watchdog lease;
- touch HP WMI fan control;
- change Intel DTT or OMEN settings.

A future P1 write test must be a separate explicit authorization boundary with exact baseline capture, read-modify-write, immediate readback and guaranteed restoration logic.
