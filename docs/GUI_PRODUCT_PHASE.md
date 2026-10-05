# Definitive GUI product phase — AC / Battery

Baseline: `bfd184e4889e6cf0b0760a48052c91f00bf441d4`, branch
`feature/victus-8c40-automatic-final-qualification`. Same-HEAD build, cpu-rapl and
wmi-fan-experiment checks were successful before implementation. Block1 physical
ZIP SHA256 `8008dd0ae890811a2a8a01bb5fec6d82772862afb60e64a0a31f9d465d25ebe9`
passed handoff/rearm/tray cleanup on that baseline. That proof concerns the old
qualification entry, not physical validation of the new normal GUI.

Blocks 2 and 3 are paused by the user's definitive-GUI instructions.

## Reference audit and surface map

All nine 1672×941 reference images were inspected. They show a shared navy shell,
left navigation, cyan selection, rounded cards, green temperature/CPU indicators,
blue GPU/load/RPM, yellow power and red/blue CPU/GPU charts.

| Reference | Product surface | Backend-correct adaptation |
|---|---|---|
| 1 Home | Four authority/source cards, temperature/load, dual RPM | Actual state; no fabricated watts or ownership claim |
| 2 Fan control | Firmware / Manual / Automatic cards, fixed-level slider, curve preview | Three real modes; no duplicate Custom mode; WMI 30–50 |
| 3 Fan telemetry | Temperatures, load/power and RPM cards | Missing/stale readings show unavailable; no invented Windows power-plan name |
| 4 CPU RAPL | PL1/PL2 sliders, selection and application/status card | CPU 8–44 W / 8–114 W, PL2 >= PL1; two source profiles |
| 5 Profiles | Exactly AC and Battery, independent details and navigation | No create/duplicate/delete of general presets |
| 6 Curves | Node graph, table, axes, add/remove/reset/save | Six demand axes retained; integer monotonic nodes, backend envelope |
| 7 Safety | Read-only backend protection rows | No switches to disable protections or invented PCH/automatic recovery rules |
| 8 Monitoring | Real five-minute histories, telemetry sidebar | No simulated series; missing/gap epochs break lines |
| 9 Settings | Startup/minimize, preference save, logs and domain states | No inactive theme/language/log-level selectors |

## Old control audit

| Old component | Classification / disposition |
|---|---|
| MainForm / P15/P16/Automatic/Block1 markers and explicit launch flags | Qualification-only. Retained for historical reproducibility; absent from normal GUI |
| P13FanControlSurface | Modes and authority are necessary; new product canvas/service bindings replace normal presentation |
| DashboardShell / DashboardTheme | Replaced for normal use; retained by qualification and regression fixtures |
| FanSettingsPanel | Old numeric/filter fields and preset list removed from normal surface. Tuning remains in configuration/backend |
| AdaptiveCurveEditorForm / legacy preset management | Replaced by the inline two-profile node editor; historical fixture remains isolated |
| PerformanceControlSurface | Replaced by profile CPU/GPU sliders and domain status cards; existing IPC client reused |
| TelemetryHistoryChart / diagnostics widgets | Replaced by red/blue five-minute histories and concise state cards |
| WindowsStartupRegistration | Reused; minimization is now an explicit task-argument preference |
| TelemetryWorker, WMI Guardian, performance domains/journals, SafetyGate | Reused; not replaced or deleted |

Remaining old GUI classes are still referenced by explicit qualification paths
and tests; they are not dead backend code. No qualification controls are copied
into the normal product canvas.

## Architecture and interaction

`ProductProfiles` is configuration only. `ProductForm` owns an editable draft and
`ProductCanvas` paints reusable cards, icons, buttons, sliders and graphs.
`IProductRuntime` separates presentation from real hardware/session state;
`ProductRuntime` adapts the existing controllers, TelemetryWorker and Guardian IPC.
Hardware operations run outside the UI thread. Form gestures emit semantic
commands; slider/curve/navigation/profile events only mutate drafts.

