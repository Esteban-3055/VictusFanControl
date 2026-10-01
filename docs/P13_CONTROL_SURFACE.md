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

## Remaining P13 steps

P13.2 will wire mode selection through `AdaptiveFanProductionController` while both execution gates remain false. P13.3 will add Manual equal-level 10..50 controls behind the closed Manual gate. P13.4 will add Automatic candidate-curve preview/recommendation status behind the closed Automatic gate. P13.5 will add persistence/tray/status hardening and the final P13 software closure.

No P13 step may open a target-side hardware gate. That belongs to P15 or a later separately authorized qualification.


## P13.2 — mode selector wiring

Firmware / Manual / Automatic buttons now call only `AdaptiveFanProductionController.SetModeAsync`. The same compile-time Manual/Automatic gates remain false. The P13 surface has no `ApplyManualAsync`, `ProcessAutomaticAsync`, coordinator admission, FanCommand, WMI, EC or watchdog/backend access in this step.


## P13.3 — Manual equal-level UI and safe preference persistence

The GUI now exposes a 10..50 equal-level selector. A future authorized Manual execution routes only through `AdaptiveFanProductionController.ApplyManualAsync`; while the Manual gate is false, the click returns locally before requesting a control-order SafetyGate result.

Only the numeric preferred manual level is persisted in `%LOCALAPPDATA%\\VictusFanControl\\p13-ui-settings.json`. Mode, ownership, gate authorization and automatic-policy state are deliberately absent from the settings schema, so every launch still begins in Firmware mode.


## P13.4 — Automatic shadow preview

Live telemetry is now fed into the no-write `AdaptiveFanPolicyShadowEvaluator`. The UI displays shadow safety readiness, the candidate equal recommendation, raw demand, notional intent and explanatory detail, plus all six Candidate V1 demand curves.

This is visualization only. `ProcessAutomaticAsync` is deliberately absent from the P13 surface and `AutomaticExecutionAuthorized=false` remains unchanged. Runtime degradation resets the shadow evaluator and returns the preview to HoldFirmware.
