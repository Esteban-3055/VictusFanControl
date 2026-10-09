# Definitive GUI product phase — AC / Battery

**Historical development record.** Current v1.0.0 operation, default curve and guarded Windows startup are described in [PRODUCT_V1.md](PRODUCT_V1.md); evidence limits remain in [PRODUCT_VALIDATION_STATUS.md](PRODUCT_VALIDATION_STATUS.md). Qualification gates and closed-control statements below refer to their original development stages.

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
| 2 Fan control | Firmware / Manual / Automatic cards, fixed-level slider, curve preview | Three real modes; no duplicate Custom mode; Manual WMI 10–50, prepared Automatic 30–50 |
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
Units deliberately differ from percentage-based images; Manual WMI uses levels 10–50.
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

Pointer fixtures also exercise actual mouse-down/move/up dispatch for sliders and
curve nodes at the scaled window size. Captured drags are canceled when page, axis,
profile or busy state changes, preventing edits from leaking into another draft.
Graph labels occupy separate rows, and vector fan/settings icons more closely
follow the reference shapes. These cosmetic changes grant no hardware admission.

The IPC client also retains a Guardian process that exits with an error during
Enable, so the product reports an unresolved session rather than Disabled and
cannot immediately replace it. The existing Close contract rejects nonzero exit.
A native IPC-client fixture starts an already-exited error process and checks both
Enable and Close; it never launches a hardware owner or kills a live Guardian.

Imported or fallback preferences are marked as needing Save; Discard returns to the
session baseline and cannot label an unpersisted fallback as saved. The corrupt-file
form fixture checks that the original survives Load/Discard and is replaced only
by an explicit successful Save.

## Offline simulator, portable profiles, diagnostics and prolonged GUI checks

Curves has Editor / Simulator views. The simulator uses the same
`AdaptiveFanInertiaPolicy` + `Hp8C40AutomaticPolicy.Create` and the edited profile's
fan tuning/configuration. Its six sliders are synthetic inputs; CPU temperature is
already the selected demand aggregate. It does not invent individual core readings
or raw-safety telemetry. Advance 1/60/1200 virtual seconds processes every one-second
sample, preserving EMA, confirmation, thermal demand override and sustained-load
history. Results show raw demand, filtered demand and the editable common level 10–50; only the
latest 600 samples are retained. A session is bounded to 24 virtual hours. Profile
or configuration changes reset the model; input changes retain temporal history.
The simulation has no hardware/service port and never mutates preferences. It is
not a thermal plant model, an RPM prediction or a test of SafetyGate/Guardian.

Settings can export both profiles as strict JSON and import into the draft.
Import size is limited to 1 MiB; malformed/schema/authority injection is rejected
without changing the draft. Import marks unsaved preferences, never writes the live
profile file or applies hardware. Export does not change dirty/applied state.

The diagnostic ZIP exports a presentation snapshot, draft configuration and at most
2 MiB from the current app event log, plus an explanatory README. It never enumerates
or removes recovery journals/leases and performs no process/hardware operations.
Logs can contain local paths; the UI tells users to review before sharing. Export
uses a temporary ZIP and atomic replacement, preserving the destination on failure.
The snapshot expressly states that physical qualification is not established.

The Windows soak fixture uses recording ports, three warmup + thirty open/close
cycles and 924 renders with page/profile/size/keyboard/simulation changes. It records
GDI, USER and private-memory samples in `logs/product-gui-soak/report.json`. After
warmup/full GC, limits are baseline +16 GDI/USER objects and +64 MiB private bytes,
so bounded framework caches are tolerated while repeated leaks fail CI. No live
hardware runtime is constructed. This is an accelerated resource/interaction test,
not evidence of days of physical operation. CI publishes the report separately.
`Start-ProductGui.ps1 -Mode Soak` runs the same isolated fixture from the package.

Core regression compares virtual phases with the prepared inertia policy; GUI
fixtures check simulator authority isolation/reset, portable-file failure handling,
draft-only import, bounded diagnostic content and preservation of saved preferences.
Automatic normal and custom GPU gates remain closed; Blocks 2/3 stay paused.

## Physical startup diagnostic and native notification regression (2026-10-05)

