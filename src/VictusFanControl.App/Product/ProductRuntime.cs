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
    internal string Message { get; init; } = "Inicio en Firmware. Los límites requieren Aplicar.";
    internal string? Failure { get; init; }
    internal bool LifecycleBlocked { get; init; }
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
    private readonly HardwareIdentity _hardware;
    private readonly HardwareTargetProfile? _target;
    private readonly Hp8C40WmiFanControlBackend? _wmi;
    private readonly FanControlCoordinator _fans;
    private readonly AdaptiveFanProductionController _controller;
    private readonly Hp8C40ThermalEmergencyConfirmation _thermal = new();
    private readonly TelemetryWorker _worker;
    private readonly PerformanceGuardianClient _performance;
    private readonly SemaphoreSlim _commands = new(1,1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateSync = new();
    private ProductRuntimeState _state = new();
    private TelemetrySnapshot? _snapshot;
    private string? _selectedFanProfile;
    private PerformanceGuiSessionConfiguration? _requestedPerformance;
    private Task? _poll;
    private volatile bool _closing, _lifecycleBlocked;
    public event Action<ProductRuntimeState>? Changed;
    public ProductRuntimeState State { get { lock (_stateSync) return _state; } }

    internal ProductRuntime(string modules, ProductProfiles profiles)
    {
        _hardware = HardwareIdentityReader.ReadCurrent();
        _target = HpHardwareTargetResolver.Resolve(_hardware,out _);
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
            backend.CanWrite && Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized,
            automaticHardware: _target == Hp8C40TargetProfile.Instance ? _hardware : null,
            automaticConfiguration: profiles.Ac.Fan);
        _performance = new(modules);
        _worker = new(modules)
        {
            FreshFanAcquisitionRequired = () => _controller.AutomaticFreshAcquisitionRequired,
            AcquisitionBudgetMilliseconds = () => _controller.AutomaticAcquisitionBudgetMilliseconds,
            NormalPollingDelayMilliseconds = () => _controller.AutomaticNormalPollingDelayMilliseconds,
            SnapshotProcessor = ProcessAutomaticAsync
        };
        _fans.AuthorityChanged += (_, e) => { AppLog.Write("Product fan authority: " + e); Publish(e.Reason); };
        _worker.SnapshotAvailable += (_, snapshot) => { _snapshot = snapshot; _ = EnforceAsync(); Publish(); };
        _worker.StateMachine.StateChanged += (_, e) =>
        {
            if (!_closing && e.Current != SystemState.Healthy && _fans.Authority == FanAuthority.Custom)
            {
                _lifecycleBlocked = true; _fans.CloseCustomAdmissionForLifecycleBoundary();
                _ = ResetInterruptedFanAsync("Telemetría no disponible: " + e.Reason);
            }
            _ = EnforceAsync(); Publish(e.Reason);
        };
        _worker.EventLogged += (_, e) => AppLog.Write(e);
        Publish();
    }
    public void Start() { _worker.Start(); _poll = PollAsync(); }
    private SafetyGateResult Safety(bool control = true)
    {
        var s = control ? SafetyGate.Evaluate(_hardware, _worker.StateMachine.State, _snapshot, DateTimeOffset.UtcNow, _fans.BackendCanWrite) : SafetyGate.EvaluateForDisplay(_hardware, _worker.StateMachine.State, _snapshot, DateTimeOffset.UtcNow, _fans.BackendCanWrite);
        return _controller.Mode == AdaptiveFanProductionMode.Automatic
            ? _controller.EvaluateAutomaticSafety(_snapshot,s,observe:control)
            : control ? _thermal.Apply(_hardware,_snapshot,s) : _thermal.Preview(_hardware,_snapshot,s);
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
        _snapshot = snapshot;
        if (_closing || _lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Automatic) return;
        try
        {
            var source = new WindowsPerformancePowerSourceReader().Read();
            var expected = source.Source == PerformancePowerSourceKind.Ac ? "Ac" : source.Source == PerformancePowerSourceKind.Battery ? "Battery" : null;
            if (expected is null || _selectedFanProfile != expected)
            {
                _lifecycleBlocked = true; _fans.CloseCustomAdmissionForLifecycleBoundary();
                await _controller.ReleaseToFirmwareAsync("Cambio de fuente: volver a seleccionar Automatic con el perfil real.",token);
                Publish("La fuente cambió; ventiladores en Firmware. Requiere calificación de transición de curvas."); return;
            }
            var raw = SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
            await _controller.ProcessAutomaticAsync(snapshot,raw,token, refreshRawSafetyProvider: () =>
                ReferenceEquals(_snapshot,snapshot) ? SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite) : null);
            Publish();
        }
        catch (Exception ex) { Publish("Automatic interrumpido",ex.Message); }
    }
    public Task SelectFanModeAsync(AdaptiveFanProductionMode mode, ProductProfiles profiles) => CommandAsync(async () =>
    {
        if (mode != AdaptiveFanProductionMode.Firmware && _lifecycleBlocked)
            throw new InvalidOperationException("Sesión interrumpida. La reapertura tras lifecycle permanece cerrada; usa Firmware y reinicia después de una liberación limpia.");
        if (mode == AdaptiveFanProductionMode.Automatic)
        {
            if (!_controller.AutomaticExecutionAuthorized) throw new InvalidOperationException("Automatic está implementado, pero su gate normal sigue cerrado.");
            var source = new WindowsPerformancePowerSourceReader().Read().Source;
            if (source == PerformancePowerSourceKind.Unknown) throw new InvalidOperationException("Fuente real desconocida.");
            if (_controller.Mode != AdaptiveFanProductionMode.Firmware || _fans.Authority != FanAuthority.Firmware)
                throw new InvalidOperationException("Vuelve a Firmware antes de aplicar una curva.");
            var slot = source == PerformancePowerSourceKind.Ac ? ProductPowerProfile.Ac : ProductPowerProfile.Battery;
            await _controller.ConfigureAutomaticAsync(profiles.Get(slot).Fan,CancellationToken.None);
            _selectedFanProfile = slot.ToString();
        }
        var result = await _controller.SetModeAsync(mode,CancellationToken.None);
        if (!result.ExecutionAuthorized) throw new InvalidOperationException(result.Detail);
        if (mode != AdaptiveFanProductionMode.Automatic) _selectedFanProfile = null;
        Publish(result.Detail);
    });
    public Task ApplyManualAsync(int level) => CommandAsync(async () =>
    {
        if (_lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Manual) throw new InvalidOperationException("Selecciona Manual antes de aplicar; una sesión interrumpida no puede rearmarse.");
        var result = await _controller.ApplyManualAsync(level,Safety(),() => Safety(false),CancellationToken.None);
        if (!result.ExecutionAuthorized || result.Action == AdaptiveFanProductionActionKind.Blocked) throw new InvalidOperationException(result.Detail);
        Publish(result.Detail);
    });
    public Task ApplyPerformanceAsync(ProductProfiles profiles) => CommandAsync(async () =>
    {
        if (_target != Hp8C40TargetProfile.Instance || _lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Firmware || _fans.Authority != FanAuthority.Firmware || _worker.StateMachine.State != SystemState.Healthy)
            throw new InvalidOperationException("Aplicar rendimiento requiere el destino validado, telemetría Healthy y ventiladores en Firmware.");
        profiles.Validate(); var configuration = profiles.PerformanceConfiguration(); configuration.Validate();
        if (_performance.HasProcess) throw new InvalidOperationException("Libera la sesión CPU/GPU antes de cambiar su configuración.");
        _requestedPerformance = configuration; Publish("Aplicando CPU/GPU…"); await _performance.EnableAsync(configuration); Publish("Sesión de rendimiento aplicada. GPU: solicitud aceptada; rango independiente no verificable.");
    });
    public Task ReleasePerformanceAsync() => CommandAsync(async () => { await _performance.CloseAsync(); Publish("CPU/GPU liberados mediante Performance Guardian."); });
    private Task CommandAsync(Func<Task> command) => Task.Run(async () =>
    {
        await _commands.WaitAsync();
        try { if (_closing) throw new ObjectDisposedException(nameof(ProductRuntime)); lock (_stateSync) _state = _state with { Failure = null }; await command(); }
        catch (Exception ex) { Publish("No se pudo completar la operación",ex.Message); throw; }
        finally { _commands.Release(); }
    });
    public void FenceLifecycle(string reason)
    {
        _lifecycleBlocked = true; _fans.CloseCustomAdmissionForLifecycleBoundary(); _worker.NotifySuspend(reason); Publish(reason);
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
        try { await _controller.ReleaseToFirmwareAsync(reason,CancellationToken.None); Publish(reason); }
        catch (Exception ex) { Publish("Recovery no resuelto",ex.Message); }
    }
    public void ResumeTelemetry(string reason) { _worker.NotifyResume(reason); Publish("Revalidando telemetría; autoridad de ventiladores permanece bloqueada."); }
    private async Task PollAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try { await Task.Delay(2000,_lifetime.Token); if (_performance.HasProcess) await _performance.StatusAsync(); Publish(); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { Publish("Estado Performance Guardian no disponible",ex.Message); }
        }
    }
    private void Publish(string? message = null, string? failure = null)
    {
        var source = "Unknown";
        try { var reading = new WindowsPerformancePowerSourceReader().Read(); if (reading.Succeeded) source = reading.Source.ToString(); } catch { }
        var p = _performance.LastStatus;
        ProductRuntimeState state;
        lock (_stateSync)
        {
            _state = _state with
            {
                Hardware = _hardware.SystemProductName + " (" + _hardware.BoardProduct + ")", Target = _target?.Id ?? "Unsupported",
                Source = source, Runtime = _worker.StateMachine.State.ToString(), Snapshot = _snapshot,
                FanMode = _controller.Mode.ToString(), FanAuthority = _fans.Authority.ToString(), FanLevel = _wmi?.LastAcceptedLevel,
                ManualAuthorized = _controller.ManualExecutionAuthorized, AutomaticAuthorized = _controller.AutomaticExecutionAuthorized,
                PerformanceSupported = _target == Hp8C40TargetProfile.Instance,
                CanApplyPerformance = !_closing && !_lifecycleBlocked && !_performance.HasProcess && _controller.Mode == AdaptiveFanProductionMode.Firmware && _fans.Authority == FanAuthority.Firmware && _worker.StateMachine.State == SystemState.Healthy,
                PerformanceActive = _performance.LimitsActive, PerformanceProcessPresent = _performance.HasProcess,
                CpuState = p?.CpuState ?? (_performance.HasProcess ? "Recovering" : "Disabled"), GpuState = p?.GpuState ?? (_performance.HasProcess ? "Recovering" : "Disabled"),
                CpuStatus = p?.CpuStatus, GpuStatus = p?.GpuStatus, AppliedPerformanceSource = p?.PowerSource ?? "Unknown", AppliedPerformance = _performance.AppliedConfiguration ?? (p is { CpuState: "Active" } or { GpuState: "ActiveUnverified" } ? _requestedPerformance : null),
                GuardianState = p?.RuntimeFailure is not null ? "Failed" : _performance.HasProcess ? p?.Phase ?? "Recovering" : "Sin sesión",
                AppliedFanProfile = _fans.Authority == FanAuthority.Custom ? _selectedFanProfile : null,
                LifecycleBlocked = _lifecycleBlocked, Message = message ?? _state.Message, Failure = failure ?? _state.Failure
            }; state = _state;
        }
        Changed?.Invoke(state);
    }
    public async ValueTask DisposeAsync()
    {
        _closing = true; _lifetime.Cancel(); _fans.CloseCustomAdmissionForLifecycleBoundary();
        var failures = new List<Exception>();
        // Each domain cleanup is attempted even if a preceding domain fails; retained journals are never deleted here.
        try { await _commands.WaitAsync(); try { await _fans.DisposeAsync(); } finally { _commands.Release(); } } catch (Exception ex) { failures.Add(ex); }
        try { await _performance.CloseAsync(); } catch (Exception ex) { failures.Add(ex); }
        try { await _worker.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
        if (_poll is not null) try { await _poll; } catch (OperationCanceledException) { }
        _lifetime.Dispose();
        if (failures.Count > 0) throw new AggregateException("Liberación incompleta. Conserva los journals y revisa los informes.",failures);
    }
}
