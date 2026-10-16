# NVML crash containment and battery recovery timing

## Observed evidence (16 October 2026, Windows 1.2.11)

Windows Application events 1000/1026/1001 record a fatal access violation at
06:16:27 America/Santiago, while `NvmlClient.ReadSampleWithRetries` called
`nvmlDeviceGetTemperature`. The reopened application's ZIP contains only its
new session, not that fatal stack. A pending GPU ReleaseWriteArmed journal is
subsequent recovery evidence, not proof that the user requested a normal exit.
The exact source of the invalid native access remains undetermined. A display
topology change shortly before it is a correlation, not an established cause.

The battery ZIP at 14:00:29 shows a second, reproducible software defect:
recovery durations of 10–48 seconds were included in the next loop's scheduling
gap. This repeatedly triggered the fallback resume detector. The initial
freshness watchdog and slow acquisition remain legitimate observations;
correcting the loop does not make stale sensors valid or prove every source of
delay resolved. At capture the app was running in Firmware, preparing Automatic.

## Changes

Product GPU telemetry uses a read-only auxiliary invocation of the existing app
executable. Dispatch occurs before UI, hardware identity, HP control or guardian
startup. It owns its NVML client; it accepts only initialization and sample
requests, not any write/control command. Other hardware harnesses retain their
existing reader. The same telemetry source is reused by ExternalCooling.

Private redirected pipes carry bounded JSON with version, fresh session,
sequence, original acquisition date, GPU identity and finite bounded values.
Startup has a 5 s deadline, each sample 1.5 s, frames at most 4096 characters.
EOF, timeout, fatal child exit or invalid data invalidates that acquisition;
there is no in-process NVML fallback. Failed instances wait 5 s before retry.
The existing telemetry continuity, admission, watchdog and release policies
still decide how the notebook responds. Their thresholds are unchanged.

Normal disposal closes stdin, allows bounded orderly shutdown, and terminates
only the owned read-only child if it does not exit. A separate child thread
checks its owner's process identity and a 20 s inactivity limit, including
while native code is blocked. stderr is drained without unbounded buffering.
Structured failure records contain helper PID/exit code, failure category,
invalid-sample status and at most 8192 characters of native stderr stack tail;
the helper receives no external-cooling credentials. The installed .NET console
host launches the helper DLL even when the GUI uses a WinExe apphost: private
console streams must work in both development and the packaged application.

After recovery returns, only the loop scheduling origin is reset. Acquisition,
completed-read watchdog, authority and source timestamps are not renewed.
An actual scheduling gap after that point still triggers the original detector.

## Verification and remaining physical checks

`VictusFanControl.App.exe --nvml-telemetry-self-test` exercises the actual child
process/pipes using synthetic GPU values, including fatal process termination,
silence, replay, corruption, wrong identity/session/sequence, stale/future
timestamps, oversized/incomplete frames, backoff, disposal and owner death.
The clock fixture replays battery recovery durations and a subsequent genuine
gap. These tests perform no hardware IO and do not reproduce the original
driver fault. Release build and product GUI regressions are required as well.

Before promoting a build, test on the actual Victus: AC/battery round trips,
screen changes, sleep/resume, startup Automatic and prolonged operation. Check
that the GUI stays alive, invalid GPU data cannot authorize a new request,
recovery does not loop, helpers exit, and notebook protections remain active.
Do not induce a real native fault or change GPU drivers while controlling fans.

Microsoft documents that corrupted-state access violations cannot be recovered
using ordinary catch handlers in current .NET:
https://learn.microsoft.com/en-us/dotnet/api/system.accessviolationexception
NVIDIA documents the native query and its return semantics:
https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html

No existing release is replaced by these source changes. Physical validation
and a separately reviewed preliminary package remain necessary.