The first product-GUI startup on the exact 8C40/F.18 target failed before telemetry
started. The supplied diagnostic recorded `EntryPointNotFoundException` for
`RegisterSuspendResumeNotification` in `powrprof.dll`; Firmware authority and no
Performance session were reported. This is a failed startup observation, not a
physical GUI PASS. Both suspend/resume bindings belong to `user32.dll` and are now
corrected. Registration failure cleans up partial registrations and captures the
Win32 error immediately. Startup exceptions remain in the presentation/diagnostic
state, even if a later update or export changes the transient notice.

The Windows GUI self-test now uses the production registration/unregistration
path on real window handles for three cycles with recording runtime ports. It
also injects a startup exception and verifies its exported diagnostic. No hardware
runtime is constructed by these fixtures. Repeat the real startup/telemetry step
with the corrected package before continuing hardware-control tests.

The next real startup diagnostic confirmed Healthy telemetry with all fourteen
cores, complete CPU/GPU/fan readings, and Firmware authority without a Performance
session. A later diagnostic also recorded four Manual attempts: each entered
read-only Custom preparation, rejected a stale SafetyGate refresh, and returned to
reported Firmware authority through accepted release requests (physical ownership
remains unverified). These are failed Manual attempts, not a Manual PASS.
The product adapter incorrectly supplied a presentation evaluation (sequence zero)
as its post-admission control refresh. Its control-only safety helper now always
allocates a control evaluation, including every bounded Manual retry, preserving
thermal confirmation and the existing sequence/freshness guards. Regression covers
sequence-zero refusal without fan dispatch, a fresh-control retry that writes once,
unchanged-target hold, and Firmware release. Repeat Manual qualification only after
the pending draft/profile GUI checks; no execution gate is promoted by this fix.


## Live offline simulator and editable 10–50 range (2026-10-05)

The physical GUI review confirmed AC/Battery editing isolation and discard,
then diagnostic(3) confirmed saved CPU drafts AC 18/36 W and Battery 9/18 W
survived reopening build 472142c with healthy telemetry, Firmware, disabled
CPU/GPU and no Guardian session. The user also confirmed importing the original
backup, saving and reopening restored AC 20/40 W and Battery 8/15 W.

The offline simulator now advances one virtual second per visible presentation
tick by default. Pausar/Reanudar controls playback; +60 s and +20 min remain
available for accelerated inspection. Input edits feed the next tick without
resetting filter/load history. Leaving Curvas, selecting Editor gráfico,
hiding/minimizing the window or closing it prevents automatic advancement;
returning does not catch up hidden time. Reset replaces only simulation state.
Simulation errors pause playback. The timer never dispatches a runtime command.

The curve editor, keyboard/pointer mapping, graph axes, storage and simulation
now support 10–50. Legacy product files with tuning minimum 30 load with an
editor minimum of 10 while retaining every stored curve point and independent
performance preference; loading does not rewrite the file. Previously clamped
points are not reconstructed or silently replaced. New defaults retain the
original candidate curve points instead of clamping them to 30.

This is an editable demand model, not proof of hardware control. The earlier
8C40 qualification already characterized every equal level 10–50 and passed
production endpoint/restart/large-transition tests (see HARDWARE_8C40.md).
The WMI-only migration had independently narrowed Manual to 30–50. That
integration restriction is corrected below; the historical characterization
is retained rather than requesting another full sweep. Prepared Automatic
actuation retains 30–50 and its execution gate remains closed. No direct EC
path is introduced.

Regression coverage checks level-10 persistence and simulation, legacy range
migration without point loss, unchanged prepared Automatic minimum, live
playback/pause/input changes/hidden-page behavior/reset, and zero hardware
commands from simulator interactions. Windows CI validates native GUI behavior;
physical live-simulator acceptance remains pending on the new package.


## Restore the qualified Manual 10–50 range through WMI-only GUI

The exact target HP 8C40 / 63.43 / 9D0R1LA / F.18 already passed the full
10–50 equal-level characterization, level-10 restart from stopped fans, large
10/30/50 transitions and production endpoint regression documented in
HARDWARE_8C40.md. Those tests used HP WMI setters with EC acknowledgement and
dual tachometers. They establish the physical command range; they do not turn
the current WMI-only request acceptance into independent ownership proof.

