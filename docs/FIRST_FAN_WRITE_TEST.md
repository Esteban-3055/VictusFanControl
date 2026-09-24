# First HP 88F8 fan-write validation

This is the first bounded fan-level write test for the validated HP 88F8 target.

It is deliberately **not** a configurable fan controller.

## Fixed test

- fan level: CPU 30 / GPU 30
- maximum custom duration: 15 seconds
- telemetry interval: approximately 1 second
- RPM acknowledgement deadline: 8 seconds
- acknowledgement criterion: both tachometers at or above 2500 RPM
- CPU thermal handoff: 95 C
- GPU thermal handoff: 87 C
- any incomplete/stale/implausible telemetry aborts the test
- HP `FanMode=LegacyDefault` is requested in a `finally` block after a successful fan-level write

Before the fan-level write, the PowerShell wrapper first tests `LegacyDefault` restoration. If that operation fails, the manual fan test is never started.

## Undervolt coexistence

OMEN Gaming Hub should remain open with the user's normal CPU undervolt. The wrapper explicitly asks the user to verify the undervolt before and after the test.

VictusFanControl does not issue voltage, CPU power, performance-profile or undervolt commands in this test.

## Running

After pulling the latest branch:

```powershell
.\scripts\test-first-fan-write.ps1
```

Do not terminate the process through Task Manager during this first validation. Ctrl+C is handled and still executes the `finally` restore path. Forced-process termination is a separate failure-mode test to perform only after firmware/watchdog recovery behavior is characterized.
