using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed record ProductRuntimeState
{
    internal string Hardware { get; init; } = "Detectando equipo…";
    internal string Target { get; init; } = "Sin destino validado";
    internal string Source { get; init; } = "Unknown";
    internal string Runtime { get; init; } = "Initializing";
    internal string FanMode { get; init; } = "Firmware";
    internal string FanAuthority { get; init; } = "Firmware";
    internal int? FanLevel { get; init; }
    internal bool ManualAuthorized { get; init; }
    internal bool AutomaticAuthorized { get; init; }
    internal bool AutomaticReview { get; init; }
    internal bool AutomaticPreparing { get; init; }
    internal TelemetrySnapshot? AutomaticInterruptionSnapshot { get; init; }
    internal string? AutomaticSessionId { get; init; }
    internal TelemetrySnapshot? AutomaticDecisionSnapshot { get; init; }
    internal int? AutomaticReviewRemainingSeconds { get; init; }
    internal int? AutomaticCpuSpikeRemainingMilliseconds { get; init; }
    internal AdaptiveFanProductionResult? AutomaticDecision { get; init; }
    internal FanConfiguration? AppliedAutomaticConfiguration { get; init; }
    internal bool PerformanceSupported { get; init; }
    internal bool CanApplyPerformance { get; init; }
    internal bool PerformanceActive { get; init; }
    internal bool PerformanceProcessPresent { get; init; }
    internal string CpuState { get; init; } = "Disabled";
    internal string GpuState { get; init; } = "Disabled";
    internal string GuardianState { get; init; } = "Sin sesión";
    internal string? CpuStatus { get; init; }
    internal string? GpuStatus { get; init; }
    internal string? AppliedFanProfile { get; init; }
    internal PerformanceGuiSessionConfiguration? AppliedPerformance { get; init; }
    internal string AppliedPerformanceSource { get; init; } = "Unknown";
    internal TelemetrySnapshot? Snapshot { get; init; }
    internal string Message { get; init; } = "Inicio en Firmware. Automatic activa CPU/GPU; Aplicar permite su uso independiente.";
    internal string? Failure { get; init; }
    internal bool LifecycleBlocked { get; init; }
    internal string? LifecycleBlockReason { get; init; }
}

internal interface IProductRuntime : IAsyncDisposable
{
    event Action<ProductRuntimeState>? Changed;
    ProductRuntimeState State { get; }
    void Start();
    Task SelectFanModeAsync(AdaptiveFanProductionMode mode, ProductProfiles profiles);
    Task ApplyManualAsync(int level);
    Task ApplyPerformanceAsync(ProductProfiles profiles);
    Task ReleasePerformanceAsync();
    void FenceLifecycle(string reason);
    Task ReleaseForLifecycleAsync(string reason);
    void ResumeTelemetry(string reason);
}

