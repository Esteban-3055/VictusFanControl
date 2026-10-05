using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed partial class MainForm
{
    private readonly string? _block1Root;
    private readonly object _block1Sync = new();
    private readonly Block1QualificationSequence _block1 = new();
    private bool Block1Enabled => _block1Root is not null;
    private sealed record Block1Identity(string Directory, int GuardianPid, long GuardianStartUtcTicks);
    private Block1Identity? _block1A, _block1B;
    private string? _block1PerformanceReport, _block1FanConfiguration;
    private int _block1HealthySamples, _block1RestoreTransitions;
    private DateTimeOffset? _block1LastHealthy;
    private volatile bool _block1FirmwareRequested, _block1TrayRequested;
    private long _block1EventNumber;
    private readonly Stopwatch _block1Clock = Stopwatch.StartNew();
    private void MonitorBlock1()
    {
        if (!Block1Enabled || _shutdownStarted || _block1.Failed || _block1.Phase == Block1Phase.Completed) return;
        if (_block1Clock.Elapsed > TimeSpan.FromMinutes(16) || File.Exists(Path.Combine(_block1Root!, "block1.abort.signal")))
            FailBlock1("Block1 deadline expired or harness requested abort.");
        if (_block1.Phase != Block1Phase.Preparing && !Block1LimitsValid())
            FailBlock1("Required CPU/GPU limits or AC status lost.");
    }
    private readonly List<(TelemetrySnapshot Snapshot, AdaptiveFanProductionResult Result)> _block1PendingAutomatic = [];
    private static string Block1OwnerStartTicks()
    {
        using var owner = Process.GetCurrentProcess();
        return owner.StartTime.ToUniversalTime().Ticks.ToString();
    }

    private void InitializeBlock1()
    {
        if (!Block1Enabled) return;
        if (_wmiFanBackend is null || !_fanProductionController.AutomaticExecutionAuthorized ||
            !_fanProductionController.ManualExecutionAuthorized || Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized ||
            !WmiOnlyInvestigationPolicy.Enabled || !Hp8C40TargetProfile.Matches(_hardwareIdentity, out _))
            throw new InvalidOperationException("Block1 requires exact-target writable WMI, both isolated modes and normal Automatic CLOSED.");
        Directory.CreateDirectory(_block1Root!);
        if (File.Exists(Path.Combine(_block1Root!, "block1.state.json")) || File.Exists(Path.Combine(_block1Root!, "block1.events.jsonl")))
            throw new IOException("Block1 refuses to overwrite existing evidence.");
        _block1FanConfiguration = FanConfigurationStore.Serialize(_fanProductionController.AutomaticConfiguration!);
        lock (_block1Sync) WriteBlock1Locked("initialized");
        AppendEvent("BLOCK1: isolated Automatic + Manual enabled. Apply CPU AC 20/40 W and GPU, then wait for console READY.");
    }

    private void WriteBlock1Locked(string kind, object? detail = null)
    {
        if (!Block1Enabled) return;
        var state = new
        {
            schemaVersion = 1, gate = "HP-8C40-WMI-BLOCK1", phase = _block1.Phase.ToString(), failure = _block1.Failure,
            timestampUtc = DateTimeOffset.UtcNow, guiPid = Environment.ProcessId,
            guiStartUtcTicks = Block1OwnerStartTicks(),
            targetProfileId = _targetProfile?.Id, directEcProhibited = WmiOnlyInvestigationPolicy.Enabled,
            deniedEcAccesses = WmiOnlyInvestigationPolicy.DeniedEcAccesses,
            independentFirmwareOwnershipVerified = false, normalAutomaticAuthorized = Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized,
            handoffPassed = _block1.HandoffPassed, rearmPassed = _block1.RearmPassed, closePassed = _block1.ClosePassed,
            automaticDecisions = _block1.AutomaticDecisions, automaticCommands = _block1.AutomaticCommands,
            returnManualLevel = _block1.ReturnManualLevel, lastAcceptedLevel = _wmiFanBackend?.LastAcceptedLevel,
            restoreTransitions = _block1RestoreTransitions, sessionA = _block1A, sessionB = _block1B,
            performanceGuardianReportPath = _block1PerformanceReport,
            performanceConfiguration = _performanceControlSurface.AppliedConfiguration,
            performanceStatus = _performanceControlSurface.LastStatus,
            fanConfiguration = _fanProductionController.AutomaticConfiguration,
            authority = _fanCoordinator.Authority.ToString(), mode = _fanProductionController.Mode.ToString(),
            journalPresent = AutomaticFanJournalPresent, trayExitRequested = _block1TrayRequested
        };
        File.AppendAllText(Path.Combine(_block1Root!, "block1.events.jsonl"), JsonSerializer.Serialize(new
        { ordinal = ++_block1EventNumber, kind, timestampUtc = DateTimeOffset.UtcNow, state, detail }) + Environment.NewLine);
        WmiFanExperiment.WriteJson(Path.Combine(_block1Root!, "block1.state.json"), state);
    }

    private bool Block1LimitsValid() =>
        _performanceControlSurface.LimitsActive &&
        _performanceControlSurface.AppliedConfiguration is { CpuEnabled: true, GpuEnabled: true, AcPl1Watts: 20, AcPl2Watts: 40 } &&
        _performanceControlSurface.LastStatus is { Ok: true, CpuState: "Active", GpuState: "ActiveUnverified", PowerSource: "Ac", RuntimeFailure: null };

    private void ObserveBlock1Snapshot(TelemetrySnapshot snapshot)
    {
        if (!Block1Enabled || _shutdownStarted) return;
        try
        {
            lock (_block1Sync)
            {
                if (_block1.Failed || _block1.Phase == Block1Phase.Completed) return;
                if (!Block1LimitsValid())
                {
                    if (_block1.Phase != Block1Phase.Preparing) throw new InvalidOperationException("Required CPU/GPU limits or AC status lost.");
                    _block1HealthySamples = 0; return;
                }
                if (_block1FanConfiguration != FanConfigurationStore.Serialize(_fanProductionController.AutomaticConfiguration!))
                    throw new InvalidOperationException("Fan configuration changed during Block1.");
                if (_block1.Phase == Block1Phase.Preparing)
                {
                    var safety = GetP13ControlSafety();
                    if (_worker.StateMachine.State != SystemState.Healthy || safety?.CustomControlPermitted != true ||
                        _fanCoordinator.Authority != FanAuthority.Firmware || AutomaticFanJournalPresent)
                    { _block1HealthySamples = 0; return; }
                    EnsureAutomaticFinalReadyEnvelope(snapshot);
                    if (_block1LastHealthy.HasValue && snapshot.Timestamp <= _block1LastHealthy.Value) return;
                    if (_block1LastHealthy.HasValue && snapshot.Timestamp - _block1LastHealthy.Value > TimeSpan.FromSeconds(3))
                        _block1HealthySamples = 0;
                    _block1LastHealthy = snapshot.Timestamp;
                    if (++_block1HealthySamples < 3) return;
                    _block1PerformanceReport = _performanceControlSurface.GuardianReportPath;
                    if (string.IsNullOrWhiteSpace(_block1PerformanceReport)) throw new IOException("Performance report binding missing.");
                    _block1.Ready(); WriteBlock1Locked("ready");
                }
            }
        }
        catch (Exception ex) { FailBlock1(ex.Message); }
    }

    private bool AuthorizeBlock1(P13ControlInteractionKind kind, AdaptiveFanProductionMode? mode, int? level)
    {
        lock (_block1Sync)
        {
            if (kind == P13ControlInteractionKind.ModeRequest && mode == AdaptiveFanProductionMode.Firmware)
            { _block1FirmwareRequested = true; WriteBlock1Locked("firmware-escape-requested"); return true; }
            if (!Block1LimitsValid() || _worker.StateMachine.State != SystemState.Healthy) return false;
            var allowed = _block1.Allows(kind == P13ControlInteractionKind.ManualApply, mode, level);
            if (!allowed) FailBlock1("Unexpected or premature control interaction; follow the console sequence.");
            return allowed;
        }
    }

    private Block1Identity CaptureBlock1Identity()
    {
        var directory = _wmiFanBackend!.GuardianSessionDirectory;
        using var ready = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "ready.json")));
        var r = ready.RootElement;
        using var owner = Process.GetCurrentProcess();
        if (r.GetProperty("OwnerPid").GetInt32() != Environment.ProcessId ||
            r.GetProperty("OwnerStartUtcTicks").GetInt64() != owner.StartTime.ToUniversalTime().Ticks ||
            !r.GetProperty("DirectEcProhibited").GetBoolean() || !File.Exists(WmiFanGuiGuardianHost.LeasePath) ||
            File.Exists(Path.Combine(directory, "guardian-report.json")))
            throw new IOException("Block1 live guardian/lease identity is invalid or already released.");
        using var guardian = Process.GetProcessById(r.GetProperty("GuardianPid").GetInt32());
        if (guardian.HasExited || guardian.StartTime.ToUniversalTime().Ticks != r.GetProperty("GuardianStartUtcTicks").GetInt64())
            throw new IOException("Guardian process identity changed.");
        return new(directory, guardian.Id, guardian.StartTime.ToUniversalTime().Ticks);
    }

    private void EnsureBlock1SessionA()
    {
        if (_block1A is null || CaptureBlock1Identity() != _block1A)
            throw new IOException("Custom handoff changed WMI session/guardian or retired its lease.");
    }

    private void OnBlock1Interaction(P13ControlInteractionObservation o)
    {
        try
        {
            lock (_block1Sync)
            {
                if (_block1.Failed) { WriteBlock1Locked("interaction-after-failure", o); return; }
                if (o.Failure is not null || o.Result is not { ExecutionAuthorized: true } result)
                    throw new InvalidOperationException("Control interaction failed: " + o.Failure);
                if (o.Kind == P13ControlInteractionKind.ModeRequest)
                {
                    if (o.RequestedMode is AdaptiveFanProductionMode.Automatic ||
                        (_block1.Phase == Block1Phase.AutomaticChanged && o.RequestedMode == AdaptiveFanProductionMode.Manual))
                        EnsureBlock1SessionA();
                    if (o.RequestedMode == AdaptiveFanProductionMode.Firmware)
                    {
                        AssertBlock1Release(_block1A);
                        if (_block1RestoreTransitions != 1) throw new IOException("Unexpected restore count during handoff.");
                    }
                    _block1.Mode(o.RequestedMode!.Value, result.Action, result.Authority, _wmiFanBackend?.LastAcceptedLevel);
                    if (o.RequestedMode == AdaptiveFanProductionMode.Manual) _block1FirmwareRequested = false;
                }
                else
                {
                    var identity = CaptureBlock1Identity();
                    if (_block1.Phase == Block1Phase.ManualSelectedA) _block1A = identity;
                    else if (_block1.Phase == Block1Phase.ManualSelectedB)
                    {
                        if (identity.Directory == _block1A?.Directory ||
                            (identity.GuardianPid == _block1A?.GuardianPid && identity.GuardianStartUtcTicks == _block1A.GuardianStartUtcTicks))
                            throw new IOException("Rearm reused the released session/guardian.");
                        _block1B = identity;
                    }
                    else EnsureBlock1SessionA();
                    _block1.Manual(o.EqualFanLevel!.Value, result.Action, result.Authority);
                }
                WriteBlock1Locked("interaction", o);
                if (o.RequestedMode == AdaptiveFanProductionMode.Automatic)
                {
                    foreach (var pending in _block1PendingAutomatic)
                        RecordBlock1Automatic(pending.Snapshot, pending.Result);
                    _block1PendingAutomatic.Clear();
                }
            }
        }
        catch (Exception ex) { FailBlock1(ex.Message); }
    }

    private void RecordBlock1Automatic(TelemetrySnapshot snapshot, AdaptiveFanProductionResult result)
    {
        if (!Block1Enabled) return;
        try
        {
            lock (_block1Sync)
            {
                if (_block1.Failed || _fanProductionController.Mode != AdaptiveFanProductionMode.Automatic) return;
                if (_block1.Phase == Block1Phase.ManualActiveA)
                {
                    if (_block1PendingAutomatic.Count >= 4) throw new IOException("Automatic selection evidence callback stalled.");
                    _block1PendingAutomatic.Add((snapshot, result));
                    return;
                }
                EnsureBlock1SessionA();
                _block1.Automatic(result.Action, result.Authority, result.EqualFanLevel);
                WriteBlock1Locked("automatic-decision", new { snapshot.Timestamp, snapshot.CpuControlTemperatureC,
                    snapshot.GpuTemperatureC, snapshot.CpuFanRpm, snapshot.GpuFanRpm, result });
            }
        }
        catch (Exception ex) { FailBlock1(ex.Message); }
    }

    private void RecordBlock1Authority(FanAuthorityChangedEventArgs change)
    {
        if (!Block1Enabled) return;
        lock (_block1Sync)
        {
            if (change.Current == FanAuthority.Restoring) _block1RestoreTransitions++;
            WriteBlock1Locked("authority", change);
            if (!_block1.Failed && (change.Current == FanAuthority.Faulted ||
                (change.Current == FanAuthority.Restoring && !_block1FirmwareRequested && !_block1TrayRequested)))
                FailBlock1("Unexpected safety/lifecycle/authority restoration: " + change.Reason);
        }
    }

    private void AssertBlock1Release(Block1Identity? identity)
    {
        var release = _wmiFanBackend?.ReleaseEvidence;
        if (identity is null || release is not { ReleaseRequestAccepted: true, LegacyDefaultRequestAccepted: true,
            GuardianLeaseRetired: true, IndependentFirmwareOwnershipVerified: false } || AutomaticFanJournalPresent ||
            !string.Equals(release.ReportPath, Path.Combine(identity.Directory, "guardian-report.json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Block1 release lacks matching accepted requests and lease retirement.");
        using var report = JsonDocument.Parse(File.ReadAllText(release.ReportPath));
        var r = report.RootElement;
        if (r.GetProperty("ExitReason").GetString() != "CLIENT_RELEASE" || r.GetProperty("Failure").ValueKind != JsonValueKind.Null)
            throw new IOException("Block1 guardian did not complete a clean requested release.");
    }

    private void RequestBlock1TrayExit()
    {
        if (!Block1Enabled) return;
        try
        {
            lock (_block1Sync)
            {
                if (_block1.Failed) { _block1TrayRequested = true; return; }
                if (_fanCoordinator.Authority != FanAuthority.Custom || CaptureBlock1Identity() != _block1B)
                    throw new IOException("Tray exit must begin with the second session active.");
                _block1.BeginTrayExit(); _block1TrayRequested = true; WriteBlock1Locked("tray-exit-requested");
            }
        }
        catch (Exception ex) { FailBlock1(ex.Message); }
    }

    private void CompleteBlock1Shutdown()
    {
        if (!Block1Enabled) return;
        try
        {
            lock (_block1Sync)
            {
                if (_block1.Failed) { WriteBlock1Locked("failed-shutdown"); return; }
                AssertBlock1Release(_block1B);
                if (_block1RestoreTransitions != 2 || !_block1TrayRequested || WmiOnlyInvestigationPolicy.DeniedEcAccesses != 0)
                    throw new IOException("Block1 close authority/EC/tray audit failed.");
                using var performance = JsonDocument.Parse(File.ReadAllText(_block1PerformanceReport!));
                var r = performance.RootElement;
                if (r.GetProperty("OwnerPid").GetInt32() != Environment.ProcessId || r.GetProperty("ExitReason").GetString() != "CLIENT_SHUTDOWN" ||
                    r.GetProperty("CpuDomainState").GetString() != "Disabled" || r.GetProperty("GpuDomainState").GetString() != "Disabled" ||
                    r.GetProperty("Failure").ValueKind != JsonValueKind.Null)
                    throw new IOException("Performance cleanup report failed.");
                _block1.Complete(_fanCoordinator.Authority == FanAuthority.Firmware, true, _shutdownComplete);
                WriteBlock1Locked("complete"); Environment.ExitCode = 0;
            }
        }
        catch (Exception ex) { FailBlock1(ex.Message); }
    }

    private void FailBlock1(string reason)
    {
        if (!Block1Enabled) return;
        lock (_block1Sync)
        {
            if (_block1.Failed) return;
            _block1.Fail(reason); Environment.ExitCode = 171;
            try { WriteBlock1Locked("failure"); }
            catch (Exception ex) { AppLog.Write("Block1 failure evidence could not be written: " + ex); }
            AppLog.Write("BLOCK1 FAIL_CLOSED: " + reason);
        }
        // Never run restoration inline on a coordinator callback (it owns the coordinator gate).
        if (!_shutdownStarted)
            _ = Task.Run(async () =>
            {
                try { await _fanProductionController.ReleaseToFirmwareAsync("Block1 interrupted: " + reason, CancellationToken.None); }
                catch (Exception ex) { AppLog.Write("Block1 recovery unresolved: " + ex); }
            });
    }
}
