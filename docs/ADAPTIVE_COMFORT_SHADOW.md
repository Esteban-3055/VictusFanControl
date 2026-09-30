# Adaptive comfort controls - shadow preparation

Target: `HP-8C40-9D0R1LA-F18`.

Status: **SHADOW ONLY / SIMULATION ONLY. NOT PRODUCTION-INTEGRATED.**

M8B remains open and M8C physical execution remains blocked. This work does not change
`WatchdogRecoveryValidated=false`, does not enable automatic/adaptive fan control and does not
authorize a hardware test.

## 1. CPU comfort signal

The normal temperature curve may consume a temporal **median of five** fresh, unique CPU
snapshots. Each snapshot first computes the already-established effective CPU temperature:

`T_effective = max(CPU Package, hottest reported physical-core context)`.

The five values are combined in time. Core sensors are **not** averaged spatially. The existing
`CpuCoreAverageTemperatureC` diagnostic value is not an input to this filter.

`CpuTemperatureComfortFilter` also exposes the instantaneous effective maximum separately and
estimates a least-squares temperature trend over the current temporal window. A duplicate,
out-of-order, stale or discontinuous epoch cannot manufacture a five-sample ready signal.

**SafetyGate still evaluates raw telemetry** before any comfort filtering. The existing raw
thermal thresholds, exact-target checks, freshness rules and HP 8C40 temporal emergency
confirmation are not relaxed or delayed by the comfort path.

## 2. Rising anticipation and slower falling response

The pure adaptive engine retains independent upward/downward slew, decrease confirmation and
deadband. An optional `CpuTemperatureTrendCurve` domain is now available to shadow
configuration so a rising temperature trend can raise notional demand before the filtered
temperature catches up. Existing CPU/GPU power domains remain available as feed-forward load
signals.

A trend curve is optional for backward-compatible shadow configurations. No production trend
curve or production tuning values are defined by this preparation.

## 3. Timed post-cooling only after heavy load

`TimedPostCoolingShadowStateMachine` is a hardware-independent state machine with three states:
Idle, HeavyLoad and PostCooling.

PostCooling can begin only after a temporally qualified heavy-load episode ends. It has minimum
and maximum timing bounds plus a filtered-temperature/trend exit condition. Returning to heavy
load cancels PostCooling immediately, and lifecycle/readiness loss resets the state.

There are **no express light-load cooling pulses**. Brief or ordinary light activity that was not
preceded by a qualified heavy-load episode cannot enter PostCooling.

## 4. Graphical curve editor

The explicit GUI mode `--adaptive-curve-editor-shadow` opens a hardware-free WinForms editor.
It is intentionally launched before PawnIO module resolution and never constructs the production
MainForm.

The editor provides:

- X axis = CPU temperature;
- Y axis = equal CPU/GPU fan level;
- draggable monotonic points joined by piecewise-linear segments;
- a moving blue point for the simulated filtered CPU temperature;
- a separate dashed indicator for the simulated instantaneous raw maximum;
- a text export of the edited points.

The current moving values are synthetic. The editor does not open PawnIO, WMI, the HP backend,
the watchdog or `FanControlCoordinator`; it cannot acquire Custom authority.

## 5. Verification boundary

The existing `--adaptive-policy-self-test` route includes deterministic comfort-component tests.
Static invariants keep the filter, post-cooling machine, editor model and visual editor free of
hardware-control dependencies and keep production profile flags false.

The separate retrospective M8B audit script is read-only. It can validate the preserved attempt-4
READY/result/summary, the dated M4 service log and the independent failsafe log once those raw
files are available. It does not modify the journal, service state or fan hardware.

No physical test is authorized by this document.
