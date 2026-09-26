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

The READY marker is published only after the real production backend has
acknowledged the 30/30 command and a separate EC read has verified 0x34/0x35 =
30/30. That verified EC state and its timestamp are retained by the test mode.

On entry to the real suspend handler, a valid test must still have:

- authority = Custom;
- the retained, already-verified owned EC state = 30/30.

The handler deliberately does **not** issue another diagnostic EC read before
restore. A first real run showed that an extra probe can lose the shared
`Global\Access_EC` mutex race against normal telemetry and time out inside the
critical Windows suspend path, even though the subsequent coordinator restore
succeeds. Avoiding that redundant read makes the test less intrusive and gives
the lifecycle restore first access to the shared EC/WMI path.

The handler then synchronously calls
`FanControlCoordinator.BlockCustomAdmissionAndRestoreAsync`.

Before `TelemetryWorker.NotifySuspend` is called and before the window procedure
returns, the GUI reads EC. PASS requires:

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


## First physical attempt: instrumentation false negative

The first physical run reached Custom authority, verified EC 30/30, and received
the real suspend event. The diagnostic EC read that had been placed immediately
at suspend-handler entry timed out waiting for `Global\Access_EC`. The actual
coordinator restore then completed, and the post-restore read **inside the same
suspend handler** showed authority Firmware with EC FF/FF. Resume subsequently
recovered to Healthy/Firmware with EC FF/FF and the OMEN Gaming Hub undervolt
unchanged.

That run therefore provided positive evidence for the production restore path,
but the harness marked FAIL because it incorrectly made the redundant
pre-restore diagnostic read mandatory. The harness was corrected to retain the
already-verified 30/30 arming state and remove that competing EC transaction
from the critical suspend path. A clean rerun is still required before closing
the gate.
