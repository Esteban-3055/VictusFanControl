using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

/// <summary>
/// P13 user-facing mode selector.
///
/// This control can request a logical mode only through
/// AdaptiveFanProductionController. With the post-M9 Manual/Automatic
/// execution gates closed, those requests return Blocked before the
/// coordinator/backend is touched.
/// </summary>
internal sealed class P13FanControlSurface : UserControl
{
    private readonly AdaptiveFanProductionController _controller;
    private readonly AdaptiveFanPolicyShadowEvaluator _shadowEvaluator;
    private readonly AdaptiveFanPolicyConfig _candidateConfig;
    private readonly Func<SafetyGateResult?> _controlSafetyProvider;
    private readonly Action<string> _log;
    private readonly Func<bool>? _manualInteractionReadyProvider;
    private readonly Func<P13ControlInteractionKind, AdaptiveFanProductionMode?, int?, bool>? _interactionAuthorizationProvider;
    private readonly int? _fixedManualQualificationLevel;
    private readonly Action<P13ControlInteractionObservation>? _interactionObserver;
    private int _controlInteractionInFlight;
    private FanAuthority _lastAuthority = FanAuthority.Firmware;

    private readonly Label _modeValue = ValueLabel();
    private readonly Label _authorityValue = ValueLabel();
    private readonly Label _manualGateValue = ValueLabel();
    private readonly Label _automaticGateValue = ValueLabel();
    private readonly Label _candidateValue = ValueLabel();
    private readonly Label _statusValue = new();
    private readonly NumericUpDown _manualLevel = new();
    private readonly Button _manualApply = new();
    private readonly Label _previewSafetyValue = ValueLabel();
    private readonly Label _previewLevelValue = ValueLabel();
    private readonly Label _previewRawDemandValue = ValueLabel();
    private readonly Label _previewIntentValue = ValueLabel();
    private readonly Label _previewDetailValue = new();

    public P13FanControlSurface(
        AdaptiveFanProductionController controller,
        HardwareIdentity hardware,
        string targetDescription,
        Func<SafetyGateResult?> controlSafetyProvider,
        Action<string> log,
        Func<bool>? manualInteractionReadyProvider = null,
        Func<P13ControlInteractionKind, AdaptiveFanProductionMode?, int?, bool>? interactionAuthorizationProvider = null,
        int? fixedManualQualificationLevel = null,
        Action<P13ControlInteractionObservation>? interactionObserver = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _candidateConfig =
            Hp8C40AdaptiveCandidateV1.Create();
        _shadowEvaluator =
            new AdaptiveFanPolicyShadowEvaluator(
                hardware,
                _candidateConfig);
        _controlSafetyProvider =
            controlSafetyProvider ??
            throw new ArgumentNullException(nameof(controlSafetyProvider));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _manualInteractionReadyProvider = manualInteractionReadyProvider;
        _interactionAuthorizationProvider = interactionAuthorizationProvider;
        _fixedManualQualificationLevel = fixedManualQualificationLevel;
        _interactionObserver = interactionObserver;

        Dock = DockStyle.Fill;
        AutoScroll = true;

        Controls.Add(BuildUi(targetDescription));
        RefreshState(
            "Startup mode is Firmware. Manual/Automatic availability is determined only by the explicit execution gates shown below.");
    }

    public AdaptiveFanProductionMode RequestedMode => _controller.Mode;

    public string ModeStatusText =>
        $"Mode: {_controller.Mode}";

    public string AdaptivePreviewStatusText =>
        $"Adaptive preview: {_previewLevelValue.Text}";

    public string GateStatusText =>
        $"Manual gate: {(_controller.ManualExecutionAuthorized ? "OPEN" : "CLOSED")} | " +
        $"Automatic gate: {(_controller.AutomaticExecutionAuthorized ? "OPEN" : "CLOSED")}";