Normal startup uses ProductForm. Explicit hardware qualification flags still
select the historical MainForm. A separate `--product-gui-self-test` entry uses
recording ports and never constructs hardware readers.

AC/Battery editing is independent of Windows' real source and applied state.
Applied CPU/GPU values use the source reported by Guardian IPC, rather than
assuming a new Windows source has already been applied.
Preferences persist atomically in `product-profiles.json`; no mode, authority,
Guardian nonce, source observation or lease is stored. Missing files import
legacy fan/CPU preferences into deep independent AC/Battery copies. Invalid,
unknown-schema and corrupt files remain intact and fall back to safe defaults.

The fan demand engine retains all six temperature/power/load curves, EMA,
confirmation, adaptive descent and raw thermal safety. The editor clamps nodes
into the real WMI range, keeps strict input order and monotonic outputs, and
validates each change. Keyboard arrows adjust the selected node and focused sliders. No curve edit
can alter execution gates or hardware ownership.

CPU and GPU remain separate Guardian domains. Partial status is shown, including
ActiveUnverified for GPU. Configuration is not an atomic fan/performance
transaction. Apply checks configuration before starting a session; changing an
existing session requires Release first. Release calls the existing shutdown
contract and never deletes recovery journals.

## Execution boundaries

Normal Automatic remains CLOSED. Its new GUI/bindings are complete but cannot
select or grant execution. Manual remains subject to the existing exact-target
gate, fresh safety and real backend admission.

GPU preferences use fixed minimum 210 MHz and conservative maxima from the
existing qualified presets: AC <=1850, Battery <=1200. Sliders can edit/store
values within those envelopes. New custom clock targets have an explicit CLOSED
execution gate; only the previously qualified 1850/1200 presets can execute now.
The serialized IPC configuration gained backward-compatible default fields;
old sessions/qualification launchers retain exactly their existing presets.
This phase does not claim physical validation of arbitrary lower ranges.

The GUI registers display and suspend notifications, closes admission before
lifecycle restore, pauses telemetry and defers maintenance resumes while display
is Off. Resume revalidates telemetry; normal fan admission remains closed after
an interruption, preserving the existing Modern Standby fail-closed policy.
Automatic fan re-entry and source-driven curve switching remain qualification
work for later blocks. Performance Guardian retains its existing physically
qualified display/source lifecycle semantics.

Tray close hides the window; tray Exit attempts fan, performance and telemetry
cleanup independently and reports exit failure if any cleanup fails. No Guardian
is killed and no lease/journal is removed to bypass recovery. Startup only starts
the application; it never selects a fan mode or enables performance domains.

## Verification and differences

Core profile tests cover AC/Battery isolation, strict schema/unknown-field
rejection, monotonic curve editing, persistence/corruption, CPU ordering and
closed custom-GPU execution. Existing adaptive, SafetyGate, WMI, Guardian and
performance tests remain in Windows CI. Product GUI fixtures exercise drafts,
sliders/nodes, navigation without hardware, closed Automatic, real Windows
rendering of all pages/tabs, Unsupported/Recovering/Failed and partial CPU-active /
GPU-failed states, plus exact 1040–3344 pixel surfaces. Explicit Manual/Apply/
Firmware/Release gestures dispatch separate recording ports. The render fixture
uses a detached-size canvas to avoid Windows CI desktop window-size clamping.

CI PNGs are isolated recording fixtures, not evidence of physical hardware state.
The canvas uses DPI-aware reference coordinates and re-renders text/vectors at
actual output scale. Actual multi-monitor DPI transitions and physical tray/
suspend/application behavior on the Victus still require a Windows user run.

Differences retained deliberately: backend-correct ranges/units; two profiles;
three control modes; read-only safety; no unsupported PCH or power-plan data;
no theme/language switches without implementation. Icons are deterministic vector
icons rather than copied generated artwork. Surface gradients and chart styles
approximate the reference rendering; fidelity is reviewed against actual Windows
captures, not claimed pixel-identical to generated images.

