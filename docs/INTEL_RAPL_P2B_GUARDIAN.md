# Intel RAPL P2B — guardian and bounded external-writer reacquisition

P2B continues from the physically qualified P1 evidence and the P2A
`CpuPowerLimiter` software foundation. This branch still does **not** authorize
production RAPL writes or GUI integration.

## Step 1 — conflict policy (software only)

The first P2B change freezes the policy requested for an external writer that
changes the CPU package power limit while VictusFanControl has an active,
verified setting.

The policy is intentionally hardware-free:

- The first observed external change enters `Contested`.
- VictusFanControl does **not** write immediately. The first reacquisition may
  start only after 30 seconds of active system time.
- A conflict episode has at most **5** reacquisition attempts.
- Each attempt is single-shot and must be followed by exact readback.
- A successful readback remains provisional for **60 seconds**. Only 60 active
  seconds without another external change closes the episode and resets the
  five-attempt budget.
- A renewed external change during that 60-second window keeps the same budget.
- If attempt 5 fails, or another external change arrives after attempt 5
  succeeded provisionally but before the 60-second stability window closes,
  the policy enters `Yielded`.
- `Yielded` performs no further automatic writes. A later explicit user action
  is required to start a new authority episode.
- Safety/target/lifecycle logic may force `Yielded` immediately.
- Repeated external observations while already `Contested` do not postpone the
  current 30-second retry deadline.
- Timing uses `IActiveTimeClock` / `QueryUnbiasedInterruptTime`, so sleep or
  hibernation time does not manufacture a retry deadline.

## Important ownership rule for the next step

This policy only decides **when** a reacquisition is allowed. It does not yet
decide what raw value to write or what value must be restored later.

P2B Step 2 must keep these semantics separate:

1. Original session baseline remains immutable audit evidence.
2. Before each bounded reacquisition, the guardian must capture the externally
   observed raw 0x610 value as the current handoff/release candidate.
3. A reacquisition write must preserve non-owned fields and may change only the
   explicitly qualified PL1/PL2 power fields.
4. If VictusFanControl later yields or releases after overriding an external
   writer, it must not blindly restore the stale original session baseline over
   that writer. Restore/handoff must be conditional on current ownership and the
   last externally observed state.
5. Lock, invalid target, invalid telemetry, AC/lifecycle failure or an unsafe
   register shape bypass the retry budget and fail closed without another write.

## States introduced by the pure policy

- `Inactive`
- `Contested`
- `ReacquiredPendingStability`
- `Yielded`

The existing production-domain limiter states are deliberately unchanged in
Step 1. Mapping these conflict states into `CpuPowerLimiter` and the detached
guardian is a later change so the retry policy can be tested independently.

## Verification

`CpuPowerConflictPolicySelfTest` covers:

- no immediate reapply;
- 30-second active-time delay;
- repeated observations not postponing a retry;
- 60-second stability requirement;
- budget preserved across provisional success;
- five failed attempts -> `Yielded`;
- fifth provisional success followed by another conflict -> `Yielded`;
- explicit user reset creating a fresh episode;
- immediate safety/lifecycle yield.

The existing CPU probe fixture suite invokes this self-test. No new physical
MSR write path is introduced by Step 1.

Production gates remain closed:

- `productionHardwareWritesAuthorized=false`
- `guiIntegrationAuthorized=false`
- `startupPersistenceAuthorized=false`
- `automaticProfileIntegrationAuthorized=false`
