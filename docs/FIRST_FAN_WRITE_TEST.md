# First HP 88F8 fan-write validation

This is the first bounded fan-level write test for the validated HP 88F8 target.

It is deliberately **not** a configurable fan controller.

## Fixed test

- fan level: CPU 30 / GPU 30
- maximum custom duration: 15 seconds
- telemetry interval: approximately 1 second
- strict light-load preflight **and continuous envelope**: CPU <= 80 C / 50 W, GPU <= 75 C / 70 W
- WMI GetFanLevel is logged only as the current speed level; it is **not** treated as command acknowledgement
- EC 0x34/0x35 must acknowledge the requested 30/30 setpoints and continue to hold them during the test
- RPM acknowledgement deadline: 8 seconds
- acknowledgement criterion: two consecutive samples with both tachometers between 2500 and 4000 RPM
- CPU thermal handoff: 95 C
- GPU thermal handoff: 87 C
- any incomplete/stale/implausible telemetry aborts the test
- a >3 second sampling/scheduling gap is treated as possible suspend/blocking and aborts to restore
- the restore path sends the dedicated `FF,FF` release sentinel and then `FanMode=LegacyDefault` in a `finally` block after **any attempted** fan-level write
- restore is considered acknowledged only when EC 0x34/0x35 return to `FF,FF`

The distinction between "attempted" and "successful" is important: OmenMon documents that on some HP systems a fan-level command may take effect even when the BIOS reports an error. Therefore VictusFanControl never assumes a thrown write was harmless.

Before the fan-level write, the PowerShell wrapper preflights the `LegacyDefault` WMI command. This proves the WMI route is callable while the machine is already under firmware control; the actual transition from fixed fan level back to HP automatic behavior is validated at the end of the bounded write test.

## State capture

The test logs WMI fan levels and expanded 88F8 EC state around the operation, including:

- 0x2C/0x2D rate targets
- 0x2E/0x2F rate readback
- 0x34/0x35 level setpoints
- 0x62 manual flag
- 0x63 countdown
- 0x95 mode
- 0xEC max-fan state
- 0xF4 fan switch
- B0/B2 tachometers

## Undervolt coexistence

OMEN Gaming Hub should remain open with the user's normal CPU undervolt. The wrapper explicitly asks the user to verify the undervolt before and after the test.

VictusFanControl does not issue voltage, CPU power, performance-profile or undervolt commands in this test.

## Running

After pulling the latest branch:

```powershell
.\scripts\test-first-fan-write.ps1
```

Do not run a game or stress test during this first validation.

Do not terminate the process through Task Manager during this first validation. Ctrl+C is handled and still executes the `finally` restore path. Forced-process termination is a separate failure-mode test to perform only after firmware/watchdog recovery behavior is characterized.


## Known competing controllers

The first write test refuses to start while OmenMon or the VictusFanControl GUI is running, to avoid unnecessary concurrent EC/fan-controller activity. OMEN Gaming Hub is intentionally **not** blocked because the target workflow keeps it open for CPU undervolt validation; actual fan ownership is checked by repeated EC 0x34/0x35 setpoint readback.

The direct CLI test also requires the explicit acknowledgement token `88F8-FAN30`. The wrapper supplies it only after the user types `FAN30`.


## Findings from the first hardware attempt

The first attempt safely aborted because the test incorrectly expected BIOS GetFanLevel to equal the requested `30,30` immediately. The hardware evidence showed:

- before write: BIOS current levels approximately 21/23 while EC setpoints were FF/FF;
- after SetFanLevel(30,30): BIOS current levels were 21/24 because the fans had only begun ramping;
- after LegacyDefault-only restore: EC setpoints still showed 30/30 and RPM was still elevated/rising.

This established that GetFanLevel is current speed level telemetry and that LegacyDefault alone is not a sufficient fixed-level release. The test has been corrected accordingly.


## Final corrected hardware result

The corrected bounded test passed on the target:

- baseline approximately 2200/2420 RPM;
- EC acknowledged 30/30;
- both fans converged around 3000 RPM;
- repeated EC ownership checks remained 30/30;
- FF,FF -> LegacyDefault returned EC setpoints to FF/FF;
- OMEN Gaming Hub remained open and the CPU undervolt was unchanged.

The standalone harness remains useful as a regression tool, but v0.4 now also contains the same validated route behind the production backend/coordinator architecture.
