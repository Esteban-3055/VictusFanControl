# Suspend while Custom authority is active — hardware gate

This gate validates the production Windows power-lifecycle path on the physical
HP Victus target. It follows the successful integrated coordinator/backend
30/30 hardware test.

Automatic fan policy remains OFF.

## Required sequence

The GUI is launched in a special explicit hardware-test mode. It:

1. waits for normal Healthy telemetry;
2. requires the exact validated HP 88F8 fingerprint;
3. passes SafetyGate under a light-load envelope;
4. acquires Custom authority through FanControlCoordinator;
5. applies one fixed 30/30 command through Hp88F8FanControlBackend;
6. verifies EC 0x34/0x35 = 30/30;
7. writes a READY marker.

The PowerShell wrapper then requests Windows Suspend from a **separate process**.
This matters because the VictusFanControl GUI thread must remain free to receive
the real `WM_POWERBROADCAST/PBT_APMSUSPEND` message.

## What proves the pre-sleep restore

On entry to the real suspend handler, the GUI captures authority and EC state.
A valid test must enter the handler with:

- authority = Custom;
- EC CPU setpoint = 30;
- EC GPU setpoint = 30.

The handler then synchronously calls
`FanControlCoordinator.BlockCustomAdmissionAndRestoreAsync`.

Before `TelemetryWorker.NotifySuspend` is called and before the window procedure
returns, the GUI reads EC again. PASS requires:

- authority = Firmware;
- EC CPU setpoint = FF;
- EC GPU setpoint = FF.

That gives direct evidence that the validated `FF,FF -> LegacyDefault` path
completed before the application yielded the suspend event back to Windows.

## Resume requirements

After wake:

- a real resume event must be accepted;
- telemetry recovery must complete and reach Healthy;
- the post-boundary telemetry fence must reopen admission;
- authority must remain Firmware;
- EC 0x34/0x35 must remain FF/FF.

The special test mode then writes a PASS/FAIL result marker and exits.

## Running

Close OmenMon/OmenMon-Reborn and the normal VictusFanControl GUI. OMEN Gaming
Hub may remain open for the same undervolt-coexistence check used by the
integrated hardware gate.

Save all unrelated work, then from an elevated PowerShell in the repository:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force
.\scripts\test-suspend-custom.ps1
```

The wrapper runs build/self-tests and read-only preflight first. It asks for
`UNDERVOLT-OK`, then requires:

```text
SUSPEND30
```

Only after that confirmation does the GUI acquire 30/30 and the wrapper request
Windows Suspend.

After wake the wrapper prints the dedicated result marker and relevant persistent
log lines. Confirm the OMEN Gaming Hub undervolt with `SAME`.

## Out of scope

Do not kill the GUI from Task Manager during this test. Forced-process
termination and EC countdown/watchdog recovery is the next independent failure
mode and has not yet been characterized.