    public void UpdateTelemetry(
        SystemState state,
        TelemetrySnapshot snapshot)
    {
        try
        {
            var result =
                _shadowEvaluator.Evaluate(
                    state,
                    snapshot,
                    DateTimeOffset.UtcNow);

            _previewSafetyValue.Text =
                result.SafetyPreconditionsReady
                    ? "READY (shadow)"
                    : result.EffectiveThermalEmergency
                        ? "THERMAL HANDOFF"
                        : "BLOCKED (shadow)";

            _previewLevelValue.Text =
                result.RecommendedEqualLevel.HasValue
                    ? $"{result.RecommendedEqualLevel.Value}/{result.RecommendedEqualLevel.Value}"
                    : "—";

            _previewRawDemandValue.Text =
                result.RawDemandLevel.HasValue
                    ? result.RawDemandLevel.Value.ToString("0.00")
                    : "—";

            _previewIntentValue.Text =
                result.Intent.Kind.ToString();

            _previewDetailValue.Text =
                result.Detail;
        }
        catch (Exception ex)
        {
            _shadowEvaluator.Reset();
            _previewSafetyValue.Text = "ERROR / RESET";
            _previewLevelValue.Text = "—";
            _previewRawDemandValue.Text = "—";
            _previewIntentValue.Text = "HoldFirmware";
            _previewDetailValue.Text =
                $"Shadow preview failed closed and reset: {ex.Message}";
            _log(
                $"P13 adaptive shadow preview reset after exception: {ex}");
        }
    }

    public void UpdateRuntimeState(SystemState state)
    {
        if (state == SystemState.Healthy)
        {
            return;
        }

        _shadowEvaluator.Reset();
        _previewSafetyValue.Text =
            $"BLOCKED ({state})";
        _previewLevelValue.Text = "—";
        _previewRawDemandValue.Text = "—";
        _previewIntentValue.Text = "HoldFirmware";
        _previewDetailValue.Text =
            $"Shadow preview reset because runtime state is {state}.";
    }

    public void UpdateAuthority(FanAuthority authority)
    {
        _lastAuthority = authority;
        _authorityValue.Text = authority.ToString();
    }

    private System.Windows.Forms.Control BuildUi(string targetDescription)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 7,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(
            new Label
            {
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Font = new Font(Font, FontStyle.Bold),
                Text =
                    "P13 user-control surface\r\n" +
                    targetDescription
            },
            0,
            0);

