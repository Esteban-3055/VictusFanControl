# P16 — normal user Manual promotion

## P16A architecture preparation

P16 starts from the formally closed P15D2 baseline:

- HEAD: `c1e04963448e78a87dccc1719f11d9951f2970a7`
- CI: #1180 / run `37050355620` / SUCCESS
- P15A/P15B/P15C/P15D1/P15D2 stay closed and re-blocked.

P16 does not reopen P15.

The first P16A step adds a new qualification-only source gate,
`Hp8C40P16NormalManualQualificationGate.PhysicalExecutionAuthorized`,
and wires it only into the ordinary application's Manual authorization path.
The gate is false in P16A.

No P16 command-line startup mode, marker-root argument or alternate MainForm
qualification state machine is introduced. When a later bounded authorization
opens this dedicated gate, the normal application must still start in Firmware
and reuse the existing P13 surface, AdaptiveFanProductionController,
FanControlCoordinator, HP 8C40 backend and M4 watchdog.

The permanent user gate remains independently false:

- `Hp8C40PostM9UserControlGate.ManualExecutionAuthorized = false`
- `Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized = false`

Candidate V1 remains physically unvalidated and production-unauthorized.
Control remains disabled by default.

## Planned bounded sequence after P16A is fully prepared and closed

Firmware -> Manual -> 30/30 -> 40/40 -> 30/30 -> Firmware -> tray Exit.

The future parent harness must independently prove EC setpoints, durable
ownership generations 3 -> 5 -> 7, strong restore, journal absence, causal
watchdog RESTORE_BEGIN/RELEASE and final FF/FF. Automatic is forbidden.

P16A architecture preparation itself performs no hardware execution and opens
no physical gate. Harness, failsafe and evidence packaging are the next
software-only P16A substep and must also be closed by same-head CI before any
P16B physical authorization.

P16C permanent Manual promotion is a later separate commit after a physical
PASS has been independently audited and evidence-closed.
