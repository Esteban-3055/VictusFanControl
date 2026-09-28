# HP 8C40 watchdog M2 - LocalSystem / Session-0 read-only dependency gate

Status: **prepared in code; physical HP 8C40 execution still required.**

M2 is deliberately read-only. Its purpose is to prove that the exact
HP 8C40 target can expose the two hardware dependencies required by a future
restore-only watchdog while running as a real Windows service under LocalSystem
in Session 0.

A PASS in M2 does **not** authorize service-side restore or ordinary fan
control.

## Exact target

M2 accepts only:

- target profile: `HP-8C40-9D0R1LA-F18`;
- board: `HP / 8C40 / 63.43`;
- system: `Victus by HP Gaming Laptop 15-fa1xxx`;
- SKU base: `9D0R1LA`;
- BIOS: `F.18`;
- service context: LocalSystem SID `S-1-5-18`;
- Windows Session: `0`.

Any mismatch is fail-closed before EC or HP BIOS/WMI dependency reads.

## Read-only hardware surface

The M2 service receives an intentionally narrow interface containing only:

~~~text
ReadHardwareIdentity()
ReadEcSetpoint()
ReadBiosCurrentLevels()
~~~

There is no method for:

- `SetFanLevel`;
- FF/FF release;
- `LegacyDefault`;
- `RestoreFirmwareAuto`;
- watchdog lease prepare/commit/release;
- arbitrary EC register writes;
- automatic fan policy.

CI reflects over this interface and fails if an unexpected non-read operation
is added.

## Physical reads

On the exact target, one M2 service start performs only:

1. registry-based hardware identity read;
2. narrow PawnIO/ACPI EC ownership read of profile-selected setpoints
   `0x34/0x35`;
3. HP BIOS/WMI `GetFanLevel` read.

The ACPI EC read protocol necessarily writes the ACPI READ command and register
address to the EC command/data I/O ports. It does **not** write an EC register
value.

`GetFanLevel` remains current/effective fan-speed telemetry and is not treated
as setpoint acknowledgement.

## Isolation

M2 uses a dedicated demand-start service:

~~~text
VictusFanControlWatchdogM2
~~~

and an isolated ProgramData tree:

~~~text
%ProgramData%\VictusFanControl\WatchdogM2
~~~

The M2 path does not instantiate `WatchdogLeaseManager`, does not create
`lease.json`, and does not use the historical HP 88F8 Gate D recovery adapter.

The installer stops other VictusFanControl watchdog validation/service
instances before M2 characterization so direct EC/WMI dependency access is not
being tested concurrently.

## Synthetic CI gate

Run:

~~~powershell
dotnet run --project .\src\VictusFanControl.Watchdog -c Release --no-build -- --m2-8c40-self-test
~~~

The self-test pins:

- the read-only hardware interface surface;
- exact 8C40 + LocalSystem + Session-0 success semantics;
- rejection of wrong service context before hardware access;
- exact-target mismatch before EC/WMI reads;
- fail-closed behavior when the WMI read fails.

This synthetic gate performs no real hardware access.

## Physical gate

Run from elevated PowerShell on the exact HP 8C40 target:

~~~powershell
git pull
.\scripts\test-watchdog-m2-8c40.ps1
~~~

The default physical gate performs two short service start/stop cycles. It is
not a long soak.

Before service installation it requires a clean read-only EC baseline of
`FF/FF`. For every cycle it requires:

- result success;
- LocalSystem SID `S-1-5-18`;
- Session 0;
- exact target profile id;
- successful narrow EC setpoint read;
- successful HP BIOS/WMI `GetFanLevel` read;
- service-observed EC ownership still `FF/FF`;
- no watchdog lease journal;
- the service remains Running after publishing the result.

After the cycles, an independent interactive read-only EC probe must still
report `FF/FF`.

A physical M2 PASS proves dependency access only. It does not change
`WatchdogRecoveryValidated`, does not remove the production 8C40 watchdog
guards, and does not enable automatic/adaptive policy.

## Boundary after M2

Only after a real M2 PASS may M3 be prepared.

M3 is a separate physical authorization boundary: a LocalSystem service-side
restore from one known VFC-owned equal `30/30` state using the already
qualified local primitive:

~~~text
FF/FF release -> LegacyDefault -> EC FF/FF verification
~~~

M3 must preserve the external-override rules and remains distinct from crash
recovery, double-death and Modern Standby lifecycle qualification.