/// <summary>Presentation adapter. Owns no raw hardware commands; uses the existing controllers and IPC.</summary>
internal sealed class ProductRuntime : IProductRuntime
{
    private readonly ProductAutomaticReview? _automaticReview;
    private readonly ProductAutomaticActivation _automaticActivation = new();
    private readonly HardwareIdentity _hardware;
    private readonly HardwareTargetProfile? _target;
    private readonly Hp8C40WmiFanControlBackend? _wmi;
    private readonly FanControlCoordinator _fans;
    private readonly AdaptiveFanProductionController _controller;
    private readonly Hp8C40ThermalEmergencyConfirmation _thermal = new();
    private readonly TelemetryWorker _worker;
    private readonly PerformanceGuardianClient _performance;
    private readonly SemaphoreSlim _commands = new(1,1);
    private readonly SemaphoreSlim _fanCommands = new(1,1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateSync = new();
    private ProductRuntimeState _state = new();
    private TelemetrySnapshot? _snapshot;
    private string? _selectedFanProfile;
    private string? _lifecycleBlockReason;
    private AdaptiveFanProductionResult? _automaticDecision;
    private TelemetrySnapshot? _automaticInterruptionSnapshot;
    private TelemetrySnapshot? _automaticDecisionSnapshot;
    private string? _automaticSessionId;
    private PerformanceGuiSessionConfiguration? _automaticPerformance;
    private ProductAutomaticActivation.Ticket? _activeAutomaticTicket;
    private DateTimeOffset _automaticStartedUtc;
    private volatile bool _plannedFanRelease;
    private PerformanceGuiSessionConfiguration? _requestedPerformance;
    private Task? _poll;
    private volatile bool _closing, _lifecycleBlocked;
    public event Action<ProductRuntimeState>? Changed;
    public ProductRuntimeState State { get { lock (_stateSync) return _state; } }

    internal ProductRuntime(string modules, ProductProfiles profiles, bool automaticReview = false)
    {
        _hardware = HardwareIdentityReader.ReadCurrent();
        _target = HpHardwareTargetResolver.Resolve(_hardware,out _);
        if (automaticReview)
        {
            if (!ProductAutomaticReview.IsAuthorized(true, _target?.Id)) throw new InvalidOperationException("Prueba Automatic disponible solo para el HP 8C40/F.18 validado.");
            _automaticReview = new();
            _state = _state with { Message = "Prueba Automatic habilitada: 10–50, máximo 5 min. Inicio en Firmware; requiere clic explícito." };
        }
        if (_hardware.BoardProduct == "8C40") WmiOnlyInvestigationPolicy.Enable();
        IFanControlBackend backend;
        try
        {
            if (_target == Hp8C40TargetProfile.Instance)
            {
                _wmi = new(new WmiFanGuiGuardianClient()); backend = _wmi;
                _wmi.CommandAccepted += (_, message) => AppLog.Write("PRODUCT WMI REQUEST ACCEPTED: " + message);
            }
            else backend = new DisabledFanControlBackend();
        }
        catch (Exception ex) { backend = new DisabledFanControlBackend(); AppLog.Write("Product backend unavailable: " + ex); }
        _fans = new(backend);
        _controller = new(_fans, Hp8C40AdaptiveCandidateV1.Create(),
            backend.CanWrite && Hp8C40PostM9UserControlGate.IsManualAuthorizedForTarget(_target?.Id),
            backend.CanWrite && (Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized || _automaticReview is not null),
            automaticHardware: _target == Hp8C40TargetProfile.Instance ? _hardware : null,
            automaticConfiguration: profiles.Ac.Fan,
            automaticMinimumLevel: _automaticReview is not null ? 10 : Hp8C40AutomaticPolicy.MinimumLevel,
            useRawCpuThermalResponse: _automaticReview is not null);
        WmiFanExperimentBoundary.PlannedGuiRelease = () => _plannedFanRelease;
        _performance = new(modules);
        _worker = new(modules)
        {
            FreshFanAcquisitionRequired = () => _controller.AutomaticFreshAcquisitionRequired,
            AcquisitionBudgetMilliseconds = () => ProductAutomaticReview.AcquisitionBudget(
                _controller.AutomaticAcquisitionBudgetMilliseconds, _automaticReview?.RemainingCpuSpikeMilliseconds),
            NormalPollingDelayMilliseconds = () => _controller.AutomaticNormalPollingDelayMilliseconds,
            SnapshotProcessor = ProcessAutomaticAsync
        };
        _fans.AuthorityChanged += (_, e) => { AppLog.Write("Product fan authority: " + e); Publish(e.Reason); };
        _worker.SnapshotAvailable += (_, snapshot) => { AppLog.WriteTelemetry(snapshot); _snapshot = snapshot; _ = EnforceAsync(); Publish(); };
        _worker.StateMachine.StateChanged += (_, e) =>
        {
            if (!_closing && e.Current != SystemState.Healthy && (_automaticActivation.Pending || _fans.Authority == FanAuthority.Custom || _controller.Mode == AdaptiveFanProductionMode.Automatic))
            {
                _automaticActivation.Cancel();
                _lifecycleBlocked = true; _lifecycleBlockReason = "Telemetría no disponible: " + e.Reason; _fans.CloseCustomAdmissionForLifecycleBoundary();
                _ = ResetInterruptedFanAsync("Telemetría no disponible: " + e.Reason);
            }
            _ = EnforceAsync(); Publish(e.Reason);
        };
        _worker.EventLogged += (_, e) => AppLog.Write(e);
        Publish();
    }
    public void Start() { _worker.Start(); _poll = PollAsync(); }
    private SafetyGateResult Safety()
    {
        var s = SafetyGate.Evaluate(_hardware, _worker.StateMachine.State, _snapshot, DateTimeOffset.UtcNow, _fans.BackendCanWrite);
        return _controller.Mode == AdaptiveFanProductionMode.Automatic
            ? _controller.EvaluateAutomaticSafety(_snapshot,s,observe:true)
            : _thermal.Apply(_hardware,_snapshot,s);
    }
    private async Task EnforceAsync()
    {
        if (_closing) return;
        try { await _fans.EnforceSafetyAsync(Safety(), "Product runtime safety", CancellationToken.None); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Publish("Supervisión de seguridad", ex.Message); }
    }
    private async Task ProcessAutomaticAsync(TelemetrySnapshot snapshot, CancellationToken token)
    {
        await _fanCommands.WaitAsync(token);
        try
        {
            _snapshot = snapshot;
            if (_closing || _lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Automatic || snapshot.Timestamp <= _automaticStartedUtc) return;
            if (_activeAutomaticTicket is null || !_automaticActivation.IsCurrent(_activeAutomaticTicket)) return;
            try
            {
                var source = new WindowsPerformancePowerSourceReader().Read();
                var expected = source.Source == PerformancePowerSourceKind.Ac ? "Ac" : source.Source == PerformancePowerSourceKind.Battery ? "Battery" : null;
                if (expected is null || _selectedFanProfile != expected)
                {
                    _automaticInterruptionSnapshot = snapshot;
                    _lifecycleBlocked = true; _lifecycleBlockReason = "Cambio de fuente durante la prueba Automatic; requiere reinicio."; _fans.CloseCustomAdmissionForLifecycleBoundary(); _automaticReview?.Stop();
                    await _controller.ReleaseToFirmwareAsync("Cambio de fuente: volver a seleccionar Automatic con el perfil real.",token);
                    Publish("La fuente cambió; ventiladores en Firmware. Requiere calificación de transición de curvas."); return;
                }
                if (_automaticPerformance is null || !ProductAutomaticActivation.PerformanceReady(_automaticPerformance,
                    _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, expected))
                    throw new InvalidOperationException("Automatic interrumpido: CPU/GPU sin confirmación vigente o Performance Guardian en recuperación.");
                var raw = SafetyGate.Evaluate(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                // Use the shared bounded 8C40 admission, rather than rejecting the
                // unchanged raw SafetyGate's first CPU >=95 C sample here.
                var effective = _controller.EvaluateAutomaticSafety(snapshot, raw, observe: true);
                var wasCpuPending = _automaticReview?.RemainingCpuSpikeMilliseconds.HasValue == true;
                if (_automaticReview is not null && !_automaticReview.Observe(snapshot,effective))
                {
                    Publish("Prueba Automatic: verificando tres adquisiciones Healthy antes de controlar."); return;
                }
                var cpuPending = _automaticReview?.RemainingCpuSpikeMilliseconds.HasValue == true;
                if (cpuPending != wasCpuPending)
                    AppLog.Write("PRODUCT AUTOMATIC CPU SPIKE: " + System.Text.Json.JsonSerializer.Serialize(new
                    { automaticSessionId = _automaticSessionId, snapshotTimestamp = snapshot.Timestamp,
                        cpuControlTemperatureC = snapshot.CpuControlTemperatureC, pending = cpuPending,
                        remainingMilliseconds = _automaticReview?.RemainingCpuSpikeMilliseconds }));
                var decision = await _controller.ProcessAutomaticAsync(snapshot,raw,token, refreshRawSafetyProvider: () =>
                {
                    if (!ReferenceEquals(_snapshot,snapshot) || _closing || _lifecycleBlocked) return null;
                    // A voluntary Firmware click queues release after this in-flight fan operation.
                    // Do not turn that cancellation into a false thermal/lifecycle interruption.
                    if (_automaticPerformance is null || !ProductAutomaticActivation.PerformanceReady(_automaticPerformance,
                        _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, _selectedFanProfile ?? "Unknown")) return null;
                    _automaticReview?.EnsureDispatchAllowed(snapshot);
                    var currentSource = new WindowsPerformancePowerSourceReader().Read().Source;
                    if (currentSource.ToString() != _selectedFanProfile) return null;
                    return SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                });
                _automaticDecision = decision; _automaticDecisionSnapshot = snapshot;
                AppLog.Write("PRODUCT AUTOMATIC DECISION: " + System.Text.Json.JsonSerializer.Serialize(new { automaticSessionId = _automaticSessionId, snapshotTimestamp = snapshot.Timestamp, decision }));
                if (decision.Action == AdaptiveFanProductionActionKind.Blocked || decision.Action == AdaptiveFanProductionActionKind.RestoreFirmware)
                    throw new InvalidOperationException(decision.Detail);
                Publish($"Prueba Automatic · {_selectedFanProfile} · nivel {decision.EqualFanLevel?.ToString() ?? "—"} · {_automaticReview?.RemainingSeconds} s restantes.");
            }
            catch (Exception ex)
            {
                _automaticInterruptionSnapshot = snapshot; AppLog.Write("PRODUCT AUTOMATIC INTERRUPTED: " + _automaticSessionId + " · " + ex.Message); _automaticActivation.Cancel();
                _lifecycleBlocked = true; _lifecycleBlockReason = ex.Message; _fans.CloseCustomAdmissionForLifecycleBoundary();
                _automaticReview?.Stop();
                var failure = ex.Message;
                try { await _controller.ReleaseToFirmwareAsync("Automatic interrumpido: " + ex.Message,CancellationToken.None); }
                catch (Exception release) { failure += "; liberación no resuelta: " + release.Message; }
                Publish("Automatic interrumpido; reiniciar después de una liberación limpia.",failure);
            }
        }
        finally { _fanCommands.Release(); }
    }
    public Task SelectFanModeAsync(AdaptiveFanProductionMode mode, ProductProfiles profiles)
    {
        // Fence synchronously at the user's click, including a command not yet admitted by its queue.
        if (mode == AdaptiveFanProductionMode.Automatic) return ActivateAutomaticAsync(profiles);
        _automaticActivation.Cancel();
        return FanCommandAsync(() => SetFanModeAsync(mode));
    }
    private Task ActivateAutomaticAsync(ProductProfiles profiles)
    {
        if (!_closing && !_lifecycleBlocked && _controller.Mode == AdaptiveFanProductionMode.Automatic)
        {
            Publish("Automatic ya está seleccionado. Volver a pulsarlo no reaplica límites ni renueva los cinco minutos.");
            return Task.CompletedTask;
        }
        // Reject unavailable modes before a new generation could supersede a running session.
        AutomaticAdmission();
        var ticket = _automaticActivation.Begin(profiles);
        ProductPowerProfile? slot = null;
        Publish("Preparando Automatic: primero CPU/GPU, después la curva. Firmware cancela la entrada.");
        return CommandAsync(async () =>
        {
            try
            {
                await _automaticActivation.RunAsync(ticket,
                    () => FanCommandAsync(() =>
                    {
                        _automaticActivation.EnsureCurrent(ticket); slot = AutomaticAdmission(); return Task.CompletedTask;
                    }),
                    async () =>
                    {
                        _automaticActivation.EnsureCurrent(ticket);
                        var configuration = ticket.Performance;
                        await ProductAutomaticActivation.PreparePerformanceAsync(configuration, _performance.HasProcess,
                            _performance.AppliedConfiguration, async () => { await _performance.StatusAsync(); },
                            async () => { _requestedPerformance = configuration; await _performance.EnableAsync(configuration); });
                    },
                    () => FanCommandAsync(async () =>
                    {
                        _automaticActivation.EnsureCurrent(ticket);
                        var current = AutomaticAdmission();
                        if (current != slot) throw new InvalidOperationException("La fuente cambió durante la preparación; selecciona Automatic de nuevo con la fuente estable.");
                        if (!ProductAutomaticActivation.PerformanceReady(ticket.Performance, _performance.AppliedConfiguration,
                            _performance.LastStatus, _performance.LastStatusFresh, current.ToString()))
                            throw new InvalidOperationException("CPU/GPU no confirmaron ambos límites para la fuente real; Automatic permanece en Firmware.");
                        await _controller.ConfigureAutomaticAsync(ticket.Profiles.Get(current).Fan, CancellationToken.None);
                        // Configuration may await a controller lock; recheck cancellation before committing.
                        _automaticActivation.EnsureCurrent(ticket);
                        if (AutomaticAdmission() != current || !ProductAutomaticActivation.PerformanceReady(ticket.Performance,
                            _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, current.ToString()))
                            throw new InvalidOperationException("Fuente o estado CPU/GPU cambiaron durante la preparación; Automatic permanece en Firmware.");
                        _selectedFanProfile = current.ToString(); _automaticPerformance = ticket.Performance;
                        _activeAutomaticTicket = ticket;
                        await SetFanModeAsync(AdaptiveFanProductionMode.Automatic);
                        if (!_automaticActivation.IsCurrent(ticket))
                        {
                            await SetFanModeAsync(AdaptiveFanProductionMode.Firmware);
                            _automaticActivation.EnsureCurrent(ticket);
                        }
                        AppLog.Write("PRODUCT AUTOMATIC ACTIVATED WITH PERFORMANCE: " + System.Text.Json.JsonSerializer.Serialize(new { automaticSessionId = _automaticSessionId, performance = ticket.Performance, fan = _controller.AutomaticConfiguration }));
                    }));
            }
            finally { Publish(); }
        });
    }
    private ProductPowerProfile AutomaticAdmission()
    {
        if (_closing || _lifecycleBlocked) throw new InvalidOperationException("Sesión interrumpida; Automatic no puede rearmarse.");
        if (!_controller.AutomaticExecutionAuthorized) throw new InvalidOperationException("Automatic está implementado, pero su gate normal sigue cerrado.");
        if (_controller.Mode != AdaptiveFanProductionMode.Firmware || _fans.Authority != FanAuthority.Firmware)
            throw new InvalidOperationException("Vuelve a Firmware antes de aplicar una curva. Un clic repetido no renueva la prueba.");
        if (_worker.StateMachine.State != SystemState.Healthy || !Safety().CustomControlPermitted)
            throw new InvalidOperationException("Espera telemetría Healthy completa y vigente antes de seleccionar Automatic.");
        var source = new WindowsPerformancePowerSourceReader().Read().Source;
        return source switch { PerformancePowerSourceKind.Ac => ProductPowerProfile.Ac,
            PerformancePowerSourceKind.Battery => ProductPowerProfile.Battery, _ => throw new InvalidOperationException("Fuente real desconocida.") };
    }
    private async Task SetFanModeAsync(AdaptiveFanProductionMode mode)
    {
        if (mode != AdaptiveFanProductionMode.Firmware && _lifecycleBlocked)
            throw new InvalidOperationException("Sesión interrumpida. La reapertura tras lifecycle permanece cerrada; usa Firmware y reinicia después de una liberación limpia.");
        if (mode == AdaptiveFanProductionMode.Automatic) { _automaticSessionId = Guid.NewGuid().ToString("N"); _automaticDecision = null; _automaticDecisionSnapshot = null; _automaticInterruptionSnapshot = null; _automaticStartedUtc = DateTimeOffset.UtcNow; _automaticReview?.Start(); }
        _plannedFanRelease = mode == AdaptiveFanProductionMode.Firmware;
        AdaptiveFanProductionResult result;
        try { result = await _controller.SetModeAsync(mode,CancellationToken.None); }
        finally { _plannedFanRelease = false; }
        if (!result.ExecutionAuthorized) throw new InvalidOperationException(result.Detail);
        if (mode != AdaptiveFanProductionMode.Automatic) { _selectedFanProfile = null; _automaticPerformance = null; _activeAutomaticTicket = null; _automaticReview?.Stop(); }
        Publish(result.Detail);
    }
    public Task ApplyManualAsync(int level) => FanCommandAsync(async () =>
    {
        if (_lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Manual) throw new InvalidOperationException("Selecciona Manual antes de aplicar; una sesión interrumpida no puede rearmarse.");
        var result = await _controller.ApplyManualAsync(level,Safety(),() => Safety(),CancellationToken.None);
        if (!result.ExecutionAuthorized || result.Action == AdaptiveFanProductionActionKind.Blocked) throw new InvalidOperationException(result.Detail);
        Publish(result.Detail);
    });
    internal static bool PerformanceAdmissionPermitted(bool closing,bool lifecycleBlocked,bool hasProcess,SystemState runtime,FanAuthority authority) =>
        !closing && !lifecycleBlocked && !hasProcess && runtime == SystemState.Healthy && authority is not (FanAuthority.Faulted or FanAuthority.Restoring);
    public Task ApplyPerformanceAsync(ProductProfiles profiles) => CommandAsync(async () =>
    {
        if (_target != Hp8C40TargetProfile.Instance || !PerformanceAdmissionPermitted(_closing,_lifecycleBlocked,_performance.HasProcess,_worker.StateMachine.State,_fans.Authority))
            throw new InvalidOperationException("Aplicar rendimiento requiere el destino validado, telemetría Healthy y ninguna recuperación pendiente.");
        profiles.Validate(); var configuration = profiles.PerformanceConfiguration(); configuration.Validate();
        if (_performance.HasProcess) throw new InvalidOperationException("Libera la sesión CPU/GPU antes de cambiar su configuración.");
        _requestedPerformance = configuration; Publish("Aplicando CPU/GPU…"); await _performance.EnableAsync(configuration); Publish("Sesión de rendimiento aplicada. GPU: solicitud aceptada; rango independiente no verificable.");
    });
    public Task ReleasePerformanceAsync() => _automaticActivation.ReleaseLimitsAsync(
        // Restore fans before removing the prerequisite limits; do not hold the Performance queue while releasing WMI.
        () => FanCommandAsync(async () =>
        {
            if (_controller.Mode == AdaptiveFanProductionMode.Automatic) await SetFanModeAsync(AdaptiveFanProductionMode.Firmware);
        }),
        () => CommandAsync(async () => { await _performance.CloseAsync(); Publish("CPU/GPU liberados mediante Performance Guardian."); }));
    private Task CommandAsync(Func<Task> command) => RunCommandAsync(_commands,command);
    private Task FanCommandAsync(Func<Task> command) => RunCommandAsync(_fanCommands,command);
    private Task RunCommandAsync(SemaphoreSlim domain,Func<Task> command) => Task.Run(async () =>
    {
        await domain.WaitAsync();
        try { if (_closing) throw new ObjectDisposedException(nameof(ProductRuntime)); lock (_stateSync) _state = _state with { Failure = null }; await command(); }
        catch (Exception ex) { Publish("No se pudo completar la operación",ex.Message); throw; }
        finally { domain.Release(); }
    });
    public void FenceLifecycle(string reason)
    {
        _automaticActivation.Cancel();
        _lifecycleBlocked = true; _lifecycleBlockReason = reason; _fans.CloseCustomAdmissionForLifecycleBoundary(); _automaticReview?.Stop(); _worker.NotifySuspend(reason); Publish(reason);
    }
    public async Task ReleaseForLifecycleAsync(string reason)
    {
        try
        {
            await _fans.BlockCustomAdmissionAndRestoreAsync(reason,DateTimeOffset.UtcNow,CancellationToken.None);
            await _controller.ReleaseToFirmwareAsync(reason,CancellationToken.None); Publish(reason);
        }
        catch (Exception ex) { Publish("Liberación lifecycle no resuelta",ex.Message); }
    }
    private async Task ResetInterruptedFanAsync(string reason)
    {
        _automaticActivation.Cancel();
        try { _automaticReview?.Stop(); await _controller.ReleaseToFirmwareAsync(reason,CancellationToken.None); Publish(reason); }
        catch (Exception ex) { Publish("Recovery no resuelto",ex.Message); }
    }
    public void ResumeTelemetry(string reason) { _worker.NotifyResume(reason); Publish("Revalidando telemetría; autoridad de ventiladores permanece bloqueada."); }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000,_lifetime.Token);
                if (_automaticReview?.Expired == true)
                    await FanCommandAsync(async () =>
                    {
                        if (_automaticReview.Expired && _controller.Mode == AdaptiveFanProductionMode.Automatic)
                        {
                            _lifecycleBlocked = true; _lifecycleBlockReason = "Finalizó la prueba de cinco minutos. Reinicia tras una liberación limpia."; _fans.CloseCustomAdmissionForLifecycleBoundary(); _automaticReview.Stop();
                            await _controller.ReleaseToFirmwareAsync("Fin de los cinco minutos de prueba Automatic.",CancellationToken.None);
                            Publish("Prueba Automatic finalizada; Firmware solicitado. Reinicia para otra sesión.");
                        }
                    });
                if (_performance.HasProcess) await _performance.StatusAsync(); Publish();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { Publish("Estado Performance Guardian no disponible",ex.Message); }
        }
    }
    private void Publish(string? message = null, string? failure = null)
    {
        var source = "Unknown";
        try { var reading = new WindowsPerformancePowerSourceReader().Read(); if (reading.Succeeded) source = reading.Source.ToString(); } catch { }
        var p = _performance.LastStatusFresh ? _performance.LastStatus : null;
        ProductRuntimeState state;
        lock (_stateSync)
        {
            _state = _state with
            {
                Hardware = _hardware.SystemProductName + " (" + _hardware.BoardProduct + ")", Target = _target?.Id ?? "Unsupported",
                Source = source, Runtime = _worker.StateMachine.State.ToString(), Snapshot = _snapshot,
                FanMode = _controller.Mode.ToString(), FanAuthority = _fans.Authority.ToString(), FanLevel = _fans.Authority == FanAuthority.Custom ? _wmi?.LastAcceptedLevel : null,
                ManualAuthorized = _controller.ManualExecutionAuthorized, AutomaticAuthorized = _controller.AutomaticExecutionAuthorized,
                AutomaticReview = _automaticReview is not null, AutomaticReviewRemainingSeconds = _controller.Mode == AdaptiveFanProductionMode.Automatic ? _automaticReview?.RemainingSeconds : null,
                AutomaticCpuSpikeRemainingMilliseconds = _automaticReview?.RemainingCpuSpikeMilliseconds,
                AutomaticPreparing = _automaticActivation.Pending, AutomaticSessionId = _automaticSessionId, AutomaticDecisionSnapshot = _automaticDecisionSnapshot, AutomaticInterruptionSnapshot = _automaticInterruptionSnapshot,
                AutomaticDecision = _controller.Mode == AdaptiveFanProductionMode.Automatic ? _automaticDecision : null,
                AppliedAutomaticConfiguration = _controller.Mode == AdaptiveFanProductionMode.Automatic ? _controller.AutomaticConfiguration : null,
                PerformanceSupported = _target == Hp8C40TargetProfile.Instance,
                CanApplyPerformance = _target == Hp8C40TargetProfile.Instance && PerformanceAdmissionPermitted(_closing,_lifecycleBlocked,_performance.HasProcess,_worker.StateMachine.State,_fans.Authority),
                PerformanceActive = _performance.LimitsActive, PerformanceProcessPresent = _performance.HasProcess,
                CpuState = p?.CpuState ?? (_performance.HasProcess ? "Recovering" : "Disabled"), GpuState = p?.GpuState ?? (_performance.HasProcess ? "Recovering" : "Disabled"),
                CpuStatus = p?.CpuStatus, GpuStatus = p?.GpuStatus, AppliedPerformanceSource = p?.PowerSource ?? "Unknown", AppliedPerformance = _performance.AppliedConfiguration ?? (p is { CpuState: "Active" } or { GpuState: "ActiveUnverified" } ? _requestedPerformance : null),
                GuardianState = p?.RuntimeFailure is not null ? "Failed" : _performance.HasProcess ? p?.Phase ?? "Recovering" : "Sin sesión",
                AppliedFanProfile = _fans.Authority == FanAuthority.Custom ? _selectedFanProfile : null,
                LifecycleBlocked = _lifecycleBlocked, LifecycleBlockReason = _lifecycleBlockReason, Message = message ?? _state.Message, Failure = failure ?? _state.Failure
            }; state = _state;
        }
        Changed?.Invoke(state);
    }
    public async ValueTask DisposeAsync()
    {
        _automaticActivation.Cancel();
        _closing = true; _lifetime.Cancel(); _fans.CloseCustomAdmissionForLifecycleBoundary();
        var failures = new List<Exception>();
        // Each domain cleanup is attempted even if a preceding domain fails; retained journals are never deleted here.
        try { await _fanCommands.WaitAsync(); try { await _fans.DisposeAsync(); } finally { _fanCommands.Release(); } } catch (Exception ex) { failures.Add(ex); }
        try { await _performance.CloseAsync(); } catch (Exception ex) { failures.Add(ex); }
        try { await _worker.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
        if (_poll is not null) try { await _poll; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        if (failures.Count > 0) throw new AggregateException("Liberación incompleta. Conserva los journals y revisa los informes.",failures);
    }
}