## Follow-up: interaction and lifecycle hardening

Continuation from `12d5c51b81047d3404838d4d2eabd9e471c02535` keeps Blocks 2/3
paused and all normal hardware gates unchanged.

- Closing during asynchronous runtime construction waits for the returned service
  and disposes it without starting telemetry or registering notifications.
- Display-Off and suspend fence immediately. Releases are queued, so resume and
  tray Exit wait for every pending boundary rather than only the newest task.
- The normal product presentation requires a live Guardian process and a response
  less than six seconds old. Expired or future-dated status is not presented as
  Active; a retained session is Recovering until a fresh response arrives. Failed
  or unconfirmed domains say "Sin confirmación actual"; only Disabled says
  "Sin límite aplicado", so loss of IPC never implies a confirmed hardware reset.
- Apply handlers recheck admission and busy state. Firmware remains available while
  another command is pending. These checks supplement the backend protections.
- Tab/Shift+Tab, slider arrows and accessible slider values use the same draft-only
  edit path. Retained accessible controls resolve against the live page; unavailable
  controls cannot dispatch an action. Keyboard focus follows a control ID across
  redraws instead of accidentally selecting another page's control by list index.
  Ordinary typing no longer edits curve nodes.
- Failed/Faulted states use red, Applying/Recovering use yellow, and disabled or
  unsupported domains use muted text; CPU/GPU colors remain for active domains.

The Windows product GUI test now includes delayed-construction Exit, queued
lifecycle boundaries, display-Off maintenance resume, busy Apply/Firmware,
keyboard and accessibility, freshness boundaries and status colors. All use
recording ports; they do not qualify physical suspend, fan response or recovery.
Actual hardware and multi-monitor DPI validation remain required.

## Software completion without target-side execution

The curve table now has aligned columns/grid, previous/next selection, and
Home/End/PageUp/PageDown access to all 64 nodes. The graph identifies its editable
CPU/GPU series and dashed peer reference; a yellow ring marks the selected node.
Units and the WMI 30–50 envelope deliberately differ from percentage-based images.
Settings/Profiles offer explicit discard to the latest successfully saved draft.
Saving flushes the new file before atomic replacement. Unsupported schema, duplicate
JSON keys, null curves/points and invalid values fall back without replacing the
original. Save failures retain dirty state and clean the temporary file.

History uses the current clock, rather than pinning the last received sample to
"now". Five-minute series drop expired samples and break at missing/nonfinite
readings or gaps over three seconds. Future samples cannot erase retained history.
A one-second presentation timer updates data age even with no runtime event.
Healthy runtime with stale/no snapshot no longer displays "Telemetría validada".
Accepted fan levels are shown only while Custom authority is present.

The form drains admitted UI commands before runtime disposal on Exit; no new draft
edits are admitted while closing. The surface also checks the existing performance
configuration execution contract before dispatch, so custom GPU values fail before
the runtime port. Apply uses the current draft, independently of disk persistence.

Expanded Windows fixtures exercise both profiles and all six axes, 64-node limits,
keyboard paging, save/discard/save failures, startup failure, Guardian no-response
Apply/Release, lost/missing/expired telemetry, invalid/gapped/future history and Exit
while a command is pending. These add to the original lifecycle, accessibility,
partial-state and size fixtures. Every hardware effect is a recording port.

CI packages the already verified P14 payload into `product-gui-review-<HEAD>` with
current documentation, a SHA256 manifest and a Verify/SelfTest/Open launcher. The
packaged executable runs the same isolated GUI tests in CI. Historical P14 metadata
and artifact attestation remain unchanged. The new artifact describes physical GUI
validation as pending and cannot promote a gate. See
[the grouped Victus validation route](GUI_VICTUS_VALIDATION.md).

Physical Apply/Release, AC/Battery transitions, lifecycle, tray exit and real DPI
remain required. No physical PASS is asserted by software tests or PNGs. Blocks 2/3
remain paused; normal Automatic and custom GPU clocks remain closed.