        var group = new GroupBox
        {
            Text = "Operating mode",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 12, 3, 8)
        };

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        var firmware = new Button
        {
            Text = "Firmware",
            AutoSize = true
        };
        firmware.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Firmware);

        var manual = new Button
        {
            Text = _controller.ManualExecutionAuthorized
                ? "Manual"
                : "Manual (locked)",
            AutoSize = true
        };
        manual.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Manual);

        var automatic = new Button
        {
            Text = _controller.AutomaticExecutionAuthorized
                ? "Automatic"
                : "Automatic (locked)",
            AutoSize = true
        };
        automatic.Click += async (_, _) =>
            await RequestModeAsync(
                AdaptiveFanProductionMode.Automatic);

        buttons.Controls.Add(firmware);
        buttons.Controls.Add(manual);
        buttons.Controls.Add(automatic);

        var state = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 5,
            Margin = new Padding(0, 8, 0, 0)
        };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var value in new[]
                 {
                     _modeValue,
                     _authorityValue,
                     _manualGateValue,
                     _automaticGateValue,
                     _candidateValue
                 })
        {
            value.AutoSize = true;
            value.MaximumSize = new Size(650, 0);
        }

        _manualGateValue.Text =
            _controller.ManualExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _automaticGateValue.Text =
            _controller.AutomaticExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _candidateValue.Text =
            $"{Hp8C40AdaptiveCandidateV1.Id} — shadow-only / unvalidated";

        state.Controls.Add(new Label { Text = "Requested mode:", AutoSize = true }, 0, 0);
        state.Controls.Add(_modeValue, 1, 0);
        state.Controls.Add(new Label { Text = "Fan authority:", AutoSize = true }, 0, 1);
        state.Controls.Add(_authorityValue, 1, 1);
        state.Controls.Add(new Label { Text = "Manual execution gate:", AutoSize = true }, 0, 2);
        state.Controls.Add(_manualGateValue, 1, 2);
        state.Controls.Add(new Label { Text = "Automatic execution gate:", AutoSize = true }, 0, 3);
        state.Controls.Add(_automaticGateValue, 1, 3);
        state.Controls.Add(new Label { Text = "Candidate curve:", AutoSize = true }, 0, 4);
        state.Controls.Add(_candidateValue, 1, 4);

        content.Controls.Add(buttons, 0, 0);
        content.Controls.Add(state, 0, 1);
        group.Controls.Add(content);

        var manualGroup = new GroupBox
        {
            Text = "Manual equal fan level",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 8, 3, 8)
        };

        var manualFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true
        };

        var saved =
            P13UiSettingsStore.Load();

        _manualLevel.Minimum = 10;
        _manualLevel.Maximum = 50;

        var initialManualLevel =
            _fixedManualQualificationLevel ?? saved.ManualEqualLevel;

        if (initialManualLevel < _manualLevel.Minimum ||
            initialManualLevel > _manualLevel.Maximum)
        {
            throw new InvalidOperationException(
                $"Manual qualification level {initialManualLevel} is outside the 10..50 envelope.");
        }

        _manualLevel.Value = initialManualLevel;
        _manualLevel.Enabled = !_fixedManualQualificationLevel.HasValue;
        _manualLevel.Width = 70;
        _manualLevel.ValueChanged += (_, _) =>
        {
            if (_fixedManualQualificationLevel.HasValue)
            {
                return;
            }

            try
            {
                P13UiSettingsStore.SaveManualEqualLevel(
                    decimal.ToInt32(_manualLevel.Value));
            }
            catch (Exception ex)
            {
                _log(
                    $"P13 manual-level preference save failed harmlessly: {ex.Message}");
            }
        };

        _manualApply.Text = "Apply equal CPU/GPU level";
        _manualApply.AutoSize = true;
        _manualApply.Click += async (_, _) =>
            await ApplyManualAsync();

        manualFlow.Controls.Add(new Label
        {
            Text = _fixedManualQualificationLevel.HasValue
                ? $"Qualification level {_fixedManualQualificationLevel.Value} (fixed):"
                : "Level 10..50:",
            AutoSize = true,
            Margin = new Padding(3, 7, 3, 3)
        });
        manualFlow.Controls.Add(_manualLevel);
        manualFlow.Controls.Add(_manualApply);

        manualGroup.Controls.Add(manualFlow);

        var previewGroup = new GroupBox
        {
            Text = "Automatic candidate preview — NO WRITE",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 8, 3, 8)
        };

        var previewTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 5
        };
        previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        previewTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        foreach (var value in new[]
                 {
                     _previewSafetyValue,
                     _previewLevelValue,
                     _previewRawDemandValue,
                     _previewIntentValue,
                     _previewDetailValue
                 })
        {
            value.AutoSize = true;
            value.MaximumSize = new Size(650, 0);
        }

        _previewSafetyValue.Text = "Waiting for telemetry";
        _previewLevelValue.Text = "—";
        _previewRawDemandValue.Text = "—";
        _previewIntentValue.Text = "HoldFirmware";
        _previewDetailValue.Text =
            "The candidate policy is evaluated only through the read-only shadow evaluator.";

        previewTable.Controls.Add(new Label { Text = "Shadow SafetyGate:", AutoSize = true }, 0, 0);
        previewTable.Controls.Add(_previewSafetyValue, 1, 0);
        previewTable.Controls.Add(new Label { Text = "Recommended equal level:", AutoSize = true }, 0, 1);
        previewTable.Controls.Add(_previewLevelValue, 1, 1);
        previewTable.Controls.Add(new Label { Text = "Raw demand:", AutoSize = true }, 0, 2);
        previewTable.Controls.Add(_previewRawDemandValue, 1, 2);
        previewTable.Controls.Add(new Label { Text = "Notional intent:", AutoSize = true }, 0, 3);
        previewTable.Controls.Add(_previewIntentValue, 1, 3);
        previewTable.Controls.Add(new Label { Text = "Detail:", AutoSize = true }, 0, 4);
        previewTable.Controls.Add(_previewDetailValue, 1, 4);
        previewGroup.Controls.Add(previewTable);

        var curveGroup = new GroupBox
        {
            Text = "Candidate V1 curves — shadow-only",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(12),
            Margin = new Padding(3, 8, 3, 8)
        };

        var curves = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 6
        };
        curves.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        curves.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddCurveRow(curves, 0, "CPU temperature", _candidateConfig.CpuTemperatureCurve);
        AddCurveRow(curves, 1, "GPU temperature", _candidateConfig.GpuTemperatureCurve);
        AddCurveRow(curves, 2, "CPU package power", _candidateConfig.CpuPowerCurve);
        AddCurveRow(curves, 3, "GPU power", _candidateConfig.GpuPowerCurve);
        AddCurveRow(curves, 4, "CPU load", _candidateConfig.CpuLoadCurve);
        AddCurveRow(curves, 5, "GPU load", _candidateConfig.GpuLoadCurve);
        curveGroup.Controls.Add(curves);

        _statusValue.AutoSize = true;
        _statusValue.MaximumSize = new Size(760, 0);
        _statusValue.Margin = new Padding(3, 12, 3, 3);

        var safetyBoundary = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Margin = new Padding(3, 12, 3, 3),
            Text =
                "P13.4 boundary: Automatic remains execution-gated CLOSED. The live recommendation shown above " +
                "comes only from AdaptiveFanPolicyShadowEvaluator and cannot acquire Custom authority."
        };

        root.Controls.Add(group, 0, 1);
        root.Controls.Add(manualGroup, 0, 2);
        root.Controls.Add(previewGroup, 0, 3);
        root.Controls.Add(curveGroup, 0, 4);
        root.Controls.Add(_statusValue, 0, 5);
        root.Controls.Add(safetyBoundary, 0, 6);
        return root;
    }

    private static void AddCurveRow(
        TableLayoutPanel table,
        int row,
        string name,
        IReadOnlyList<AdaptiveFanCurvePoint> curve)
    {
        var value =
            string.Join(
                "  ",
                curve.Select(point =>
                    $"{point.Input:0.#}→{point.Level}"));

        table.Controls.Add(
            new Label
            {
                Text = name + ":",
                AutoSize = true
            },
            0,
            row);

        table.Controls.Add(
            new Label
            {
                Text = value,
                AutoSize = true,
                MaximumSize = new Size(590, 0)
            },
            1,
            row);
    }

    private bool TryBeginControlInteraction(
        P13ControlInteractionKind kind,
        AdaptiveFanProductionMode? requestedMode,
        int? equalFanLevel)
    {
        if (Interlocked.CompareExchange(
                ref _controlInteractionInFlight,
                1,
                0) != 0)
        {
            ReportPreActionRejection(
                kind,
                requestedMode,
                equalFanLevel,
                "another control interaction is already in progress");
            return false;
        }

        try
        {
            if (_interactionAuthorizationProvider is not null &&
                !_interactionAuthorizationProvider(
                    kind,
                    requestedMode,
                    equalFanLevel))
            {
                Interlocked.Exchange(
                    ref _controlInteractionInFlight,
                    0);
                ReportPreActionRejection(
                    kind,
                    requestedMode,
                    equalFanLevel,
                    "qualification pre-action fence rejected the interaction");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(
                ref _controlInteractionInFlight,
                0);
            ReportPreActionRejection(
                kind,
                requestedMode,
                equalFanLevel,
                $"qualification pre-action fence failed closed: {ex.Message}");
            return false;
        }
    }

    private void EndControlInteraction() =>
        Interlocked.Exchange(
            ref _controlInteractionInFlight,
            0);

    private void ReportPreActionRejection(
        P13ControlInteractionKind kind,
        AdaptiveFanProductionMode? requestedMode,
        int? equalFanLevel,
        string reason)
    {
        var detail =
            $"P13 control interaction blocked before production adapter access: {reason}.";
        RefreshState(detail);
        _log(detail);
        _interactionObserver?.Invoke(
            new P13ControlInteractionObservation(
                kind,
                requestedMode,
                equalFanLevel,
                null,
                detail,
                DateTimeOffset.UtcNow));
    }

    private async Task ApplyManualAsync()
    {
        var level =
            decimal.ToInt32(
                _manualLevel.Value);

        // The provider is intentionally not consulted while the compile/runtime
        // Manual gate is closed. A UI click cannot consume a SafetyGate
        // sequence or touch the coordinator/backend in the blocked state.
        if (!_controller.ManualExecutionAuthorized)
        {
            RefreshState(
                $"Manual {level}/{level} blocked: post-M9 Manual execution gate is CLOSED.");
            _log(
                $"P13 manual request {level}/{level}: BLOCKED before SafetyGate/coordinator access; Manual gate CLOSED.");
            return;
        }

        if (_manualInteractionReadyProvider is not null &&
            !_manualInteractionReadyProvider())
        {
            RefreshState(
                $"Manual {level}/{level} blocked: qualification readiness has not been published.");
            _log(
                $"P13 manual request {level}/{level}: BLOCKED before SafetyGate/coordinator access; qualification readiness is false.");
            return;
        }

        if (_controller.Mode != AdaptiveFanProductionMode.Manual)
        {
            RefreshState(
                "Manual apply refused because Manual mode is not selected.");
            return;
        }

        if (!TryBeginControlInteraction(
                P13ControlInteractionKind.ManualApply,
                null,
                level))
        {
            return;
        }

        try
        {
            var safety =
                _controlSafetyProvider();

            if (safety is null)
            {
                if (_interactionAuthorizationProvider is not null)
                {
                    throw new InvalidOperationException(
                        "qualification lost the current control SafetyGate result before Manual Apply");
                }

                RefreshState(
                    "Manual apply refused because no current control SafetyGate result is available.");
                return;
            }

            var result =
                await _controller.ApplyManualAsync(
                    level,
                    safety,
                    _controlSafetyProvider,
                    CancellationToken.None);

            RefreshState(result.Detail);
            _log(
                $"P13 manual request {level}/{level}: action={result.Action}; " +
                $"authorized={result.ExecutionAuthorized}; authority={result.Authority}; {result.Detail}");
            _interactionObserver?.Invoke(
                new P13ControlInteractionObservation(
                    P13ControlInteractionKind.ManualApply,
                    null,
                    level,
                    result,
                    null,
                    DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            RefreshState(
                $"Manual request failed closed: {ex.Message}");
            _log(
                $"P13 manual request {level}/{level} FAILED CLOSED: {ex}");
            _interactionObserver?.Invoke(
                new P13ControlInteractionObservation(
                    P13ControlInteractionKind.ManualApply,
                    null,
                    level,
                    null,
                    ex.ToString(),
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            EndControlInteraction();
        }
    }

    private async Task RequestModeAsync(
        AdaptiveFanProductionMode mode)
    {
        if (mode == AdaptiveFanProductionMode.Manual &&
            _manualInteractionReadyProvider is not null &&
            !_manualInteractionReadyProvider())
        {
            RefreshState(
                "Manual mode request blocked: qualification readiness has not been published.");
            _log(
                "P13 mode request Manual: BLOCKED before production adapter access; qualification readiness is false.");
            return;
        }

        if (!TryBeginControlInteraction(
                P13ControlInteractionKind.ModeRequest,
                mode,
                null))
        {
            return;
        }

        try
        {
            var result =
                await _controller.SetModeAsync(
                    mode,
                    CancellationToken.None);

            RefreshState(result.Detail);
            _log(
                $"P13 mode request {mode}: action={result.Action}; " +
                $"authorized={result.ExecutionAuthorized}; authority={result.Authority}; {result.Detail}");
            _interactionObserver?.Invoke(
                new P13ControlInteractionObservation(
                    P13ControlInteractionKind.ModeRequest,
                    mode,
                    null,
                    result,
                    null,
                    DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            RefreshState(
                $"Mode request failed closed: {ex.Message}");
            _log(
                $"P13 mode request {mode} FAILED CLOSED: {ex}");
            _interactionObserver?.Invoke(
                new P13ControlInteractionObservation(
                    P13ControlInteractionKind.ModeRequest,
                    mode,
                    null,
                    null,
                    ex.ToString(),
                    DateTimeOffset.UtcNow));
        }
        finally
        {
            EndControlInteraction();
        }
    }

    private void RefreshState(string detail)
    {
        _modeValue.Text = _controller.Mode.ToString();
        _authorityValue.Text = _lastAuthority.ToString();
        _manualGateValue.Text =
            _controller.ManualExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _automaticGateValue.Text =
            _controller.AutomaticExecutionAuthorized
                ? "OPEN"
                : "CLOSED";
        _statusValue.Text = detail;
    }

    private static Label ValueLabel() =>
        new()
        {
            AutoSize = true,
            Font = new Font(
                SystemFonts.DefaultFont,
                FontStyle.Bold)
        };
}
