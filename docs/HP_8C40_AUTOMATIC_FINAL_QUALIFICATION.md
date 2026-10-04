# HP 8C40 — final Automatic qualification

Branch prepared: `feature/victus-8c40-automatic-final-qualification`

This branch starts from the fan/Automatic HEAD `e1b199b55ea9e95bc83b77b7a6f7bbff7002e6d1` and prepares the first supervised physical gate for the **final GUI + final fan backend path**.

## Scope of this gate

This is **A1: normal-path qualification**.

It is deliberately narrower than final product promotion. It proves:

1. normal user Automatic remains closed;
2. an explicit exact-target qualification launch can temporarily authorize Automatic;
3. the real P13 GUI Automatic button changes only logical mode at first;
4. fresh telemetry drives `AdaptiveFanProductionController.ProcessAutomaticAsync`;
5. real changed targets go through `FanControlCoordinator` and the current HP 8C40 product backend;
6. at least 30 Automatic decisions and at least 2 real fan-command decisions are observed;
7. the real P13 Firmware button performs strong restore;
8. final restore requires local firmware ACK, watchdog RELEASE, durable journal absence and two consecutive EC `FF/FF` observations.

This gate does **not** promote normal Automatic.

## Hard isolation

Normal product gate remains:

```text
Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized = false
```

The dedicated gate requires all of:

- command line mode `--8c40-automatic-final-qualification`;
- exact token `8C40-AUTOMATIC-FINAL`;
- isolated marker root;
- exact target HP-8C40-9D0R1LA-F18;
- normal product Automatic still closed;
- writable final backend;
- Firmware authority;
- no durable watchdog journal;
- fresh healthy SafetyGate samples.

Manual is disabled inside this qualification process to avoid mixed evidence.

## Frozen A1 profile

A1 pins the operating/timing envelope instead of treating every editable timing combination as already proven. The saved curve profile itself is captured in READY evidence so the exact curves used in the physical run remain auditable:

- CPU demand source: average of the **3 hottest P-Cores**;
- fan envelope: **30..50**;
- normal polling: 1000 ms;
- normal step: maximum +1 / -1 level;
- thermal-demand memory: disabled;
- normal rise: EMA 8 s, confirmation 3 s;
- short-load descent: EMA 6 s, confirmation 4 s;
- sustained-load descent: EMA 20 s, confirmation 16 s;
- sustained-load qualification: 1200 s observed loaded intervals;
- load thresholds: CPU/GPU utilization 50 %, CPU package 25 W, GPU 40 W;
- tolerated pause: 30 s;
- sustained-load cooldown: 120 s idle;
- thermal response threshold: CPU 85 C, GPU 78 C;
- existing emergency admission remains CPU 95..98.x confirmation / CPU >=99 immediate / GPU >=87 immediate.

A settings file that does not match this A1 profile is rejected before READY.

## Running A1

Before launching the gate, configure and save the candidate in the normal GUI while still in Firmware.

Then, from elevated PowerShell:

```powershell
git switch feature/victus-8c40-automatic-final-qualification
git pull --ff-only
.\scripts\test-automatic-final-qualification.ps1
```

The harness verifies branch/upstream identity, clean tracked state, exact hardware, AC power, battery sanity, no conflicting controller, PawnIO modules, persisted profile and absence of a stale fan journal.

When READY is printed:

1. click **Automatic** once;
2. run a representative workload long enough to obtain at least one changed fan level;
3. observe normal response;
4. click **Firmware** once.

Do not use Manual, edit settings, disconnect AC, suspend, or close the GUI during A1.

The application writes:

- `automatic-final.ready.json`;
- `automatic-final.events.jsonl`;
- `automatic-final.result.json`.

The parent harness also performs a final independent EC setpoint proof and packages the evidence ZIP with a SHA-256 sidecar.

## Acquisition during fan acknowledgement

The worker awaits one Automatic operation at a time. The backend's mechanical
acknowledgement can legitimately last longer than the 3-second telemetry watchdog.
During that wait, after dispatch and each completed native RPM proof, the backend
requests a CPU/GPU acquisition through the same worker reader and hardware-read gate.
This acquisition consumes the shared RPM publication with periodic fan queries
disabled. It starts no second polling loop or fan writer.

