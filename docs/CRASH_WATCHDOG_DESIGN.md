# Independent crash-watchdog design

Status: design requirement established; implementation pending.

## Failure model

The validated backend can always restore HP authority while the process is
alive. Forced termination is different: the controller cannot run managed
cleanup after it has died.

Physical testing established the important target-specific fact: with OMEN
Gaming Hub open, EC 0x63 can be refreshed externally while the dead
VictusFanControl process's 30/30 setpoint remains installed. The firmware
countdown therefore cannot be the sole crash-recovery mechanism.

## Required architecture

Use two failure domains:

```text
VictusFanControl GUI / adaptive controller
          |
          | renewable lease / heartbeat
          v
Independent watchdog process or Windows service
          |
          | only on lease loss / owner death
          v
validated HP restore:
SetFanLevel(FF,FF) -> LegacyDefault -> verify EC FF/FF
```

The watchdog must not depend on the GUI event loop, GUI process lifetime,
`finally`, or the controller's in-process coordinator.

## Safety requirements

The first implementation should remain deliberately narrow:

1. exact HP 88F8 target fingerprint only;
2. no arbitrary EC writes;
3. watchdog may read EC state but restores through the validated BIOS/WMI path;
4. controller must hold a short renewable lease only while it owns a fixed
   setpoint;
5. loss of heartbeat, owner PID death, malformed lease state, or watchdog
   restart with an orphaned VFC-owned fixed setpoint must fail toward HP
   firmware authority;
6. restore is `FF,FF -> LegacyDefault`, followed by mandatory EC FF/FF
   verification;
7. watchdog must not clear an unknown/external fixed override unless the lease
   proves the setpoint belongs to the VFC session;
8. controller startup must refuse Custom authority if watchdog readiness cannot
   be proven;
9. watchdog shutdown/update must first force a firmware handoff or prevent new
   Custom admission;
10. automatic fan policy remains OFF until crash-watchdog hardware tests pass.

## Lease concept

The controller should create a session identifier when entering Custom
authority and renew a monotonic heartbeat at a short interval. The independent
watchdog owns the timeout decision.

The lease should contain only the information necessary to establish ownership,
for example:

- random session identifier;
- controller PID plus process start identity;
- expected owned CPU/GPU setpoint;
- last heartbeat monotonic timestamp/counter;
- protocol version.

A stale file timestamp alone is insufficient because wall-clock changes can
produce ambiguity. The watchdog should combine process identity with an
explicit renewable heartbeat and its own monotonic timeout.

## Proposed conservative timing

Initial hardware validation should use a short but non-aggressive envelope, for
example a 1-second heartbeat with a 5-second watchdog lease. These values are
not final controller constants; they are a starting point for measuring
scheduler stalls and service recovery without creating nuisance restores.

## Next hardware gate

A valid crash-watchdog gate should:

1. start watchdog and prove it is armed;
2. enter Custom and verify 30/30;
3. prove the lease is actively renewing;
4. force-kill only the GUI/controller;
5. verify the watchdog detects lease loss;
6. verify it sends `FF,FF -> LegacyDefault`;
7. verify EC returns to FF/FF without help from the parent test script;
8. repeat with the parent PowerShell also terminated so only the independent
   watchdog remains;
9. confirm OMEN Gaming Hub undervolt is unchanged.

Only after that gate should representative-load and adaptive-policy work rely on
Custom authority for unattended operation.