Manual slider/edit clamping and backend capabilities now advertise 10–50.
The GUI backend's command session explicitly selects minimum 10, including
rearm after a clean release. The same session class keeps minimum 30 by default
for the supervised Automatic experiment. The final native whitelist admits
10–29 only in the GUI boundary; shadow, stop and recovery still prohibit normal
commands, and asymmetric/out-of-range/malformed requests remain rejected.
Guardian intent persistence also rejects levels outside 10–50 before writing.

Changing the slider, opening Manual or loading preferences does not dispatch a
fan setter; only explicit Apply does. Startup remains Firmware. SafetyGate,
thermal thresholds, fresh RPM, durable intent before dispatch, guardian identity,
no duplicate setter, native serialization, release/default and unknown-completion
fences are unchanged. The legacy 88F8 target and Automatic gate are untouched.

Deterministic regressions exercise GUI endpoint editing and explicit Apply(10),
backend 10/29/50 payloads, outside-range/asymmetric refusal before intent, low-end
rearm, failed low-end intent, guardian loss, and GUI-versus-experiment whitelist
isolation. The real detached guardian fixture persists level-10 intent and
completes its existing release/lease-retirement flow without hardware IO.

Current product GUI hardware acceptance remains pending. The next operator
check is a brief cool/light-load Manual 30 -> 10 -> Firmware integration test,
with diagnostics before/after release. It is not a repeat of the historical
full sweep and does not authorize Automatic or prove independent firmware
ownership from WMI/RPM alone.

CI validation encountered the previously observed hosted-runner Win32_Service
initialization timeout before the GM26 non-target rejection fixture. The build
workflow now initializes that same read-only inventory query once with a bounded
30-second timeout before the test suite. Real isolation guards retain their
five-second deadline and every original assertion. This changes runner setup,
not HP requests, production admission or release behavior.


### Entrada explícita de revisión Automatic

La GUI incluye `--product-automatic-review`, expuesto únicamente por el modo
`AutomaticReview` del launcher verificado. Usa la autorización dedicada de
cualificación para 8C40/F.18, arranque Firmware, copia de curva por fuente real,
3 muestras Healthy, límites físicos conservadores y una ventana de 300 s.
`Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized` permanece false.
El runtime usa evaluaciones SafetyGate de control con secuencia positiva; las
previsualizaciones del despacho mantienen la evaluación de presentación sin
consumir secuencias. El diagnóstico captura tanto borrador como curva aplicada.
La aceptación física de esta entrada y la cualificación general siguen pendientes.


## Actualización: presets silenciosos y limitadores ajustables

La revisión producto admite 10–50 explícitamente, defaults silenciosos AC/Batería
con seis ejes y sin piso oculto 30. Se conserva el gate normal Automatic cerrado,
inicio Firmware, ventana de cinco minutos y bloqueo ante interrupciones reales.
La devolución voluntaria a Firmware distingue la denegación WMI prevista del
fallo de sensores; el motivo de un bloqueo queda visible y en el diagnóstico.
CPU/GPU usan una cola independiente y pueden aplicarse con Automatic activo.
Los máximos GPU son configurables entre 210 y 2500 MHz en ambos perfiles,
con defaults 1850 AC y 1200 Batería; GPU continúa ActiveUnverified y NVML puede rechazar valores.
Las preferencias existentes no se sobrescriben; usar Valores iniciales del perfil
para instalar el preset nuevo conservando PL1/PL2/GPU. La derivación, tablas,
replay y límites de evidencia están en [QUIET_PRODUCT_PRESETS.md](QUIET_PRODUCT_PRESETS.md).

## Activación conjunta de Automatic

Automatic ahora habilita CPU/GPU con el perfil congelado antes de iniciar fans.
El estado vigente de ambos dominios es una condición continua del control.
No cambia el gate normal, el inicio Firmware ni la revisión de cinco minutos.
La matriz completa de aplicación, cancelación y casos sin rearmado está en
[AUTOMATIC_PRODUCT_SESSION.md](AUTOMATIC_PRODUCT_SESSION.md).
