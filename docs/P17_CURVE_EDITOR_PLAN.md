# Part 2 — editable Automatic curves

Implemented in the subsequent P17 editor change; see P17_CURVE_EDITOR.md for
actual behavior, preset provenance, scope and remaining P18 work.
Use the existing WinForms app and policy interpolation; no additional chart
framework or native hardware access belongs in the editor.

- Names: Silencio, Equilibrado, Performance, plus user-created profiles.
- Six demand curves: CPU/GPU temperature (C), power (W), load (%); Y is the
  equal requested fan level, 10..50. A level is not a calibrated RPM or percent.
- Points joined by straight lines, drag and numeric editing, increasing X,
  nondecreasing level, at least two points, snapping to integral X/level.
- CPU temperature uses CpuControlTemperatureC and the existing temporal
  filtering; show filtered and instantaneous markers separately. SafetyGate
  continues to use real unsmoothed safety temperatures.
- Six demands combine by MAX, then the existing policy rounding/limits/smoothing.
  CPU/GPU receive one equal level. Editing a point only changes a draft.
- New, duplicate, rename, delete custom, reset defaults, save, explicit Apply,
  undo/redo. Persist profile content in LOCALAPPDATA/VictusFanControl/profiles;
  never persist operating mode, authority or hardware authorization.
- Equilibrado starts from Candidate V1; proposed Silencio/Performance remain
  unqualified profiles. Retrieve the full recorded preset tables before coding;
  do not guess missing temperature/power points or claim preset physical PASS.
- Common initial smoothing isolates curve differences: up 4, down 1,
  5 samples, deadband 1 and maximum gap 3 seconds, subject to matching the
  existing configuration's units and semantics.
- Expose each demand, RawDemandLevel, SmoothedEqualFanLevel and DominantSource.
- Saving/applying profile content does not authorize physical Automatic.
  Automatic remains separately gated until its own qualification is complete.

Implement and validate the editor/persistence in part 2. Preserve the P16C
Manual promotion, production backend and existing safety thresholds.
