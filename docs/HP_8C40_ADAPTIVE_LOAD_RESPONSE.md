# Adaptive response after brief and sustained workload

Candidate operating tuning, 2026-10-04. Automatic GUI authorization remains closed.

## Capture evidence

The 172146 capture has 139 accepted decisions. It uses CPU source 0
(PackageOrHottestCore), despite the P-core selector being available. Four thermal
increases take the target from 31 to 47. Each later descent requires a new
16-second confirmation; after about 151 seconds the target is still 39 although
raw demand is 30. This supports faster descent after short activity, not a claim
about chassis cooling following twenty minutes of load. SHA256 verified.
Release requests were accepted; firmware restoration is not independently
verified in this capture (FirmwareRestorationVerified=false).

## Candidate response

- Normal rise: EMA time constant 8 seconds, continuous confirmation 3 seconds,
  maximum one level per step. Time constant is not a fixed delay.
- Brief workload descent: EMA time constant 6 seconds, confirmation 4 seconds,
  existing maximum one level per step.
- Sustained workload descent: existing EMA 20 seconds, confirmation 16 seconds.
- Qualify sustained workload after 1200 seconds of observed loaded intervals.
  CPU or GPU utilization >=50%, CPU power >=25 W, or GPU power >=40 W qualifies
  a sample. Only intervals bounded by two loaded samples are accumulated.
  Pauses up to 30 seconds preserve the count but do not add loaded time; a longer
  idle pause resets unqualified history.
- Once qualified, retain slow descent until 120 seconds of observed continuous
  idle. A new loaded sample resets that idle window. The demand curves still
  prevent descent while temperatures or power require higher levels.
- Invalid or discontinuous input cannot certify cooling. It breaks accumulation
  while preserving an already qualified slow descent. Explicit session Reset
  clears history; no previous-session or pre-start load duration is inferred.
- Thermal overrides and raw emergency admission thresholds remain unchanged.

Workload history is a configurable heuristic, not a measured chassis temperature
or an exact thermal model. Even with faster descent, high raw demand prevents
lower targets. These values require acoustic and thermal observation.

## GUI and compatibility

All workload thresholds, duration, pause tolerance, cooldown and descent timings
are editable. New settings default to the candidate response. Archived settings
missing adaptiveDescentEnabled retain fixed descent and existing timings.
Use “Respuesta suave adaptativa” to stage the new timing while preserving curves,
CPU source, N, levels and remaining preferences, then Apply and save in Firmware.
Settings do not select a hardware mode or grant Automatic authorization.
Export again for supervised 30–50 testing. Ensure the desired P-core source is
selected; the latest supplied export still selects PackageOrHottestCore.

## Verification

Hardware-independent tests cover short-spike recovery, slower normal rise,
thermal bypass, twenty-minute qualification, ignored idle intervals, long-pause
reset, idle cooldown, GPU-only qualification, invalid telemetry, explicit reset,
configuration roundtrip, archived compatibility and rejection of invalid timing.
The real WinForms fixture verifies staged candidate selection, edited timing
persistence, absence of fan writes and the closed Automatic gate. Windows
compilation and real UI fixtures run through existing GitHub workflows.
