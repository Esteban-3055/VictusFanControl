# P17 — curve editor and custom profiles

Part 2 implements an editor in the existing WinForms app. Open Fan Control,
then **Editar curvas y perfiles…**. Manual remains promoted on exact HP 8C40;
Automatic remains closed. This commit performs no physical qualification.

The chart has draggable points joined by straight lines, numeric point fields,
a point list, add/remove, undo/redo and input-axis selection. Temperature (C),
power (W) and load (%) each have separate CPU/GPU curves; Y is the equal
requested fan level 10..50, not a percent or calibrated RPM. Inputs are integral,
strictly increasing, with nondecreasing levels and 2..64 points per curve.
Dragging commits one undo operation on release/capture loss. Interpolation and
endpoint clamping use the policy engine's exact implementation.

**Silencio**, **Equilibrado**, **Performance** are always available. Equilibrado
is exactly Candidate V1. Recorded proposed GPU power and CPU/GPU load points
are used for Silencio and Performance; unrecovered historical CPU/GPU thermal
and CPU power tables deliberately retain Equilibrado. These are provisional
unqualified presets, not claims of optimal noise or thermal performance.
Common smoothing remains up 4/down 1, decrease confirmation 5, deadband 1 and
maximum gap 3 seconds. Default presets cannot be renamed/deleted/overwritten;
editing then saving creates a named custom copy. New/duplicate/rename/delete,
reset, Save/Save As and explicit Apply to preview are supported. Reset custom
restores Equilibrado curves while retaining its name/ID. Ctrl+Z/Y/S work.

Select or drag only changes a draft. **Aplicar a vista previa** validates and
constructs a new read-only evaluator completely before swapping; it never calls
the production controller. Saving does not apply, applying does not save, and
opening/restarting does not restore control or an active Automatic profile.
Closing/reopening the editor preserves the currently applied in-process preview.
The main surface also shows static Equilibrado reference points, labelled as such.

The active preview shows all six demands, the dominant source, MAX raw demand
and the existing smoothed equal level. Green marks the current selected input
on the draft chart. A violet CPU marker shows the median of five fresh healthy
samples for visual comparison only. The motor still uses CpuControlTemperatureC
(max package/core context); the marker does not change policy inputs or SafetyGate.
Both history and markers reset on invalid/stale/non-Healthy telemetry. Whether to
promote median filtering into Automatic's policy is a separate P18 decision,
which must not soften instantaneous safety temperatures.

Custom JSON files live in LOCALAPPDATA/VictusFanControl/profiles. IDs are GUIDs,
name length is limited, JSON rejects unknown members, exact target/shadow purpose
is mandatory and authorizedForProduction must be false. Input ranges are capped
at CPU temperature 110, GPU temperature 100, CPU power 150, GPU power 200 and
load 100. The smoothing contract is fixed. Writes use same-directory temporary
files, flush and atomic replacement. Invalid/oversized files are skipped with a
visible count; builtins survive. Profile data cannot contain authority/mode gates.
These input domains are editor bounds, not new physical limits or calibration.

Validation: ten pure profile/persistence case groups and two real WinForms
paint/drag/numeric/undo/apply/resize groups run on Windows CI. The UI test branch
executes before single-instance, log or MainForm/hardware initialization. CI
retains a real editor render as artifact **curve-editor-ui**. P17 static checks
reject production/hardware dependencies in editor/chart/profile code and
preserve P16C Manual plus Automatic=false. Existing full regressions still run.

Next: P18 must transactionally connect the selected validated profile to the
production automatic engine while in Firmware, handle policy changes/reset and
qualify sustained telemetry, workload response and restoration on the actual
8C40. Prepare a bounded watchdog-backed test with exact-head CI and evidence;
only after PASS may physical Automatic be promoted. New presets require their
own physical evaluation. No direct EC RPM fallback or OmenMon is introduced.
