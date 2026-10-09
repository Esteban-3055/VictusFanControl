# P13 — user-facing control surface

P13 is developed incrementally. It does not authorize hardware execution.

## P13.1 — presentation-only mode surface

Implemented in the normal WinForms GUI:

- the former Fan Curve placeholder is replaced by a Fan Control tab;
- Firmware, Manual and Automatic are visible as the intended operating modes;
- Firmware is shown as the startup/requested mode;
- Manual and Automatic are visibly locked;
- the HP 8C40 candidate curve ID is shown as shadow-only / unvalidated;
- the compile-time post-M9 user-control gate exposes both Manual and Automatic execution as false;
- this step intentionally installs no P13 mode click handlers and calls neither `ApplyManualAsync` nor `ProcessAutomaticAsync`.

Opening the GUI therefore does not acquire Custom authority through P13.

## P13 step progression

P13.2 through P13.5 are now complete and documented below. No P13 step opened a target-side hardware gate; Manual and Automatic execution remain closed. Target-side execution belongs to P15 or a later separately authorized qualification.


## P13.2 — mode selector wiring

Firmware / Manual / Automatic buttons now call only `AdaptiveFanProductionController.SetModeAsync`. The same compile-time Manual/Automatic gates remain false. The P13 surface has no `ApplyManualAsync`, `ProcessAutomaticAsync`, coordinator admission, FanCommand, WMI, EC or watchdog/backend access in this step.


## P13.3 — Manual equal-level UI and safe preference persistence

The GUI now exposes a 10..50 equal-level selector. A future authorized Manual execution routes only through `AdaptiveFanProductionController.ApplyManualAsync`; while the Manual gate is false, the click returns locally before requesting a control-order SafetyGate result.

Only the numeric preferred manual level is persisted in `%LOCALAPPDATA%\\VictusFanControl\\p13-ui-settings.json`. Mode, ownership, gate authorization and automatic-policy state are deliberately absent from the settings schema, so every launch still begins in Firmware mode.


## P13.4 — Automatic shadow preview

Live telemetry is now fed into the no-write `AdaptiveFanPolicyShadowEvaluator`. The UI displays shadow safety readiness, the candidate equal recommendation, raw demand, notional intent and explanatory detail, plus all six Candidate V1 demand curves.

This is visualization only. `ProcessAutomaticAsync` is deliberately absent from the P13 surface and `AutomaticExecutionAuthorized=false` remains unchanged. Runtime degradation resets the shadow evaluator and returns the preview to HoldFirmware.


## P13.5 — tray/status hardening and software completion

The tray now exposes requested mode, adaptive shadow recommendation and Manual/Automatic gate state in addition to runtime, CPU/GPU and fan authority. The P13 surface keeps the last real `FanAuthority` delivered by the coordinator so a Firmware mode request cannot cosmetically overwrite a Faulted/Restoring authority state.

### P13 software completion boundary

P13 is considered software-complete only while all of the following stay true:

- startup mode is Firmware;
- `control.enabledByDefault=false`;
- `automaticPolicyEnabled=false`;
- `ManualExecutionAuthorized=false`;
- `AutomaticExecutionAuthorized=false`;
- Candidate V1 remains physically unvalidated and production-unauthorized;
- the Manual UI reaches hardware only through `AdaptiveFanProductionController` after its closed gate;
- Automatic in P13 is shadow/recommendation only and has no `ProcessAutomaticAsync` execution path;
- persisted settings contain only the equal manual preference 10..50;
- the UI contains no direct WMI, EC, PawnIO, watchdog lease or backend access.

Opening hardware execution is not part of P13 and remains a later target-side qualification.


## Formal P13 software closure

Source implementation HEAD: `4f48d1ba68cccfc931f116793672d70efeb8ba52`.

Full GitHub Actions validation: **#1069**, run ID `36892688980`, **SUCCESS**.

This closes P13 as a software/UI milestone only. It does not authorize Manual or Automatic fan execution, does not validate Candidate V1 physically, and does not change the P15 hardware-validation boundary.