Only complete, fresh acquisitions in the current Healthy power epoch renew telemetry
liveness. Suspension, recovery, cancellation, incomplete/stale samples and thermal
admission loss still abort the operation. The acknowledgement deadline and watchdog
threshold remain unchanged. A blocked native RPM query cannot generate a refresh.

The newest acknowledgement acquisition stays published when the operation returns;
the worker uses it for health evaluation instead of replaying the pre-command sample.
The same publication rule applies to normal and recovery processing.
Power-epoch and cancellation checks also run after awaiting the processor, so
suspend/resume during an actuation cannot publish or count a pre-boundary sample.

Firmware is always available through the qualification pre-action fence, including
the historical Manual gates. Sequence and minimum-evidence checks still run after
the release, so an early Firmware return fails the gate while allowing safe cleanup.

The A1 evidence now records `automatic-operation-started` and
`automatic-operation-interrupted`, including the exception and restore authority.
Zero completed hardware-command decisions do not prove that no native write was
attempted. Runtime failure is recorded from the first Automatic selection, even
before the first confirmed command. Both PASS and FAIL_CLOSED packages include the
daily application log when available and a SHA-256 sidecar.

Hardware-free regression checks cover acknowledgement exceeding 3 seconds,
cancellation/thermal rejection with Firmware restore, blocked WMI, independent
acknowledgement expiry, and publication order:

```powershell
dotnet run --project .\src\VictusFanControl -c Release -- --hp-backend-self-test
dotnet run --project .\src\VictusFanControl.App -c Release -- --telemetry-coordination-self-test
dotnet run --project .\src\VictusFanControl.App -c Release -- --dashboard-self-test
```

Physical A1 is still required to qualify these changes on HP 8C40/F.18.

### A1 evidence: first command confirmed, policy continuity rejected

The physical run `automatic-final-normal_2026-10-04_202116.zip` used source
`93b220233769b31a9b360c1a8d4b92a51eb0ebfc`. Its SHA-256 is
`c1ac58e10a4c531f6c58939da580a425149a3b9e296be78d6f745c0deadb59d8`.
The result remains FAIL_CLOSED, with two decisions and one completed command.

The first acquisition was at 23:21:29.5998932 UTC. Its 30/30 command completed
at 23:21:33.693577 UTC with two fresh WMI confirmations: baseline 2200/1900 RPM,
confirmed nominal 2700/2500 RPM. No telemetry watchdog expiry was logged in this
run. The next decision acquisition at 23:21:35.0618799 UTC was 5.462 seconds after
the first decision's acquisition. Policy continuity had not consumed the real
acquisitions made during acknowledgement, so it refused that interval. Firmware
restore completed and the result reported no remaining journal.

Acknowledgement acquisitions now also update validated policy demand/EMA and
workload history. They keep the selected fan target and planner unchanged, clear
normal increase/decrease confirmation windows, and issue no additional command.
The next normal decision resumes from the last real acquisition. Missing samples,
duplicates and invalid sensors retain their rejection behavior and the original
3-second continuity limit. Observation is permitted only inside an active Automatic
actuation with matching current thermal admission, without reacquiring the
controller operation semaphore. Evidence records these samples as
`automatic-actuation-observation`, separately from completed decisions.

## PASS meaning

A1 PASS means only:

- the final GUI selection,
- final Automatic controller path,
- final writable fan backend,
- changed-level actuation,
- and explicit Firmware restore

worked together on the exact target during a supervised normal session.

It does **not** yet authorize:

- normal product Automatic;
- startup Automatic;
- profile-triggered Automatic;
- Automatic + Performance Control;
- destructive telemetry-loss qualification;
- app-exit-while-Automatic qualification;
- 20-minute real sustained-load descent qualification.

## Next gates after A1

After A1 physical PASS is reviewed and committed without rewriting evidence:

- **A2:** explicit app/tray exit while Automatic is active; prove final watchdog/restore behavior.
- **A3:** controlled loss/staleness of telemetry while Automatic is active; prove fail-closed Firmware handoff.
- **B:** endurance run: >=20 min representative load, then unload and observe the sustained-load slow descent and eventual 120 s idle cooldown.

Only after A1/A2/A3/B should the project consider promoting normal Automatic or merging the fan branch into the Performance integration branch.
