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
    internal long PublicationSequence { get; init; }
    internal string Hardware { get; init; } = "Detectando equipo…";
    internal string Target { get; init; } = "Sin destino validado";
    internal string Source { get; init; } = "Unknown";
    internal string Runtime { get; init; } = "Initializing";
    internal string FanMode { get; init; } = "Firmware";
    internal string FanAuthority { get; init; } = "Firmware";
    internal int? FanLevel { get; init; }
    // Observation only: an explicit Manual Apply accepted in this mode, never a retained Automatic target.
    internal int? ManualAppliedLevel { get; init; }
    internal int ManualMaximumLevel { get; init; } = 50;
    internal bool FanUpperRangePreview { get; init; }
    internal int? CommandedCpuLevel { get; init; }
    internal int? CommandedGpuLevel { get; init; }
    internal bool ManualAuthorized { get; init; }
    internal bool AutomaticAuthorized { get; init; }
    internal bool AutomaticReview { get; init; }
    internal int? AutomaticReviewMaximumSeconds { get; init; }
    internal bool AutomaticPreparing { get; init; }
    internal ProductProtectionSettings? AppliedProtections { get; init; }
    internal ProductProfiles? AutomaticResumeProfiles { get; init; }
    internal bool AutomaticResumptionBlocked { get; init; }
    internal string? AutomaticSourceTransition { get; init; }
    internal TelemetrySnapshot? AutomaticInterruptionSnapshot { get; init; }
    internal string? AutomaticSessionId { get; init; }
    internal TelemetrySnapshot? AutomaticDecisionSnapshot { get; init; }
    internal int? AutomaticReviewRemainingSeconds { get; init; }
    internal int? AutomaticCpuSpikeRemainingMilliseconds { get; init; }
    internal AdaptiveFanProductionResult? AutomaticDecision { get; init; }
    internal FanConfiguration? AppliedAutomaticConfiguration { get; init; }
    internal bool PerformanceSupported { get; init; }
    internal bool CanApplyPerformance { get; init; }
    internal bool CanReleasePerformance {get;init;}
    internal bool PerformanceLifecycleBlocked {get;init;}
    internal bool PerformanceActive { get; init; }
    internal bool PerformanceProcessPresent { get; init; }
    internal bool PerformanceSessionConfirmed {get;init;}
    internal bool PerformanceUpdating { get; init; }
    internal PerformanceRecoveryPreview? PerformanceRecovery { get; init; }
    internal VictusFanControl.PlatformThermalReplay.ProductRetentionState? PlatformRetention { get; init; }
    internal string CpuState { get; init; } = "Disabled";
    internal string GpuState { get; init; } = "Disabled";
    internal string CtgpState { get; init; } = "Disabled";
    internal string? CtgpFailure { get; init; }
    internal CtgpStatus? CtgpObservation { get; init; }
    internal string? FanGuardianSessionDirectory { get; init; }
    internal string? LastFanGuardianReportPath { get; init; }
    internal bool CtgpFaulted { get; init; }
    internal bool CtgpIndependent {get;init;}
    internal ProductFunctionFlags FunctionRequests {get;init;}=new();
    internal ProductFunctionFlags FunctionFaults {get;init;}=new();
    internal ProductFunctionPolicies FunctionPolicies {get;init;}=new();
    internal bool FunctionCoordinationExplicit {get;init;}
    internal ProductFunctionFanMode? RequestedFanMode {get;init;}
    internal IReadOnlyList<ProductFunctionRetryState>? FunctionRetries {get;init;}
    internal bool MaintenanceActive {get;init;}
    internal bool SharedWmiRecoveryPending {get;init;}
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
    internal string? InterruptionDetails => !LifecycleBlocked ? null :
        (LifecycleBlockReason ?? "Sesión interrumpida; revisar el diagnóstico.") +
        $"\n\nEstado actual: ventiladores {FanMode} · {FanAuthority}; CPU {CpuState}; GPU {GpuState}." +
        (AutomaticInterruptionSnapshot is { } s
            ? $"\nMuestra del disparo ({s.Timestamp:O}): CPU {s.CpuControlTemperatureC:0.##} °C; GPU {s.GpuTemperatureC:0.##} °C."
            : "") +
        (AutomaticResumptionBlocked?"\nLa actualización CPU/GPU no tuvo respuesta confirmada. La reanudación desatendida quedó cancelada; requiere recuperación explícita.":"")+
        "\n\nEn uso habitual, pulsa Automático para preparar un reintento manual en esta ventana, después de liberar los controles y verificar sensores nuevos. Si quedan registros pendientes, resuelve la recuperación. Reiniciar sesión sigue disponible: guarda un diagnóstico, libera los controles y abre una sesión nueva en Firmware. Si la liberación falla, no abre otra sesión y conserva los registros. También puedes usar Salir desde la bandeja.";
}

internal interface IProductRuntime : IAsyncDisposable
{
    event Action<ProductRuntimeState>? Changed;
    ProductRuntimeState State { get; }
    void Start();
    void RequestAutomaticTelemetry();
    Task SelectFanModeAsync(AdaptiveFanProductionMode mode, ProductProfiles profiles);
    Task ApplyFanCurveAsync(ProductPowerProfile source, UnifiedFanDemand demand);
    Task ApplyFanTuningAsync(AdaptiveFanTuning tuning);
    Task ApplyManualAsync(int level);
    Task ApplyPerformanceAsync(ProductProfiles profiles);
    Task ReleasePerformanceAsync();
    Task SetPerformanceDomainAsync(bool cpu,bool enabled,ProductProfiles profiles)
    {
        var selected=State.AppliedPerformance??profiles.PerformanceConfiguration() with{CpuEnabled=false,GpuEnabled=false};
        var values=profiles.PerformanceConfiguration();
        var next=cpu?selected with{CpuEnabled=enabled,AcPl1Watts=values.AcPl1Watts,AcPl2Watts=values.AcPl2Watts,
            BatteryPl1Watts=values.BatteryPl1Watts,BatteryPl2Watts=values.BatteryPl2Watts}:
            selected with{GpuEnabled=enabled,AcGpuMaximumMHz=values.AcGpuMaximumMHz,BatteryGpuMaximumMHz=values.BatteryGpuMaximumMHz};
        return ApplyPerformanceAsync(ProductPerformanceUpdate.WithPerformance(profiles,next));
    }
    Task SetCtgpAsync(bool enabled)=>Task.FromException(new NotSupportedException("cTGP no disponible."));
    Task ConfigureFunctionsAsync(ProductFunctionPolicies policies)=>Task.CompletedTask;
    Task SetExternalCoolingRequestedAsync(bool enabled)=>Task.CompletedTask;
    Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles profiles,ProductFunctionEntryReason reason)=>SelectFanModeAsync(mode,profiles);
    void FenceLifecycle(string reason);
    void FenceFanRetry(string reason)=>FenceLifecycle(reason);
    Task ReleaseForLifecycleAsync(string reason);
    void ResumeTelemetry(string reason);
    Task CompleteMaintenanceAsync()=>Task.CompletedTask;
}

internal interface IProductRecoveryHandoff
{
    Guid? DrainedGpuSession { get; }
    void CompleteRecovery(Guid session);
}

/// <summary>Presentation adapter. Owns no raw hardware commands; uses the existing controllers and IPC.</summary>
internal sealed partial class ProductRuntime : IProductRuntime, IProductRecoveryHandoff, IProductLateReadDrain, IProductFanRetry
{
    private readonly ProductAutomaticReview? _automaticReview;
    private readonly ProductAutomaticReview _automaticGuard;
    private readonly ProductAutomaticActivation _automaticActivation = new();
    private readonly ProductAutomaticSourceTransition _sourceTransition = new();
    private readonly HardwareIdentity _hardware;
    private readonly HardwareTargetProfile? _target;
    private readonly Hp8C40WmiFanControlBackend? _wmi;
    private readonly FanControlCoordinator _fans;
    private readonly AdaptiveFanProductionController _controller;
    private readonly Hp8C40ThermalEmergencyConfirmation _thermal = new();
    private readonly TelemetryWorker _worker;
    private readonly PerformanceGuardianClient _performance;
    private readonly ProductPerformanceLifetime _performanceLifetime;
    private readonly Func<PerformanceRetainedJournals> _retainedJournalReader;
    private volatile bool _ownsPerformanceLifetime;
    private readonly object _disposalSync=new();
    private Task? _localDisposal,_fullDisposal;
    private bool _localDrainConfirmed;
    private PerformanceRetainedSession? _retainedPerformance;
    private readonly RecoveryPreviewCache _recoveryPreview = new();
    private readonly RecoveryPreviewCache _performanceRecoveryPreview=new(performanceOnly:true);
    private readonly Func<PerformanceRecoveryPreview> _performanceRecoveryInventory;
    private readonly IPerformancePowerSourceReader _powerSource;
    private readonly Func<PerformanceRecoveryPreview> _recoveryInventory;
    private readonly WmiFanGuiGuardianClient? _fanGuardian;
    private readonly ProductWmiLifetime? _wmiLifetime;
    private volatile bool _ownsWmiLifetime;
    private ProductFunctionPolicies _functionPolicies;
    private ProductFunctionFlags _functionRequests;
    private ProductFunctionFlags _functionFaults=new();
    private ProductFunctionFanMode _functionFanMode;
    private bool _functionCoordinationExplicit;
    private ProductProfiles _latestFunctionProfiles;
    private bool _maintenanceActive;
    private int _reconcilingFunctions;
    private bool _ctgpRequested;
    private bool _ctgpFaulted;
    private bool _automaticResumptionBlocked;
    private bool _releaseCompleted;
    private WmiFanReadDrainPendingException? _lateReadDrain;
    bool IProductLateReadDrain.HasPendingReadDrain => _lateReadDrain is not null;
    async Task IProductLateReadDrain.ConfirmReadDrainAsync()
    {
        var pending = _lateReadDrain ?? throw new IOException("No hay una lectura WMI pendiente de confirmar.");
        await pending.ConfirmAsync();
        _releaseCompleted = true;
        _localDrainConfirmed = true;
        _lateReadDrain = null;
    }
    private Guid? _drainedGpuSession;
    public Guid? DrainedGpuSession => _drainedGpuSession;
    public void CompleteRecovery(Guid session)
    {
        if (session == Guid.Empty || _drainedGpuSession != session)
            throw new IOException("No existe un cierre exclusivo de esa sesión GPU.");
        _releaseCompleted = true;
        _drainedGpuSession = null;
    }
    private int _retryBoundaryClaimed;
    private readonly SemaphoreSlim _commands = new(1,1);
    private readonly SemaphoreSlim _fanCommands = new(1,1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateSync = new();
    private ProductRuntimeState _state = new();
    private readonly LatestValuePump<ProductRuntimeState> _presentation;
    private long _publicationSequence;
    private TelemetrySnapshot? _snapshot;
    private string? _selectedFanProfile;
    private int _manualAppliedLevel=-1;
    private string? _lifecycleBlockReason;
    private AdaptiveFanProductionResult? _automaticDecision;
    private TelemetrySnapshot? _automaticInterruptionSnapshot;
    private TelemetrySnapshot? _automaticDecisionSnapshot;
    private string? _automaticSessionId;
    private PerformanceGuiSessionConfiguration? _automaticPerformance;
    private ProductAutomaticActivation.Ticket? _activeAutomaticTicket;
    private ProductProfiles? _frozenProfiles;
    private ProductProfiles? _automaticProfiles
    {
        get=>Volatile.Read(ref _frozenProfiles);
        set=>Volatile.Write(ref _frozenProfiles,value is null?null:ProductProfilesStore.Freeze(value));
    }
    private DateTimeOffset _automaticStartedUtc;
    private volatile bool _plannedFanRelease;
    private PerformanceGuiSessionConfiguration? _requestedPerformance;
    private Task? _poll;
    private Task? _thermalDeadline;
    private volatile bool _closing, _lifecycleBlocked;
    private volatile bool _performanceLifecycleBlocked;
    private volatile bool _performanceUpdating;
    private DateTimeOffset _performanceAdmissionAfterUtc=DateTimeOffset.MinValue;
    private long _performanceUpdateStarted;
    public event Action<ProductRuntimeState>? Changed;
    public ProductRuntimeState State { get { lock (_stateSync) return _state; } }

    internal ProductRuntime(string modules, ProductProfiles profiles, ProductAutomaticReviewMode? automaticReview = null,
        VictusFanControl.PlatformThermalReplay.PhysicalPlatformExperiment? platformExperiment = null, ProductRuntime? releasedRuntime = null,
        ProductRuntimeDependencies? dependencies = null,bool preservePerformanceFromReleasedRuntime=false,string? fanControlUnavailableReason=null)
    {
        _presentation=new(state=>
        {
            if(_closing)return;
            var observers=Changed;if(observers is null)return;
            foreach(Action<ProductRuntimeState> observer in observers.GetInvocationList())
                try{if(!_closing)observer(state);}catch(Exception error){AppLog.Write("Presentation observer failed: "+error.GetType().Name);}
        },state=>state.PublicationSequence);
        // Reject the retired physical entry before identity reads, IPC or hardware construction.
        if(platformExperiment is not null)
            throw new NotSupportedException("TZ01/DTT3 y la retención térmica experimental se han retirado del producto.");
        if(dependencies is null)WmiOnlyInvestigationPolicy.Enable(); // Product telemetry never opens an EC session.
        _hardware = dependencies?.Hardware ?? HardwareIdentityReader.ReadCurrent();
        _powerSource=dependencies?.PowerSource??new WindowsPerformancePowerSourceReader();
        _recoveryInventory=dependencies?.RecoveryInventory??_recoveryPreview.Get;
        _performanceRecoveryInventory=dependencies?.RecoveryInventory??_performanceRecoveryPreview.Get;
        _target = HpHardwareTargetResolver.Resolve(_hardware,out _);
        var retainedOwner=preservePerformanceFromReleasedRuntime?releasedRuntime??throw new ArgumentException("Falta el controlador liberado para conservar CPU/GPU."):null;
        _functionPolicies=retainedOwner?._functionPolicies??profiles.FunctionPolicies();
        _functionCoordinationExplicit=retainedOwner?._functionCoordinationExplicit??profiles.Functions is not null;
        _latestFunctionProfiles=retainedOwner?._latestFunctionProfiles??ProductProfilesStore.Freeze(profiles);
        _functionRequests=releasedRuntime?._functionRequests??ProductFunctionCoordination.ApplicationStart(_functionPolicies);
        _functionFaults=retainedOwner?._functionFaults??new();
        _functionFanMode=retainedOwner?._functionFanMode??ProductFunctionFanMode.Firmware;
        if(retainedOwner is not null){_functionRetries=retainedOwner._functionRetries;FunctionRecoveryClock=retainedOwner.FunctionRecoveryClock;}
        if(automaticReview.HasValue)_functionPolicies=_functionPolicies with{ContinueLinkedFunctionsDuringFanRecovery=false,StopAutomaticOnPerformanceFailure=true,StopAutomaticOnCtgpFailure=true};
        if(retainedOwner is not null&&retainedOwner._target!=_target)throw new IOException("La identidad cambió antes de transferir los controles.");
        if (automaticReview.HasValue)
        {
            if (!ProductAutomaticReview.IsAuthorized(true, _target?.Id)) throw new InvalidOperationException("Prueba Automatic disponible solo para el HP 8C40/F.18 validado.");
            _automaticReview = new(mode: automaticReview.Value);
            _state = _state with { Message = $"Prueba Automatic habilitada: 10–50, máximo {_automaticReview.MaximumDurationSeconds / 60} min. Inicio en Firmware; requiere clic explícito." };
        }
        _automaticGuard=_automaticReview??new(mode:ProductAutomaticReviewMode.Habitual);
        IFanControlBackend backend;
        try
        {
            if(dependencies is not null)
            {
                backend=dependencies.FanBackend;_wmi=backend as Hp8C40WmiFanControlBackend;
                _wmiLifetime=dependencies.WmiLifetime;_fanGuardian=_wmiLifetime?.Client;
                _ownsWmiLifetime=_wmiLifetime is not null&&retainedOwner is null;
            }
            else if (_target == Hp8C40TargetProfile.Instance)
            {
                _wmiLifetime=retainedOwner?._wmiLifetime??new ProductWmiLifetime(new WmiFanGuiGuardianClient(productUpperRange:true,deferBoundary:true),_powerSource);
                _fanGuardian=_wmiLifetime.Client;_ownsWmiLifetime=retainedOwner is null;
                _wmi = new(_wmiLifetime.FanLease,productUpperRange:true,sharedGuardian:true); backend = _wmi;
                _wmi.CommandAccepted += (_, message) => AppLog.Write("PRODUCT WMI REQUEST ACCEPTED: " + message);
            }
            else backend = new DisabledFanControlBackend();
        }
        catch (Exception ex) { if(releasedRuntime is not null)throw;backend = new DisabledFanControlBackend(); AppLog.Write("Product backend unavailable: " + ex); }
        if(fanControlUnavailableReason is not null)
        {
            backend=new DisabledFanControlBackend();_wmi=null;
            _lifecycleBlocked=true;_lifecycleBlockReason=fanControlUnavailableReason;
            AppLog.Write("Independent services retained with fan control unavailable: "+fanControlUnavailableReason);
        }
        _fans = new(backend);
        _controller = ProductFanControllerFactory.Create(_fans,profiles,_hardware,_target,
            _automaticReview is not null,ProductFanRangePreview.Enabled);
        _performanceLifetime=retainedOwner?.RetainedPerformanceForConstruction()??new(dependencies?.Performance??new(modules));
        _ownsPerformanceLifetime=retainedOwner is null;
        _performance=_performanceLifetime.Client;
        _retainedJournalReader=dependencies?.RetainedJournals??PerformanceRetainedJournals.Read;
        if(dependencies is not null&&!ReferenceEquals(dependencies.Performance,_performance))
            throw new ArgumentException("Las dependencias no corresponden al propietario CPU/GPU conservado.");
        _worker = dependencies?.Telemetry??new(modules)
        {
            ProductTelemetryTolerance = automaticReview is null && _target == Hp8C40TargetProfile.Instance
        };
        _worker.FreshFanAcquisitionRequired = () => _controller.AutomaticFreshAcquisitionRequired;
        _worker.AcquisitionBudgetMilliseconds = () => ProductAutomaticReview.AcquisitionBudget(
            _controller.AutomaticAcquisitionBudgetMilliseconds, _automaticGuard.RemainingCpuSpikeMilliseconds);
        _worker.NormalPollingDelayMilliseconds = () => _controller.AutomaticNormalPollingDelayMilliseconds;
        _worker.SnapshotProcessor = ProcessAutomaticAsync;
        if (_wmi is not null && automaticReview is null) _wmi.ProductTelemetryProvider = () => _snapshot;
        _fans.AuthorityChanged += (_, e) => { AppLog.Write("Product fan authority: " + e); Publish(e.Reason); };
        _worker.SnapshotAvailable += (_, snapshot) => { if(!ObserveSnapshot(snapshot))return; AppLog.WriteTelemetry(snapshot); _ = EnforceAsync(); Publish(); };
        _worker.StateMachine.StateChanged += (_, e) =>
        {
            if (!_closing && e.Current != SystemState.Healthy && (_automaticActivation.Pending || _fans.Authority == FanAuthority.Custom || _controller.Mode == AdaptiveFanProductionMode.Automatic))
            {
                _automaticActivation.Cancel();
                var reason = _automaticGuard.RemainingCpuSpikeMilliseconds == 0
                    ? ProductAutomaticReview.CpuSpikeDeadlineFailure : "Telemetría no disponible: " + e.Reason;
                _automaticInterruptionSnapshot = _snapshot;
                _lifecycleBlocked = true; _lifecycleBlockReason = reason; _fans.CloseCustomAdmissionForLifecycleBoundary();
                _ = ResetInterruptedFanAsync(reason);
                AppLog.Write("PRODUCT AUTOMATIC INTERRUPTED: " + _automaticSessionId + " · " + reason);
            }
            _ = EnforceAsync(); Publish(e.Reason);
        };
        _worker.EventLogged += (_, e) => AppLog.Write(e);
        Publish();
        // Commit process-local WMI ownership only after all preparation that
        // may fail. No controller/telemetry/native worker has started yet.
        void CommitFanBoundary(){if(_fanGuardian is not null&&retainedOwner?._wmiLifetime is null)CommitReleasedBoundary(releasedRuntime,_fanGuardian.ActivateBoundary);}
        if(retainedOwner is not null)retainedOwner.TransferPerformanceTo(this,CommitFanBoundary);
        else CommitFanBoundary();
        if(dependencies is null)WmiFanExperimentBoundary.PlannedGuiRelease = () => _plannedFanRelease;
        _performanceLifetime.Changed+=PerformanceChanged;
        if(_wmiLifetime is not null)
        {
            UpdateWmiBinding();
            _wmiLifetime.CanRestartAfterFailure=()=>!_closing&&!_maintenanceActive&&!_performanceLifecycleBlocked&&_fans.Authority==FanAuthority.Firmware;
            _wmiLifetime.Changed+=WmiChanged;
        }
    }
    internal static void CommitReleasedBoundary(ProductRuntime? releasedRuntime,Action<string?> activate,Func<string?>? currentBoundaryFixture=null)
    {
        var releasedBoundary=releasedRuntime?.ClaimReleasedBoundary();
        try { activate(releasedBoundary); }
        catch
        {
            // Retry only before ownership changed; never reset a committed or
            // uncertain boundary just because a constructor threw.
            if(releasedRuntime is not null && string.Equals(releasedBoundary,currentBoundaryFixture?.Invoke()??WmiFanExperimentBoundary.SessionDirectory,StringComparison.OrdinalIgnoreCase))
                Interlocked.CompareExchange(ref releasedRuntime._retryBoundaryClaimed,0,1);
            throw;
        }
    }
    private string ClaimReleasedBoundary()
    {
        if(!_releaseCompleted||_fanGuardian is null||Interlocked.CompareExchange(ref _retryBoundaryClaimed,1,0)!=0)
            throw new IOException("El controlador anterior no tiene una liberación completa disponible para reintentar.");
        return _fanGuardian.SessionDirectory;
    }
    public void Start() { _worker.Start(); _performanceLifetime.Start(); _poll = PollAsync(); _thermalDeadline = MonitorThermalDeadlineAsync(); }
    private void PerformanceChanged(Exception? error)
    {if(!_closing)Publish(error is null?null:"Estado Performance Guardian no disponible",error?.Message);}
    private void WmiChanged(){if(!_closing)Publish();}
    public void RequestAutomaticTelemetry() => _worker.RequestAutomaticTelemetry();
    private bool ObserveSnapshot(TelemetrySnapshot snapshot)
    {
        while(true)
        {
            var previous=Volatile.Read(ref _snapshot);
            var now=DateTimeOffset.UtcNow;
            if(snapshot.Timestamp>now)return false;
            // A corrected wall clock must not pin the previous future timestamp.
            if(previous is not null&&previous.Timestamp<=now&&snapshot.Timestamp<previous.Timestamp)return false;
            if(ReferenceEquals(Interlocked.CompareExchange(ref _snapshot,snapshot,previous),previous))return true;
        }
    }
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
            if(!ObserveSnapshot(snapshot))return;
            if (_closing || _lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Automatic || snapshot.Timestamp <= _automaticStartedUtc) return;
            if (_activeAutomaticTicket is null || !_automaticActivation.IsCurrent(_activeAutomaticTicket)) return;
            try
            {
                var source = _powerSource.Read();
                var expected = source.Source == PerformancePowerSourceKind.Ac ? "Ac" : source.Source == PerformancePowerSourceKind.Battery ? "Battery" : null;
                var raw = SafetyGate.Evaluate(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                // Use the shared bounded 8C40 admission, rather than rejecting the
                // unchanged raw SafetyGate's first CPU >=95 C sample here.
                if (snapshot.RetainedTelemetry is not null)
                {
                    _automaticGuard.EnsureDispatchAllowed(snapshot);
                    if (snapshot.CpuControlTemperatureC >= 85 || snapshot.GpuTemperatureC >= 78)
                        throw new InvalidOperationException("Lectura necesaria retrasada con temperatura elevada; volver a Firmware sin agotar los 5 s.");
                    if (!_controller.EvaluateAutomaticSafety(snapshot,raw,observe:true).CustomControlPermitted)
                        throw new InvalidOperationException("Sensores necesarios fuera de su tolerancia; volver a Firmware.");
                    await _controller.ObserveAutomaticSourceWaitAsync(snapshot, () =>
                    {
                        _automaticActivation.EnsureCurrent(_activeAutomaticTicket!);
                        _automaticGuard.EnsureDispatchAllowed(snapshot);
                        if(!snapshot.HasFreshControlSensorsAt(DateTimeOffset.UtcNow)||_closing||_lifecycleBlocked)
                            throw new InvalidOperationException("La tolerancia de sensores venció durante la espera.");
                    },token);
                    _automaticDecision=null;_automaticDecisionSnapshot=null;
                    Publish("Lectura retrasada: "+snapshot.RetainedTelemetry+". Se conserva el nivel sin descensos ni nuevas escrituras.");
                    return;
                }
                var effective = _controller.EvaluateAutomaticSafety(snapshot, raw, observe: true);
                var wasCpuPending = _automaticGuard.RemainingCpuSpikeMilliseconds.HasValue == true;
                if (!_automaticGuard.Observe(snapshot,effective))
                {
                    Publish("Prueba Automatic: verificando tres adquisiciones Healthy antes de controlar."); return;
                }
                var cpuPending = _automaticGuard.RemainingCpuSpikeMilliseconds.HasValue == true;
                if (cpuPending != wasCpuPending)
                    AppLog.Write("PRODUCT AUTOMATIC CPU SPIKE: " + System.Text.Json.JsonSerializer.Serialize(new
                    { automaticSessionId = _automaticSessionId, snapshotTimestamp = snapshot.Timestamp,
                        cpuControlTemperatureC = snapshot.CpuControlTemperatureC, pending = cpuPending,
                        remainingMilliseconds = _automaticGuard.RemainingCpuSpikeMilliseconds }));
                if (_automaticPerformance is null || _selectedFanProfile is null || _automaticProfiles is null)
                    throw new InvalidOperationException("Falta la configuración de la sesión Automatic.");
                if (_performanceUpdating&&_functionPolicies.StopAutomaticOnPerformanceFailure)
                {
                    ProductPerformanceUpdate.EnsureWaitAllowed(_performanceUpdateStarted,Environment.TickCount64,expected??"Unknown",
                        _selectedFanProfile,snapshot.CpuControlTemperatureC,snapshot.GpuTemperatureC);
                    await _controller.ObserveAutomaticSourceWaitAsync(snapshot, () =>
                    {
                        _automaticActivation.EnsureCurrent(_activeAutomaticTicket!);
                        if (_closing || _lifecycleBlocked) throw new InvalidOperationException("Actualización interrumpida por lifecycle.");
                        _automaticGuard.EnsureDispatchAllowed(snapshot);
                    }, token);
                    _automaticDecision=null;_automaticDecisionSnapshot=null;
                    Publish("Actualizando CPU/GPU; se conserva el nivel y continúan las lecturas sin nuevos comandos de ventiladores.");
                    return;
                }
                var wasTransition = _sourceTransition.Pending;
                var status = _performance.LastStatus;
                var reconciled = _sourceTransition.Observe(expected ?? "Unknown", _selectedFanProfile, _automaticPerformance,
                    _performance.AppliedConfiguration, status, _performance.LastStatusFresh,AutomaticPerformanceCoupled);
                if (!wasTransition && _sourceTransition.Pending)
                    AppLog.Write("PRODUCT AUTOMATIC SOURCE TRANSITION STARTED: " + System.Text.Json.JsonSerializer.Serialize(new
                    { automaticSessionId = _automaticSessionId, from = _selectedFanProfile, to = expected,
                        snapshotTimestamp = snapshot.Timestamp, guardian = status, remainingSeconds = _automaticReview?.RemainingSeconds }));
                void AdmitSource(bool requireMatchedLimits)
                {
                    if (!_automaticActivation.IsCurrent(_activeAutomaticTicket!))
                        throw new OperationCanceledException("Transición cancelada por la selección de Firmware o la liberación de límites.");
                    if (_closing || _lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Automatic ||
                        _powerSource.Read().Source.ToString() != expected)
                        throw new InvalidOperationException("Fuente o sesión cambiaron durante la transición Automatic.");
                    if (requireMatchedLimits && _functionPolicies.StopAutomaticOnPerformanceFailure&&!AutomaticPerformanceReady(_automaticPerformance,
                        _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, expected!))
                        throw new InvalidOperationException("CPU/GPU no confirmaron el perfil de destino.");
                    var currentStatus = _performance.LastStatus;
                    if (!requireMatchedLimits && _functionPolicies.StopAutomaticOnPerformanceFailure&&!AutomaticPerformanceReady(_automaticPerformance,
                        _performance.AppliedConfiguration,currentStatus,_performance.LastStatusFresh,currentStatus?.PowerSource ?? "Unknown"))
                        throw new InvalidOperationException("CPU/GPU perdieron su confirmación durante la espera de fuente.");
                    var currentRaw = SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                    if (!_controller.EvaluateAutomaticSafety(snapshot,currentRaw,observe:false).CustomControlPermitted)
                        throw new InvalidOperationException("Seguridad no permite continuar la transición.");
                    _automaticGuard.EnsureDispatchAllowed(snapshot);
                }
                if (!reconciled)
                {
                    if (snapshot.CpuControlTemperatureC >= 85 || snapshot.GpuTemperatureC > ProductProtectionSettings.HabitualGpuHandoffC)
                        throw new InvalidOperationException("Transición pendiente con demanda térmica urgente; volver a Firmware.");
                    await _controller.ObserveAutomaticSourceWaitAsync(snapshot, () => AdmitSource(false), token);
                    _automaticDecision = null; _automaticDecisionSnapshot = null;
                    AppLog.Write("PRODUCT AUTOMATIC SOURCE TRANSITION WAIT: " + System.Text.Json.JsonSerializer.Serialize(new
                    { automaticSessionId = _automaticSessionId, from = _selectedFanProfile, to = expected,
                        snapshotTimestamp = snapshot.Timestamp, guardian = status, fanWrites = false }));
                    Publish("Transición " + expected + ": esperando CPU/GPU; se conserva el nivel sin nuevas escrituras."); return;
                }
                if (_sourceTransition.Pending)
                {
                    AdmitSource(true);
                    var from = _selectedFanProfile;
                    var profile = _automaticProfiles.Get(Enum.Parse<ProductPowerProfile>(expected!)).Fan;
                    if (from != expected)
                        await _controller.ApplyUnifiedDemandAsync(profile.UnifiedDemand ?? throw new InvalidOperationException("Falta curva única de destino."),
                            () => AdmitSource(true), token, sourceConfiguration: profile);
                    _selectedFanProfile = expected;
                    await ReconcileAutomaticCtgpAsync();
                    _sourceTransition.Reset(); _automaticDecision = null; _automaticDecisionSnapshot = null;
                    AppLog.Write("PRODUCT AUTOMATIC SOURCE TRANSITION CONFIRMED: " + System.Text.Json.JsonSerializer.Serialize(new
                    { automaticSessionId = _automaticSessionId, from, to = expected, snapshotTimestamp = snapshot.Timestamp,
                        guardian = _performance.LastStatus, performance = _automaticPerformance, fan = _controller.AutomaticConfiguration,
                        remainingSeconds = _automaticReview?.RemainingSeconds }));
                }
                if (_ctgpRequested && expected == "Ac"&&_functionPolicies.StopAutomaticOnCtgpFailure&&
                    FollowsCurrentMode(ProductFunction.Ctgp)&&_wmiLifetime?.CtgpChanging!=true&&
                    _fanGuardian?.Ctgp is not{State:"Waiting",Managed:false,Active:false,Failure:null})
                    EnsureCtgpConfirmation(_fanGuardian?.Ctgp, ref _ctgpFaulted);
                var decision = await _controller.ProcessAutomaticAsync(snapshot,raw,token, refreshRawSafetyProvider: () =>
                {
                    if (!ReferenceEquals(_snapshot,snapshot) || _closing || _lifecycleBlocked) return null;
                    // A voluntary Firmware click queues release after this in-flight fan operation.
                    // Do not turn that cancellation into a false thermal/lifecycle interruption.
                    if (_automaticPerformance is null || !AutomaticPerformanceReady(_automaticPerformance,
                        _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, _selectedFanProfile ?? "Unknown")) return null;
                    _automaticGuard.EnsureDispatchAllowed(snapshot);
                    var currentSource = _powerSource.Read().Source;
                    if (currentSource.ToString() != _selectedFanProfile) return null;
                    return SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                });
                _automaticDecision = decision; _automaticDecisionSnapshot = snapshot;
                AppLog.Write("PRODUCT AUTOMATIC DECISION: " + System.Text.Json.JsonSerializer.Serialize(new { automaticSessionId = _automaticSessionId, snapshotTimestamp = snapshot.Timestamp, decision }));
                if (decision.Action == AdaptiveFanProductionActionKind.Blocked || decision.Action == AdaptiveFanProductionActionKind.RestoreFirmware)
                    throw new InvalidOperationException(decision.Detail);
                Publish($"Automático · {_selectedFanProfile} · nivel {decision.EqualFanLevel?.ToString() ?? "—"}"+(_automaticReview is not null?$" · {_automaticReview.RemainingSeconds} s restantes.":" · uso habitual."));
            }
            catch (OperationCanceledException) when (!_closing && !_lifecycleBlocked &&
                _activeAutomaticTicket is { } ticket && !_automaticActivation.IsCurrent(ticket))
            {
                await SetFanModeAsync(AdaptiveFanProductionMode.Firmware);
                Publish("Transición cancelada; ventiladores en Firmware.");
            }
            catch (Exception ex)
            {
                // Expiry or missing telemetry still releases the old controller.
                // Only an explicit policy failure cancels previous Automatic intent.
                // Resumption must independently confirm full owner/native release.
                if (_ctgpRequested)
                    _ctgpFaulted = CaptureCtgpFault(_ctgpFaulted, _fanGuardian?.Ctgp, _wmi?.ReleaseEvidence);
                AppLog.WriteStructured("PRODUCT AUTOMATIC INTERRUPTION EVIDENCE",new
                {
                    automaticSessionId=_automaticSessionId,selectedProfile=_selectedFanProfile,
                    transitionCandidate=_sourceTransition.Candidate,transitionPending=_sourceTransition.Pending,
                    guardianFresh=_performance.LastStatusFresh,guardian=_performance.LastStatus,
                    performance=_performance.AppliedConfiguration,snapshot,error=ex.Message,
                    ctgp=_fanGuardian?.CtgpDiagnostic(),ctgpRequiresManualRecovery=_ctgpFaulted
                });
                _automaticInterruptionSnapshot = snapshot; AppLog.Write("PRODUCT AUTOMATIC INTERRUPTED: " + _automaticSessionId + " · " + ex.Message); _automaticActivation.Cancel();
                _lifecycleBlocked = true; _lifecycleBlockReason = ex.Message; _fans.CloseCustomAdmissionForLifecycleBoundary();
                _automaticGuard.Stop();
                var failure = ex.Message;
                try { await _controller.ReleaseToFirmwareAsync("Automatic interrumpido: " + ex.Message,CancellationToken.None); }
                catch (Exception release) { failure += "; liberación no resuelta: " + release.Message; }
                Publish("Automatic interrumpido; reiniciar después de una liberación limpia.",failure);
            }
        }
        finally { _fanCommands.Release(); }
    }
    public Task SelectFanModeAsync(AdaptiveFanProductionMode mode, ProductProfiles profiles)
        =>SelectFanModeAsync(mode,profiles,ProductFunctionEntryReason.User);
    public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles profiles,ProductFunctionEntryReason reason)
    {
        profiles.Validate();
        if(reason==ProductFunctionEntryReason.Maintenance)_maintenanceActive=true;
        if(mode==AdaptiveFanProductionMode.Firmware&&reason is ProductFunctionEntryReason.User or ProductFunctionEntryReason.ApplicationStartup)
            _functionFanMode=ProductFunctionFanMode.Firmware;
        if(!_functionCoordinationExplicit&&profiles.Functions is null)_functionPolicies=profiles.FunctionPolicies();
        var linked=ProductFunctionCoordination.LinkedStarts(_functionPolicies,(ProductFunctionFanMode)_controller.Mode,(ProductFunctionFanMode)mode,reason);
        var requests=UpdateFunctionRequests(current=>ProductFunctionCoordination.EnterFanMode(_functionPolicies,current,
            (ProductFunctionFanMode)_controller.Mode,(ProductFunctionFanMode)mode,reason));
        var desired=_latestFunctionProfiles.PerformanceConfiguration();var incoming=profiles.PerformanceConfiguration();
        if(linked.CpuLimits)desired=desired with{AcPl1Watts=incoming.AcPl1Watts,AcPl2Watts=incoming.AcPl2Watts,
            BatteryPl1Watts=incoming.BatteryPl1Watts,BatteryPl2Watts=incoming.BatteryPl2Watts};
        if(linked.GpuClocks)desired=desired with{AcGpuMaximumMHz=incoming.AcGpuMaximumMHz,BatteryGpuMaximumMHz=incoming.BatteryGpuMaximumMHz};
        desired=desired with{CpuEnabled=requests.CpuLimits,GpuEnabled=requests.GpuClocks};
        _latestFunctionProfiles=ProductProfilesStore.Freeze(ProductPerformanceUpdate.WithPerformance(_latestFunctionProfiles,desired));
        // Fence synchronously at the user's click, including a command not yet admitted by its queue.
        if (mode == AdaptiveFanProductionMode.Automatic)
        {
            if(_controller.Mode==mode&&!_lifecycleBlocked)return ActivateAutomaticAsync(profiles);
            // Explicit policies change only through Apply links. A legacy
            // profile still uses its CPU/GPU selection as the linked shortcut.
            UpdateWmiBinding();
            var selected=ProductPerformanceUpdate.WithPerformance(profiles,desired) with{CtgpEnabled=requests.Ctgp,
                Functions=_functionCoordinationExplicit?_functionPolicies:null};
            return ActivateAutomaticAsync(selected,linked);
        }
        _automaticActivation.Cancel();
        if(mode==AdaptiveFanProductionMode.Firmware)
            return FanCommandAsync(async()=>{await SetFanModeAsync(mode);if(!_maintenanceActive)await ReconcileCtgpAsync();});
        return CommandAsync(()=>FanCommandAsync(async()=>
        {_functionFaults=_functionFaults with{CpuLimits=_functionFaults.CpuLimits&&!linked.CpuLimits,GpuClocks=_functionFaults.GpuClocks&&!linked.GpuClocks};
            if(linked.CpuLimits)Retry(ProductFunction.CpuLimits).Cancel();if(linked.GpuClocks)Retry(ProductFunction.GpuClocks).Cancel();
            await SetFanModeAsync(mode);
            if(reason is ProductFunctionEntryReason.User or ProductFunctionEntryReason.ApplicationStartup)_functionFanMode=(ProductFunctionFanMode)mode;
            if(!_maintenanceActive)await ReconcileCtgpAsync();}));
    }
    internal Task SetExperimentalStageAsync(VictusFanControl.PlatformThermalReplay.ExperimentStage stage) =>
        throw new NotSupportedException("La prueba física TZ01/DTT3 se ha retirado del producto.");
    private Task ActivateAutomaticAsync(ProductProfiles profiles,ProductFunctionFlags? linked=null)
    {
        if (!_closing && !_lifecycleBlocked && _controller.Mode == AdaptiveFanProductionMode.Automatic)
        {
            Publish("Automatic ya está seleccionado. Volver a pulsarlo no reaplica límites ni renueva el plazo de revisión.");
            return Task.CompletedTask;
        }
        // Reject unavailable modes before a new generation could supersede a running session.
        if(_closing||_lifecycleBlocked||!_controller.AutomaticExecutionAuthorized||
            _controller.Mode!=AdaptiveFanProductionMode.Firmware||_fans.Authority!=FanAuthority.Firmware)
            AutomaticAdmission(_automaticReview is null ? profiles.Protections : new());
        var ticket = _automaticActivation.Begin(profiles);
        ProductPowerProfile? slot = null;
        var performancePreparationStarted=false;
        Publish("Preparando Automatic: primero CPU/GPU, después la curva. Firmware cancela la entrada.");
        return CommandAsync(async () =>
        {
            if(linked is not null)_functionFaults=_functionFaults with{CpuLimits=_functionFaults.CpuLimits&&!linked.CpuLimits,GpuClocks=_functionFaults.GpuClocks&&!linked.GpuClocks};
            if(linked?.CpuLimits==true)Retry(ProductFunction.CpuLimits).Cancel();if(linked?.GpuClocks==true)Retry(ProductFunction.GpuClocks).Cancel();
            try
            {
                await _automaticActivation.RunAsync(ticket,
                    () => FanCommandAsync(async () =>
                    {
                        await RefreshAutomaticAdmissionAsync(ticket);
                        slot = AutomaticAdmission(_automaticReview is null ? ticket.Profiles.Protections : new());
                        var protections = _automaticReview is null ? ticket.Profiles.Protections : new ProductProtectionSettings{HistoricalGpuThermalPolicy=true};
                        _controller.ConfigureAutomaticProtections(protections); _automaticGuard.Configure(protections);
                    }),
                    async () =>
                    {
                        _automaticActivation.EnsureCurrent(ticket);
                        if(!_functionPolicies.StopAutomaticOnPerformanceFailure)return;
                        var configuration = ticket.Performance;
                        if(configuration.CpuEnabled&&_functionFaults.CpuLimits||configuration.GpuEnabled&&_functionFaults.GpuClocks)
                            throw new InvalidOperationException("El dominio de rendimiento requiere un reintento propio antes de vincular Automático.");
                        performancePreparationStarted=true;
                        await ProductAutomaticActivation.PreparePerformanceAsync(configuration, _performance.HasProcess,
                            _performance.AppliedConfiguration, async () => { await _performance.StatusAsync(); },
                            async () => { _requestedPerformance = configuration; await _performance.EnableAsync(configuration); },
                            async () =>
                            {
                                await _performance.UpdateAsync(configuration,token=>FanCommandAsync(async()=>
                                {
                                    token.ThrowIfCancellationRequested();await RefreshAutomaticAdmissionAsync(ticket);
                                    AutomaticAdmission(_automaticReview is null?ticket.Profiles.Protections:new());
                                }));
                                _requestedPerformance=_performance.AppliedConfiguration;
                            });
                    },
                    () => FanCommandAsync(async () =>
                    {
                        await RefreshAutomaticAdmissionAsync(ticket);
                        var current = AutomaticAdmission(_automaticReview is null ? ticket.Profiles.Protections : new());
                        if (current != slot) throw new InvalidOperationException("La fuente cambió durante la preparación; selecciona Automatic de nuevo con la fuente estable.");
                        if (!AutomaticPerformanceReady(ticket.Performance, _performance.AppliedConfiguration,
                            _performance.LastStatus, _performance.LastStatusFresh, current.ToString()))
                            throw new InvalidOperationException("CPU/GPU no confirmaron la selección solicitada para la fuente real; Automatic permanece en Firmware.");
                        await _controller.ConfigureAutomaticAsync(ticket.Profiles.Get(current).Fan, CancellationToken.None);
                        // Configuration may await a controller lock; recheck cancellation before committing.
                        await RefreshAutomaticAdmissionAsync(ticket,onlyIfExpired:true);
                        if (AutomaticAdmission(_automaticReview is null ? ticket.Profiles.Protections : new()) != current || !AutomaticPerformanceReady(ticket.Performance,
                            _performance.AppliedConfiguration, _performance.LastStatus, _performance.LastStatusFresh, current.ToString()))
                            throw new InvalidOperationException("Fuente o estado CPU/GPU cambiaron durante la preparación; Automatic permanece en Firmware.");
                        _selectedFanProfile = current.ToString(); _automaticPerformance = ticket.Performance;
                        _activeAutomaticTicket = ticket;
                        _automaticProfiles = ProductProfilesStore.Copy(ticket.Profiles); _sourceTransition.Reset();
                        await SetFanModeAsync(AdaptiveFanProductionMode.Automatic);
                        _functionFanMode=ProductFunctionFanMode.Automatic;
                        try { await ReconcileAutomaticCtgpAsync(); }
                        catch { await SetFanModeAsync(AdaptiveFanProductionMode.Firmware);throw; }
                        if (!_automaticActivation.IsCurrent(ticket))
                        {
                            await SetFanModeAsync(AdaptiveFanProductionMode.Firmware);
                            _automaticActivation.EnsureCurrent(ticket);
                        }
                        AppLog.Write("PRODUCT AUTOMATIC ACTIVATED WITH PERFORMANCE: " + System.Text.Json.JsonSerializer.Serialize(new { automaticSessionId = _automaticSessionId, performance = ticket.Performance, fan = _controller.AutomaticConfiguration }));
                    }));
            }
            catch
            {
                if(performancePreparationStarted&&_automaticActivation.IsCurrent(ticket))_functionFaults=_functionFaults with{
                    CpuLimits=_functionFaults.CpuLimits||ticket.Profiles.CpuEnabled&&_performance.LastStatus?.CpuState!="Active",
                    GpuClocks=_functionFaults.GpuClocks||ticket.Profiles.GpuEnabled&&_performance.LastStatus?.GpuState!="ActiveUnverified"};
                throw;
            }
            finally { Publish(); }
        });
    }
    private bool AutomaticPerformanceCoupled=>_functionPolicies.StopAutomaticOnPerformanceFailure&&
        _automaticPerformance is{CpuEnabled:true} or{GpuEnabled:true};
    private bool AutomaticPerformanceReady(PerformanceGuiSessionConfiguration expected,PerformanceGuiSessionConfiguration? applied,PerformanceGuardianResponse? status,bool fresh,string source) =>
        source is "Ac" or "Battery" && (!_functionPolicies.StopAutomaticOnPerformanceFailure||
            !expected.CpuEnabled&&!expected.GpuEnabled||ProductAutomaticActivation.PerformanceReady(expected,applied,status,fresh,source));
    private async Task RefreshAutomaticAdmissionAsync(ProductAutomaticActivation.Ticket ticket,bool onlyIfExpired=false)
    {
        _automaticActivation.EnsureCurrent(ticket);
        if(!onlyIfExpired||_snapshot is null||!TelemetryWorker.FreshForAutomaticAdmission(_snapshot,DateTimeOffset.UtcNow))
        {
            var snapshot=await _worker.RefreshForAutomaticAdmissionAsync(_lifetime.Token);
            _automaticActivation.EnsureCurrent(ticket);
            ObserveSnapshot(snapshot);
        }
        _automaticActivation.EnsureCurrent(ticket);
    }
    private ProductPowerProfile AutomaticAdmission(ProductProtectionSettings? protections = null)
    {
        // One immutable acquisition and one instant explain exactly the check made here.
        var snapshot=_snapshot;var evaluatedAt=DateTimeOffset.UtcNow;var runtime=_worker.StateMachine.State;
        var settings=protections??new ProductProtectionSettings();
        var admission=settings.ApplyThermalPolicy(snapshot,SafetyGate.Evaluate(_hardware,runtime,snapshot,evaluatedAt,_fans.BackendCanWrite));
        var issues=ProductAutomaticAdmission.RuntimeIssues(snapshot,evaluatedAt,admission,settings,runtime.ToString()).ToList();
        if (_closing || _lifecycleBlocked) issues.Add(new("lifecycle", "Sesión interrumpida: "+(_lifecycleBlockReason??"cierre del controlador")+"."));
        if (!_controller.AutomaticExecutionAuthorized) issues.Add(new("authorization", "Automático no está autorizado para este equipo."));
        if (_controller.Mode != AdaptiveFanProductionMode.Firmware || _fans.Authority != FanAuthority.Firmware)
            issues.Add(new("authority", $"Se requiere Firmware antes de entrar: modo {_controller.Mode}, autoridad {_fans.Authority}."));
        PerformancePowerSourceKind? source=null;
        if(issues.Count==0)
        {
            source=_powerSource.Read().Source;
            if(source is not (PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery))
                issues.Add(new("source", "La fuente real de energía no está confirmada: "+source+"."));
        }
        if(issues.Count>0)
        {
            AppLog.WriteStructured("PRODUCT AUTOMATIC ADMISSION REJECTED",new {evaluatedAt,runtime,source,issues,admission,snapshot});
            throw new InvalidOperationException("Automático bloqueado: "+string.Join(" ",issues.Select(i=>i.Detail)));
        }
        return source switch { PerformancePowerSourceKind.Ac => ProductPowerProfile.Ac,
            PerformancePowerSourceKind.Battery => ProductPowerProfile.Battery, _ => throw new InvalidOperationException("La entrada requiere una fuente confirmada.") };
    }
    private async Task SetFanModeAsync(AdaptiveFanProductionMode mode)
    {
        Exception? ctgpReleaseFailure = null;
        if (mode != AdaptiveFanProductionMode.Automatic && _ctgpRequested && _wmiLifetime is not null&&
            _functionPolicies.Ctgp.Binding==ProductFunctionBinding.FollowAutomatic)
        {
            try { await _wmiLifetime.SetCtgpAsync(_functionRequests.Ctgp,false,CancellationToken.None); }
            catch (Exception ex) { ctgpReleaseFailure = ex; } // Fan restoration must still be attempted.
        }
        if(mode!=_controller.Mode)
        {
            _manualAppliedLevel=-1;
            lock(_stateSync)_state=_state with{ManualAppliedLevel=null};
        }
        if (mode != AdaptiveFanProductionMode.Firmware && _lifecycleBlocked)
            throw new InvalidOperationException("Sesión interrumpida. La reapertura tras lifecycle permanece cerrada; usa Firmware y reinicia después de una liberación limpia.");
        if (mode == AdaptiveFanProductionMode.Automatic) { _automaticSessionId = Guid.NewGuid().ToString("N"); _automaticDecision = null; _automaticDecisionSnapshot = null; _automaticInterruptionSnapshot = null; _automaticStartedUtc = DateTimeOffset.UtcNow; _automaticGuard.Start(); }
        _plannedFanRelease = mode == AdaptiveFanProductionMode.Firmware;
        AdaptiveFanProductionResult result;
        try { result = await _controller.SetModeAsync(mode,CancellationToken.None); }
        finally { _plannedFanRelease = false; }
        if (!result.ExecutionAuthorized) throw new InvalidOperationException(result.Detail);
        if (mode != AdaptiveFanProductionMode.Automatic) { _selectedFanProfile = null; _automaticPerformance = null; _activeAutomaticTicket = null; _automaticProfiles = null; _sourceTransition.Reset(); _automaticGuard.Stop(); }
        Publish(FanDecisionMessage(result));
        if (ctgpReleaseFailure is not null) throw new IOException("cTGP no confirmó su liberación; se intentó también restaurar ventiladores.", ctgpReleaseFailure);
    }
    private async Task ConfigureCtgpAsync(bool selected, string source, CancellationToken token)
    {
        if (!selected && !_ctgpRequested) return;
        if (_wmiLifetime is null || _fanGuardian is null || _controller.Mode != AdaptiveFanProductionMode.Automatic)
            throw new InvalidOperationException("cTGP requiere Automático y el guardián WMI del HP 8C40.");
        try { await _wmiLifetime.SetCtgpAsync(selected,true,token); }
        catch { _ctgpFaulted = true; throw; }
        _ctgpRequested = selected;
        AppLog.WriteStructured("PRODUCT CTGP SELECTION", new { selected, source, status = _fanGuardian.Ctgp });
    }
    internal static void EnsureCtgpConfirmation(CtgpStatus? status, ref bool policyFault)
    {
        policyFault = CaptureCtgpFault(policyFault, status);
        if (status is not { State: "Active", Active: true, Managed: true, Failure: null } ||
            !status.FreshAt(DateTimeOffset.UtcNow))
            throw new InvalidOperationException("cTGP perdió su confirmación vigente; volver a Firmware.");
    }
    internal static bool CaptureCtgpFault(bool alreadyFaulted, CtgpStatus? observation, FanWmiReleaseEvidence? released = null) =>
        alreadyFaulted || observation?.RequiresManualRecovery == true || released?.SupervisionCtgp?.RequiresManualRecovery == true;
    internal static string CtgpPresentationState(bool faulted, bool released, AdaptiveFanProductionMode mode,
        bool requested, string source, CtgpStatus? status,bool independent=false) => faulted ? released ? "Faulted" : "RecoveryRequired" :
        status?.Failure is not null ? "RecoveryRequired" : !independent&&mode != AdaptiveFanProductionMode.Automatic || !requested ? "Disabled" :
        source != "Ac" ? "AcOnly" : status?.WaitingReason is not null ? "Waiting" : status?.State ?? "Unknown";
    public Task ApplyFanCurveAsync(ProductPowerProfile source, UnifiedFanDemand demand)
    {
        demand.Validate();var frozen=demand with { Curve=demand.Curve.ToArray() };
        var ticket=_activeAutomaticTicket;
        return FanCommandAsync(async () =>
        {
            void Admit()
            {
                if(ticket is null)throw new InvalidOperationException("Selecciona Automatic antes de aplicar una curva.");
                _automaticActivation.EnsureCurrent(ticket);
                if(_closing||_lifecycleBlocked||_controller.Mode!=AdaptiveFanProductionMode.Automatic||_fans.Authority!=FanAuthority.Custom||
                    _sourceTransition.Pending||!Enum.IsDefined(source)||_selectedFanProfile!=source.ToString()||
                    _powerSource.Read().Source.ToString()!=source.ToString())
                    throw new InvalidOperationException("Aplicar requiere Automatic activo y el perfil de la fuente real.");
                if(_automaticPerformance is null||!AutomaticPerformanceReady(_automaticPerformance,
                    _performance.AppliedConfiguration,_performance.LastStatus,_performance.LastStatusFresh,source.ToString()))
                    throw new InvalidOperationException("CPU/GPU deben conservar su confirmación vigente para aplicar la curva.");
                var snapshot=_snapshot??throw new InvalidOperationException("Falta telemetría vigente.");
                var raw=SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
                if(!_controller.EvaluateAutomaticSafety(snapshot,raw,observe:false).CustomControlPermitted)
                    throw new InvalidOperationException("La seguridad actual no permite aplicar la curva.");
                _automaticGuard.EnsureDispatchAllowed(snapshot);
            }
            Admit();
            await _controller.ApplyUnifiedDemandAsync(frozen,Admit,CancellationToken.None);
            var sessionProfiles = _automaticProfiles ?? throw new InvalidOperationException("Faltan perfiles de la sesión.");
            var updated = sessionProfiles.Get(source) with { Fan = FanConfigurationStore.Copy(_controller.AutomaticConfiguration!) };
            _automaticProfiles = source == ProductPowerProfile.Ac ? sessionProfiles with { Ac = updated } : sessionProfiles with { Battery = updated };
            _automaticDecision=null;_automaticDecisionSnapshot=null;
            AppLog.Write("PRODUCT AUTOMATIC CURVE APPLIED: "+System.Text.Json.JsonSerializer.Serialize(new
                {automaticSessionId=_automaticSessionId,source=source.ToString(),remainingSeconds=_automaticReview?.RemainingSeconds,demand=frozen}));
            Publish("Curva e influencias aplicadas; conservan la inercia y el plazo de la sesión. Guardar conserva los cambios para el próximo inicio.");
        });
    }
    public Task ApplyFanTuningAsync(AdaptiveFanTuning tuning) => FanCommandAsync(async () =>
    {
        tuning.Validate();
        void Admit()
        {
            if (_closing || _lifecycleBlocked || _target != Hp8C40TargetProfile.Instance || _worker.StateMachine.State != SystemState.Healthy ||
                _automaticActivation.Pending || _sourceTransition.Pending)
                throw new InvalidOperationException("Ajustes requieren el destino validado, Healthy y ninguna recuperación o transición pendiente.");
            var snapshot = _snapshot ?? throw new InvalidOperationException("Falta telemetría vigente.");
            if (CpuDemandTemperature.Select(snapshot,tuning.CpuTemperatureSource,tuning.HottestPerformanceCoreCount) is null)
                throw new InvalidOperationException("La fuente CPU o el número N no dispone de núcleos completos compatibles.");
            var raw = SafetyGate.EvaluateForDisplay(_hardware,_worker.StateMachine.State,snapshot,DateTimeOffset.UtcNow,_fans.BackendCanWrite);
            if (_controller.Mode == AdaptiveFanProductionMode.Automatic)
            {
                if (_activeAutomaticTicket is null) throw new InvalidOperationException("Falta la sesión Automatic.");
                _automaticActivation.EnsureCurrent(_activeAutomaticTicket);
                if (_automaticPerformance is null || !AutomaticPerformanceReady(_automaticPerformance,
                    _performance.AppliedConfiguration,_performance.LastStatus,_performance.LastStatusFresh,
                    _powerSource.Read().Source.ToString()) ||
                    !_controller.EvaluateAutomaticSafety(snapshot,raw,observe:false).CustomControlPermitted)
                    throw new InvalidOperationException("La seguridad o CPU/GPU no permiten aplicar ajustes ahora.");
                _automaticGuard.EnsureDispatchAllowed(snapshot);
            }
            else if (_controller.Mode != AdaptiveFanProductionMode.Firmware || _fans.Authority != FanAuthority.Firmware || !raw.CustomControlPermitted)
                throw new InvalidOperationException("Preparar ajustes requiere Firmware y telemetría segura y vigente.");
        }
        Admit();
        if (_controller.Mode == AdaptiveFanProductionMode.Automatic)
        {
            var profiles = _automaticProfiles ?? throw new InvalidOperationException("Faltan perfiles de la sesión.");
            await _controller.ApplyTuningAsync(tuning,Admit,CancellationToken.None);
            _automaticProfiles = profiles with { Ac = profiles.Ac with { Fan = profiles.Ac.Fan with { Tuning = tuning } },
                Battery = profiles.Battery with { Fan = profiles.Battery.Fan with { Tuning = tuning } } };
        }
        else
        {
            var configuration = _controller.AutomaticConfiguration ?? throw new InvalidOperationException("Falta configuración del motor.");
            await _controller.ConfigureAutomaticAsync(configuration with { Tuning = tuning },CancellationToken.None);
        }
        _automaticDecision=null;_automaticDecisionSnapshot=null;
        AppLog.Write("PRODUCT AUTOMATIC TUNING APPLIED: "+System.Text.Json.JsonSerializer.Serialize(new
            { automaticSessionId=_automaticSessionId,mode=_controller.Mode.ToString(),remainingSeconds=_automaticReview?.RemainingSeconds,tuning }));
        Publish("Ajustes actualizados. Se conserva el filtro; las confirmaciones reinician y una nueva definición de carga reinicia su historial.");
    });
    public Task ApplyManualAsync(int level) => FanCommandAsync(async () =>
    {
        if (_lifecycleBlocked || _controller.Mode != AdaptiveFanProductionMode.Manual) throw new InvalidOperationException("Selecciona Manual antes de aplicar; una sesión interrumpida no puede rearmarse.");
        _manualAppliedLevel=-1;
        lock(_stateSync)_state=_state with{ManualAppliedLevel=null};
        var result = await _controller.ApplyManualAsync(level,Safety(),() => Safety(),CancellationToken.None);
        if (!result.ExecutionAuthorized || result.Action == AdaptiveFanProductionActionKind.Blocked) throw new InvalidOperationException(result.Detail);
        if(result.Authority==FanAuthority.Custom&&result.EqualFanLevel==level&&_wmi?.LastAcceptedLevel==level)
            _manualAppliedLevel=level;
        Publish(FanDecisionMessage(result));
    });
    private string FanDecisionMessage(AdaptiveFanProductionResult result)
    {
        if(result.EqualFanLevel is {} level && _fans.Authority==FanAuthority.Custom && _wmi?.LastAcceptedLevel==level &&
            _wmi.LastAcceptedTargets is {} pair && pair.Cpu!=pair.Gpu)
            return $"Comando aceptado · nivel {level}: CPU {pair.Cpu} / GPU {pair.Gpu}.";
        return result.Detail;
    }
    internal static bool PerformanceAdmissionPermitted(bool closing,bool lifecycleBlocked,bool hasProcess,SystemState runtime,FanAuthority authority) =>
        !closing && !lifecycleBlocked && !hasProcess && runtime is not(SystemState.Suspended or SystemState.Suspending or SystemState.Resuming);
    private bool PerformancePhaseReady(PerformanceGuiSessionConfiguration? before,PerformanceGuiSessionConfiguration next)
    {
        if(_closing||_performanceLifecycleBlocked||_maintenanceActive||_worker.StateMachine.State is not(SystemState.Healthy or SystemState.Degraded))return false;
        if(_performanceAdmissionAfterUtc==DateTimeOffset.MinValue)return true;
        if(_snapshot is not{} s||s.Timestamp<=_performanceAdmissionAfterUtc)return false;
        var changed=ProductPerformanceUpdate.ChangedActiveDomains(before,next);
        bool Current(DateTimeOffset? at)=>(at??s.Timestamp)>_performanceAdmissionAfterUtc;
        return (!changed.Cpu||Current(s.CpuThermalSampledAtUtc)&&Current(s.CpuPowerSampledAtUtc))&&
            (!changed.Gpu||Current(s.GpuThermalSampledAtUtc)&&Current(s.GpuPowerSampledAtUtc));
    }
    public Task ApplyPerformanceAsync(ProductProfiles profiles) => CommandAsync(async () =>
    {
        profiles.Validate();
        UpdateFunctionRequests(current=>current with{CpuLimits=profiles.CpuEnabled,GpuClocks=profiles.GpuEnabled});
        profiles.Validate();_latestFunctionProfiles=ProductProfilesStore.Freeze(profiles);
        if(!PerformancePhaseReady(_performance.AppliedConfiguration,profiles.PerformanceConfiguration()))
        {Publish("Solicitud conservada; esperando validación posterior a la reanudación.");return;}
        _functionFaults=_functionFaults with{CpuLimits=false,GpuClocks=false};
        Retry(ProductFunction.CpuLimits).Cancel();Retry(ProductFunction.GpuClocks).Cancel();
        var before=_performance.AppliedConfiguration;
        var requested=profiles.PerformanceConfiguration() with
            {CpuEnabled=profiles.CpuEnabled&&FollowsCurrentMode(ProductFunction.CpuLimits),GpuEnabled=profiles.GpuEnabled&&FollowsCurrentMode(ProductFunction.GpuClocks)};
        try{await ApplyPerformanceConfigurationAsync(requested);}
        catch(Exception error){RecordRequestedPerformanceFailures(before,requested,error);throw;}
        finally{Publish();}
    });
    private async Task ApplyPerformanceConfigurationAsync(PerformanceGuiSessionConfiguration configuration)
    {
        configuration.Validate();
        if (_performance.HasProcess)
        {
            if(_performance.AppliedConfiguration is {} previous)
            {
                var releaseCpu=previous.CpuEnabled&&!configuration.CpuEnabled;
                var releaseGpu=previous.GpuEnabled&&!configuration.GpuEnabled;
                if(releaseCpu||releaseGpu)await ReleasePerformanceDomainsAsync(releaseCpu,releaseGpu,clearRequest:false);
                if(_performance.AppliedConfiguration==configuration)return;
            }
            await UpdatePerformanceAsync(configuration);
            return;
        }
        if (_target != Hp8C40TargetProfile.Instance || !PerformanceAdmissionPermitted(_closing,_performanceLifecycleBlocked,false,_worker.StateMachine.State,_fans.Authority) ||
            _automaticActivation.Pending||_powerSource.Read().Source is not(PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery)||
            !ProductPerformanceUpdate.HasIndependentTelemetry(_snapshot,DateTimeOffset.UtcNow,null,configuration))
            throw new InvalidOperationException("Aplicar requiere fuente confirmada, sensores propios vigentes y ninguna suspensión o cierre pendiente.");
        _requestedPerformance = configuration; Publish("Aplicando CPU/GPU…"); await _performance.EnableAsync(configuration);
        await FanCommandAsync(()=>
        {
            ReconcilePerformanceForResume();
            if(_controller.Mode==AdaptiveFanProductionMode.Automatic&&!_lifecycleBlocked&&_performance.AppliedConfiguration is{} committed)
                _automaticPerformance=committed;
            return Task.CompletedTask;
        });
        Publish("Sesión de rendimiento aplicada. GPU: solicitud aceptada; rango independiente no verificable.");
    }
    private async Task UpdatePerformanceAsync(PerformanceGuiSessionConfiguration configuration)
    {
        var started=false;
        try
        {
            await _performance.UpdateAsync(configuration, admissionToken=>FanCommandAsync(() =>
            {
                admissionToken.ThrowIfCancellationRequested();
                var source=_powerSource.Read().Source.ToString();
                if (!CanUpdatePerformance(source,configuration) || _performance.AppliedConfiguration is not { } old)
                    throw new InvalidOperationException("Actualizar requiere datos vigentes y una sesión CPU/GPU confirmada. Espera una fuente estable.");
                if (old==configuration) return Task.CompletedTask;
                if (_controller.Mode==AdaptiveFanProductionMode.Automatic&&!_lifecycleBlocked&&_functionPolicies.StopAutomaticOnPerformanceFailure)
                {
                    if (_activeAutomaticTicket is null) throw new InvalidOperationException("Falta la activación Automatic.");
                    _automaticActivation.EnsureCurrent(_activeAutomaticTicket);
                    _automaticGuard.EnsureDispatchAllowed(_snapshot!);
                    if (_snapshot!.CpuControlTemperatureC>=85 || _snapshot.GpuTemperatureC>ProductProtectionSettings.HabitualGpuHandoffC)
                        throw new InvalidOperationException("Espera a que termine la respuesta térmica urgente antes de actualizar límites.");
                }
                _performanceUpdateStarted=Environment.TickCount64;_performanceUpdating=true;started=true;
                AppLog.Write("PRODUCT PERFORMANCE UPDATE STARTED: "+System.Text.Json.JsonSerializer.Serialize(new
                    {automaticSessionId=_automaticSessionId,previous=old,requested=configuration,remainingSeconds=_automaticReview?.RemainingSeconds}));
                Publish("Actualizando CPU/GPU en la misma sesión…");
                return Task.CompletedTask;
            }));
            if(!started)return;
            await FanCommandAsync(() =>
            {
                ReconcilePerformanceForResume();
                if(_controller.Mode==AdaptiveFanProductionMode.Automatic&&!_lifecycleBlocked&&_functionPolicies.StopAutomaticOnPerformanceFailure)
                {
                    var source=_powerSource.Read().Source.ToString();
                    if(Environment.TickCount64-_performanceUpdateStarted>=4000 || _activeAutomaticTicket is null || !_automaticActivation.IsCurrent(_activeAutomaticTicket) ||
                        source!=_selectedFanProfile || !AutomaticPerformanceReady(configuration,
                            _performance.AppliedConfiguration,_performance.LastStatus,_performance.LastStatusFresh,source))
                        throw new InvalidOperationException("La fuente o activación cambió al actualizar CPU/GPU.");
                    _automaticGuard.EnsureDispatchAllowed(_snapshot!);
                    if(!Safety().CustomControlPermitted)throw new InvalidOperationException("La seguridad no permite continuar Automatic tras actualizar.");
                    _automaticPerformance=configuration;
                }
                _performanceUpdating=false;
                AppLog.Write("PRODUCT PERFORMANCE UPDATE APPLIED: "+System.Text.Json.JsonSerializer.Serialize(new
                    {automaticSessionId=_automaticSessionId,performance=_performance.AppliedConfiguration,guardian=_performance.LastStatus,
                        remainingSeconds=_automaticReview?.RemainingSeconds}));
                Publish("Límites actualizados y confirmados por dominio. Guardar conserva los valores para próximos inicios.");
                return Task.CompletedTask;
            });
        }
        catch(Exception ex)
        {
            if(!started)
            {
                Publish("No se inició la actualización CPU/GPU; la sesión anterior se conserva.",ex.Message);
                throw;
            }
            // Keep journals and independently committed values. Never fabricate rollback or renew Automatic.
            await FanCommandAsync(async () =>
            {
                await InterruptForPerformanceFailureAsync(ex);
                AppLog.Write("PRODUCT PERFORMANCE UPDATE FAILED: "+System.Text.Json.JsonSerializer.Serialize(new
                    {automaticSessionId=_automaticSessionId,performance=_performance.AppliedConfiguration,guardian=_performance.LastStatus,error=ex.Message}));
                Publish("Actualización incompleta; revisa el estado aplicado de cada dominio.",ex.Message);
            });
            throw;
        }
        finally { _performanceUpdating=false; }
    }
    private void ReconcilePerformanceForResume()
    {
        _requestedPerformance=_performance.AppliedConfiguration;
        if(_automaticProfiles is not {} frozen)return;
        if(_performance.AppliedConfiguration is {} committed && _performance.LastStatusFresh &&
            _performance.LastStatus is {SessionEnabled:true,Configuration:{} observed} status && observed==committed &&
            status.CpuEnabled==committed.CpuEnabled && status.GpuEnabled==committed.GpuEnabled)
            _automaticProfiles=ProductPerformanceUpdate.WithPerformance(frozen,committed);
        else if(_functionPolicies.StopAutomaticOnPerformanceFailure)
            // An unanswered update may already have changed either domain. Old
            // presets are not proof of rollback and must not drive unattended resume.
            _automaticResumptionBlocked=true;
    }
    private bool CanUpdatePerformance(string source,PerformanceGuiSessionConfiguration? next=null) => !_closing&&!_performanceLifecycleBlocked&&!_performanceUpdating&&
        !_automaticActivation.Pending&&(_lifecycleBlocked||!_sourceTransition.Pending)&&_target==Hp8C40TargetProfile.Instance&&
        _worker.StateMachine.State is not(SystemState.Suspended or SystemState.Suspending or SystemState.Resuming)&&
        _performance.SessionAvailable&&_performance.LastStatus?.PowerSource==source&&source is "Ac" or "Battery"&&
        (next is null||ProductPerformanceUpdate.HasIndependentTelemetry(_snapshot,DateTimeOffset.UtcNow,_performance.AppliedConfiguration,next));
    public Task ReleasePerformanceAsync()=>CommandAsync(async()=>
    {
        UpdateFunctionRequests(current=>current with{CpuLimits=false,GpuClocks=false});
        Retry(ProductFunction.CpuLimits).Cancel();Retry(ProductFunction.GpuClocks).Cancel();
        if(!_performance.HasProcess){Publish("Solicitudes CPU/GPU canceladas; no había un controlador activo.");return;}
        await ReleasePerformanceDomainsAsync(true,true);
        Publish("Límites CPU/GPU desactivados. Se conserva el modo de ventiladores.");
    });
    public Task SetPerformanceDomainAsync(bool cpu,bool enabled,ProductProfiles profiles)=>CommandAsync(async()=>
    {
        var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
        if(enabled)profiles.Validate();
        UpdateFunctionRequests(current=>current.With(function,enabled));_functionFaults=_functionFaults.With(function,false);
        Retry(function).Cancel();
        if(!enabled){await ReleasePerformanceDomainsAsync(cpu,!cpu);return;}
        var requestedValues=profiles.PerformanceConfiguration();
        var pendingValues=_latestFunctionProfiles.PerformanceConfiguration();
        var domainValues=cpu?pendingValues with{AcPl1Watts=requestedValues.AcPl1Watts,AcPl2Watts=requestedValues.AcPl2Watts,
            BatteryPl1Watts=requestedValues.BatteryPl1Watts,BatteryPl2Watts=requestedValues.BatteryPl2Watts}:
            pendingValues with{AcGpuMaximumMHz=requestedValues.AcGpuMaximumMHz,BatteryGpuMaximumMHz=requestedValues.BatteryGpuMaximumMHz};
        _latestFunctionProfiles=ProductProfilesStore.Freeze(ProductPerformanceUpdate.WithPerformance(_latestFunctionProfiles,domainValues));
        if(_performanceLifecycleBlocked||_maintenanceActive)
            throw new InvalidOperationException("La suspensión o el mantenimiento bloquea nuevas activaciones; la solicitud se conserva sin escribir hardware.");
        if(!FollowsCurrentMode(function)){Publish("Solicitud guardada; la coordinación espera el modo de ventiladores seleccionado.");return;}
        var values=profiles.PerformanceConfiguration();
        var previous=_performance.AppliedConfiguration??values with{CpuEnabled=false,GpuEnabled=false};
        var next=cpu?previous with{CpuEnabled=true,AcPl1Watts=values.AcPl1Watts,AcPl2Watts=values.AcPl2Watts,
            BatteryPl1Watts=values.BatteryPl1Watts,BatteryPl2Watts=values.BatteryPl2Watts}:
            previous with{GpuEnabled=true,AcGpuMaximumMHz=values.AcGpuMaximumMHz,BatteryGpuMaximumMHz=values.BatteryGpuMaximumMHz};
        if(!PerformancePhaseReady(_performance.AppliedConfiguration,next)||_powerSource.Read().Source is not(PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery)||
            !ProductPerformanceUpdate.HasIndependentTelemetry(_snapshot,DateTimeOffset.UtcNow,_performance.AppliedConfiguration,next))
        {Publish("Solicitud conservada; esperando la fuente y los sensores propios de la función.");return;}
        try{await ApplyPerformanceConfigurationAsync(next);}catch(Exception error){RecordFunctionFailure(function,error);throw;}
        if(cpu?_performance.LastStatus?.CpuState!="Active":_performance.LastStatus?.GpuState!="ActiveUnverified")
            throw new InvalidOperationException("El dominio solicitado no confirmó actividad; libera o recupera su sesión antes de reactivarlo.");
    });
    private async Task ReleasePerformanceDomainsAsync(bool cpu,bool gpu,bool clearRequest=true)
    {
        if(clearRequest)UpdateFunctionRequests(current=>current with{CpuLimits=cpu?false:current.CpuLimits,GpuClocks=gpu?false:current.GpuClocks});
        if(!_performance.HasProcess){Publish("Solicitud cancelada; el dominio no estaba activo.");return;}
        _performanceUpdating=true;_performanceUpdateStarted=Environment.TickCount64;Publish("Liberando límites seleccionados…");
        try
        {
            await _performance.ReleaseDomainsAsync(cpu,gpu);
            await FanCommandAsync(()=>
            {
                ReconcilePerformanceForResume();
                if(_controller.Mode==AdaptiveFanProductionMode.Automatic&&!_lifecycleBlocked&&_performance.AppliedConfiguration is{} committed)
                    _automaticPerformance=committed;
                Publish("Límites seleccionados desactivados; los demás controles conservan su sesión.");
                return Task.CompletedTask;
            });
        }
        catch(Exception error)
        {
            foreach(var function in new[]{ProductFunction.CpuLimits,ProductFunction.GpuClocks})
                if(function==ProductFunction.CpuLimits?cpu:gpu)
                {RecordFunctionFailure(function,error);Retry(function).WaitForConfirmation("La retirada de esta función requiere confirmación: "+error.Message);}
            await FanCommandAsync(()=>InterruptForPerformanceFailureAsync(error));
            throw;
        }
        finally{_performanceUpdating=false;Publish();}
    }
    private async Task InterruptForPerformanceFailureAsync(Exception error)
    {
        ReconcilePerformanceForResume();_performanceUpdating=false;
        if(_controller.Mode==AdaptiveFanProductionMode.Automatic&&_functionPolicies.StopAutomaticOnPerformanceFailure)
        {
            _automaticInterruptionSnapshot=_snapshot;_automaticActivation.Cancel();_lifecycleBlocked=true;
            _lifecycleBlockReason="Cambio CPU/GPU no confirmado: "+error.Message;_fans.CloseCustomAdmissionForLifecycleBoundary();_automaticGuard.Stop();
            await _controller.ReleaseToFirmwareAsync(_lifecycleBlockReason,CancellationToken.None);
        }
        Publish("Cambio incompleto; revisa la confirmación de cada dominio. Los registros se conservan.",error.Message);
    }
    private void UpdateWmiBinding()
    {if(_wmiLifetime is not null)_wmiLifetime.KeepCtgpAfterFanRelease=_functionPolicies.ContinueLinkedFunctionsDuringFanRecovery||_functionPolicies.Ctgp.Binding is not(ProductFunctionBinding.FollowAutomatic or ProductFunctionBinding.FollowInternalFans);}
    private ProductFunctionFanMode FunctionFanMode=>_functionPolicies.ContinueLinkedFunctionsDuringFanRecovery?_functionFanMode:
        _lifecycleBlocked?ProductFunctionFanMode.Firmware:(ProductFunctionFanMode)_controller.Mode;
    private bool FollowsCurrentMode(ProductFunction function)=>_functionPolicies.Get(function).Binding switch
    {
        ProductFunctionBinding.FollowAutomatic=>FunctionFanMode==ProductFunctionFanMode.Automatic,
        ProductFunctionBinding.FollowInternalFans=>FunctionFanMode!=ProductFunctionFanMode.Firmware,
        _=>true
    };
    public Task ConfigureFunctionsAsync(ProductFunctionPolicies policies)=>CommandAsync(async()=>
    {
        policies.Validate();
        // Legacy following is an implicit ON intent. Preserve it on the first
        // explicit Apply; an explicit base OFF has already ended legacy mode.
        if(!_functionCoordinationExplicit)
            UpdateFunctionRequests(current=>current with{ExternalCooling=true});
        _functionPolicies=policies;_functionCoordinationExplicit=true;UpdateWmiBinding();
        if(_automaticProfiles is{} active)_automaticProfiles=active with{Functions=policies};
        try{await ReconcileFunctionsCoreAsync();}
        catch(Exception error){Publish("Reglas aplicadas; revisa el estado de cada función.",error.Message);throw new ProductFunctionPoliciesCommittedException(error);}
        finally
        {
            // Reconciliation can commit CPU/GPU before an unrelated cTGP
            // failure. Future source transitions must use the actual outcome.
            if(_controller.Mode==AdaptiveFanProductionMode.Automatic)
                _automaticPerformance=_performance.AppliedConfiguration??_latestFunctionProfiles.PerformanceConfiguration() with
                    {CpuEnabled=_functionRequests.CpuLimits&&FollowsCurrentMode(ProductFunction.CpuLimits),GpuEnabled=_functionRequests.GpuClocks&&FollowsCurrentMode(ProductFunction.GpuClocks)};
        }
        Publish("Coordinación aplicada; cada función conserva su solicitud y confirma su propio estado.");
    });
    public Task SetExternalCoolingRequestedAsync(bool enabled)=>CommandAsync(()=>
    {_functionCoordinationExplicit=true;UpdateFunctionRequests(current=>current with{ExternalCooling=enabled});Publish();return Task.CompletedTask;});
    public Task SetCtgpAsync(bool enabled)=>CommandAsync(async()=>
    {
        UpdateFunctionRequests(current=>current with{Ctgp=enabled});
        if(!enabled&&_wmiLifetime is not null)await _wmiLifetime.SetCtgpAsync(false,false,_lifetime.Token);
        await ReconcileCtgpAsync();
        if(!enabled&&(_fanGuardian?.Ctgp is{Active:false,Managed:false,Failure:null}||_fanGuardian?.HasCompletedRelease==true))_ctgpFaulted=false;
        Publish();
    });
    private async Task ReconcileCtgpAsync()
    {
        if(_wmiLifetime is null)
        {if(_functionRequests.Ctgp)throw new NotSupportedException("cTGP no está disponible para este controlador.");return;}
        _ctgpRequested=_functionRequests.Ctgp;
        var eligible=_ctgpRequested&&FollowsCurrentMode(ProductFunction.Ctgp)&&!_performanceLifecycleBlocked;
        if(_wmiLifetime.CtgpWanted!=_ctgpRequested||_wmiLifetime.CtgpEligible!=eligible)
            await _wmiLifetime.SetCtgpAsync(_ctgpRequested,eligible,_lifetime.Token);
    }
    private bool SharedWmiUncertain=>_fanGuardian is not null&&
        (File.Exists(Path.Combine(_fanGuardian.SessionDirectory,"native-uncertain.signal"))||
         File.Exists(Path.Combine(_fanGuardian.SessionDirectory,"native-inflight.json")));
    private async Task ReconcileAutomaticCtgpAsync()
    {
        try{await ReconcileCtgpAsync();}
        catch(Exception error)when(!_functionPolicies.StopAutomaticOnCtgpFailure&&!SharedWmiUncertain)
        { _ctgpFaulted=CaptureCtgpFault(_ctgpFaulted,_fanGuardian?.Ctgp,_wmi?.ReleaseEvidence)||_wmiLifetime?.CtgpFaulted==true;
            Publish("cTGP requiere revisión; se conserva Automático y los controles independientes.",error.Message); }
    }
    private async Task ReconcileFunctionsAsync()
    {
        if(_closing||_automaticActivation.Pending||Interlocked.Exchange(ref _reconcilingFunctions,1)!=0)return;
        try
        {
            await CommandAsync(ReconcileFunctionsCoreAsync);
        }
        catch(Exception error){if(!_closing)Publish("Función en espera o pendiente de recuperación.",error.Message);}
        finally{Interlocked.Exchange(ref _reconcilingFunctions,0);}
    }
    private async Task ReconcileFunctionsCoreAsync()
    {
        if(_maintenanceActive)return; // Shutdown cooling never acquires unrelated domains.
                foreach(var cpu in new[]{true,false})
                {
                    var function=cpu?ProductFunction.CpuLimits:ProductFunction.GpuClocks;
                    var wanted=_functionRequests.Get(function);var eligible=FollowsCurrentMode(function);
                    var applied=cpu?_performance.AppliedConfiguration?.CpuEnabled==true:_performance.AppliedConfiguration?.GpuEnabled==true;
                    ObservePerformanceFunctionFailure(function,applied);
                    Retry(function).ObserveHealthy(FunctionRecoveryClock(),wanted&&applied&&!_functionFaults.Get(function)&&_performance.LastStatusFresh&&
                        (cpu?_performance.LastStatus?.CpuState=="Active":_performance.LastStatus?.GpuState=="ActiveUnverified"));
                    if(wanted&&eligible&&_functionFaults.Get(function)&&!_performanceLifecycleBlocked)
                    {await TryRecoverPerformanceFunctionAsync(cpu);continue;}
                    if((!wanted||!eligible)&&Retry(function).State.NeedsConfirmation)continue;
                    if(applied&&(!wanted||!eligible))
                    {
                        await ReleasePerformanceDomainsAsync(cpu,!cpu,clearRequest:false);
                    }
                    else if(wanted&&eligible&&!applied&&!_performanceLifecycleBlocked&&!_functionFaults.Get(function))
                    {
                        var values=_latestFunctionProfiles.PerformanceConfiguration();
                        var candidate=_performance.AppliedConfiguration??values with{CpuEnabled=false,GpuEnabled=false};
                        candidate=cpu?candidate with{CpuEnabled=true,AcPl1Watts=values.AcPl1Watts,AcPl2Watts=values.AcPl2Watts,
                            BatteryPl1Watts=values.BatteryPl1Watts,BatteryPl2Watts=values.BatteryPl2Watts}:
                            candidate with{GpuEnabled=true,AcGpuMaximumMHz=values.AcGpuMaximumMHz,BatteryGpuMaximumMHz=values.BatteryGpuMaximumMHz};
                        if(PerformancePhaseReady(_performance.AppliedConfiguration,candidate)&&(!_performance.HasProcess||_performance.SessionAvailable)&&_powerSource.Read().Source is PerformancePowerSourceKind.Ac or PerformancePowerSourceKind.Battery&&
                            ProductPerformanceUpdate.HasIndependentTelemetry(_snapshot,DateTimeOffset.UtcNow,_performance.AppliedConfiguration,candidate))
                        {
                            try{await ApplyPerformanceConfigurationAsync(candidate);Retry(function).Succeeded(FunctionRecoveryClock());}
                            catch(Exception error){RecordFunctionFailure(function,error);Publish("Una función espera recuperación; las demás continúan.",error.Message);}
                        }
                    }
                }
        await ReconcileCtgpAsync();Publish();
    }
    private Task CommandAsync(Func<Task> command) => RunCommandAsync(_commands,command);
    private ProductFunctionFlags UpdateFunctionRequests(Func<ProductFunctionFlags,ProductFunctionFlags> change)
    {
        while(true)
        {
            var current=Volatile.Read(ref _functionRequests);var next=change(current);
            if(ReferenceEquals(Interlocked.CompareExchange(ref _functionRequests,next,current),current))return next;
        }
    }
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
        _performanceAdmissionAfterUtc=DateTimeOffset.UtcNow;
        _performanceLifecycleBlocked=true;
        _wmiLifetime?.FenceForLifecycle();
        _lifecycleBlocked = true; _lifecycleBlockReason = reason; _fans.CloseCustomAdmissionForLifecycleBoundary(); _automaticGuard.Stop(); _worker.NotifySuspend(reason); Publish(reason);
    }
    public void FenceFanRetry(string reason)
    {
        _automaticActivation.Cancel();_lifecycleBlocked=true;_lifecycleBlockReason=reason;
        _fans.CloseCustomAdmissionForLifecycleBoundary();_automaticGuard.Stop();_worker.NotifySuspend(reason);Publish(reason);
    }
    public async Task ReleaseForLifecycleAsync(string reason)
    {
        var failures=new List<Exception>();
        try
        {
            await _fans.BlockCustomAdmissionAndRestoreAsync(reason,DateTimeOffset.UtcNow,CancellationToken.None);
            await _controller.ReleaseToFirmwareAsync(reason,CancellationToken.None);
        }
        catch(Exception error){failures.Add(error);}
        // Independence from fan mode never implies independence from system
        // suspension. Withdraw cTGP here, rather than waiting for a poll after
        // Windows has already suspended the application.
        if(_wmiLifetime is not null)
            try{await _wmiLifetime.SetCtgpAsync(_functionRequests.Ctgp,false,CancellationToken.None);}
            catch(Exception error){failures.Add(error);}
        if(failures.Count>0)
        {
            var error=new AggregateException("Liberación lifecycle no resuelta.",failures);
            Publish("Liberación lifecycle no resuelta",error.Message);throw error;
        }
        Publish(reason);
    }
    private async Task ResetInterruptedFanAsync(string reason)
    {
        _automaticActivation.Cancel();
        try { _automaticGuard.Stop(); await _controller.ReleaseToFirmwareAsync(reason,CancellationToken.None); Publish(reason); }
        catch (Exception ex) { Publish("Recovery no resuelto",ex.Message); }
    }
    public void ResumeTelemetry(string reason) { _performanceAdmissionAfterUtc=DateTimeOffset.UtcNow;_worker.NotifyResume(reason);_wmiLifetime?.ResumeAfterLifecycle();_performanceLifecycleBlocked=false; Publish("Revalidando telemetría; autoridad de ventiladores permanece bloqueada."); }
    public Task CompleteMaintenanceAsync()=>CommandAsync(()=>
    {
        if(!_maintenanceActive)return Task.CompletedTask;
        if(_fans.Authority!=FanAuthority.Firmware||SharedWmiUncertain)throw new IOException("No se confirmó la retirada del mantenimiento.");
        _maintenanceActive=false;ResumeTelemetry("Mantenimiento cancelado y retirado; revalidar funciones independientes.");return Task.CompletedTask;
    });
    internal void SuppressApplicationStartup()
    {
        if(_poll is not null||_performance.HasProcess)throw new InvalidOperationException("El inicio ya está en marcha.");
        UpdateFunctionRequests(_=>new());Publish("Sesión restaurada en Firmware; las funciones requieren solicitud explícita.");
    }
    private async Task MonitorThermalDeadlineAsync()
    {
        try
        {
            while(!_lifetime.IsCancellationRequested)
            {
                await Task.Delay(100,_lifetime.Token);
                if(_automaticGuard.RemainingCpuSpikeMilliseconds!=0 || _lifecycleBlocked || _closing)continue;
                var reason=ProductAutomaticReview.CpuSpikeDeadlineFailure;
                _automaticActivation.Cancel();_lifecycleBlocked=true;_lifecycleBlockReason=reason;
                _automaticInterruptionSnapshot=_snapshot;_fans.CloseCustomAdmissionForLifecycleBoundary();
                AppLog.Write("PRODUCT AUTOMATIC INTERRUPTED: "+_automaticSessionId+" · "+reason);
                _ = ResetInterruptedFanAsync(reason);
                Publish(reason);
            }
        }
        catch(OperationCanceledException) when(_lifetime.IsCancellationRequested) { }
    }
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
                            _lifecycleBlocked = true; _lifecycleBlockReason = $"Finalizó la revisión de {_automaticReview.MaximumDurationSeconds / 60} minutos. Reinicia tras una liberación limpia."; _fans.CloseCustomAdmissionForLifecycleBoundary(); _automaticReview.Stop();
                            await _controller.ReleaseToFirmwareAsync($"Fin de los {_automaticReview.MaximumDurationSeconds / 60} minutos de revisión Automatic.",CancellationToken.None);
                            Publish("Prueba Automatic finalizada; Firmware solicitado. Reinicia para otra sesión.");
                        }
                    });
                await ReconcileFunctionsAsync();Publish();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { Publish("Estado Performance Guardian no disponible",ex.Message); }
        }
    }
    private void Publish(string? message = null, string? failure = null, bool clearFailure = false)
    {
        var source = "Unknown";
        try { var reading = _powerSource.Read(); if (reading.Succeeded) source = reading.Source.ToString(); } catch { }
        var p = _performance.LastStatusFresh ? _performance.LastStatus : null;
        var ctgp = _fanGuardian?.Ctgp;
        _ctgpFaulted = CaptureCtgpFault(_ctgpFaulted, ctgp, _wmi?.ReleaseEvidence);
        _ctgpFaulted|=_wmiLifetime?.CtgpFaulted==true;
        var recovery=_performance.HasProcess&&!_performance.HasFailedProcess ? null : _recoveryInventory();
        var performanceRecovery=_performance.HasProcess&&!_performance.HasFailedProcess?null:_performanceRecoveryInventory();
        ProductRuntimeState state;
        lock (_stateSync)
        {
            _state = _state with
            {
                PublicationSequence=++_publicationSequence,
                Hardware = _hardware.SystemProductName + " (" + _hardware.BoardProduct + ")", Target = _target?.Id ?? "Unsupported",
                Source = source, Runtime = _worker.StateMachine.State.ToString(), Snapshot = _snapshot,
                FanMode = _controller.Mode.ToString(), FanAuthority = _fans.Authority.ToString(), FanLevel = _fans.Authority == FanAuthority.Custom ? _wmi?.LastAcceptedLevel : null,
                ManualMaximumLevel = _controller.ManualMaximumLevel, FanUpperRangePreview = ProductFanRangePreview.Enabled,
                ManualAppliedLevel = _controller.Mode==AdaptiveFanProductionMode.Manual&&_manualAppliedLevel>=0?_manualAppliedLevel:null,
                CommandedCpuLevel = _fans.Authority == FanAuthority.Custom ? _wmi?.LastAcceptedTargets?.Cpu : null,
                CommandedGpuLevel = _fans.Authority == FanAuthority.Custom ? _wmi?.LastAcceptedTargets?.Gpu : null,
                ManualAuthorized = _controller.ManualExecutionAuthorized, AutomaticAuthorized = _controller.AutomaticExecutionAuthorized,
                AutomaticReview = _automaticReview is not null, AutomaticReviewMaximumSeconds = _automaticReview?.MaximumDurationSeconds, AutomaticReviewRemainingSeconds = _controller.Mode == AdaptiveFanProductionMode.Automatic ? _automaticReview?.RemainingSeconds : null,
                AutomaticCpuSpikeRemainingMilliseconds = _automaticGuard.RemainingCpuSpikeMilliseconds,
                AppliedProtections = _automaticProfiles is null ? null : _automaticReview is null ? _automaticProfiles.Protections : new ProductProtectionSettings { ResumeAutomatic = false },
                AutomaticResumeProfiles = _ctgpFaulted&&_functionPolicies.StopAutomaticOnCtgpFailure || _automaticResumptionBlocked ? null : _automaticProfiles,
                AutomaticResumptionBlocked = _automaticResumptionBlocked,
                AutomaticPreparing = _automaticActivation.Pending, AutomaticSessionId = _automaticSessionId, AutomaticDecisionSnapshot = _automaticDecisionSnapshot, AutomaticInterruptionSnapshot = _automaticInterruptionSnapshot,
                AutomaticSourceTransition = _sourceTransition.Pending ? _sourceTransition.Candidate : null,
                AutomaticDecision = _controller.Mode == AdaptiveFanProductionMode.Automatic && ReferenceEquals(_automaticDecision,_controller.LastAutomaticResult) ? _automaticDecision : null,
                AppliedAutomaticConfiguration = _controller.Mode == AdaptiveFanProductionMode.Automatic ? _controller.AppliedConfigurationSnapshot : null,
                PerformanceSupported = _target == Hp8C40TargetProfile.Instance,
                PerformanceRecovery = recovery,
                CanApplyPerformance = performanceRecovery?.Pending!=true&&!_performanceUpdating&&!_automaticActivation.Pending&&
                    (_target == Hp8C40TargetProfile.Instance&&source is "Ac" or "Battery"&&PerformanceAdmissionPermitted(_closing,_performanceLifecycleBlocked,_performance.HasProcess,_worker.StateMachine.State,_fans.Authority) || CanUpdatePerformance(source)),
                CanReleasePerformance=!_closing&&!_performanceUpdating&&_performance.OwnerRunning,
                PerformanceLifecycleBlocked=_performanceLifecycleBlocked,
                PerformanceActive = _performance.LimitsActive&&_performance.AppliedConfiguration is{ } limits&&(limits.CpuEnabled||limits.GpuEnabled), PerformanceProcessPresent = _performance.HasProcess,
                PerformanceSessionConfirmed=_performance.LimitsActive&&_performance.LastStatus is{} confirmed&&confirmed.Configuration==_performance.AppliedConfiguration&&confirmed.PowerSource==source&&
                    PerformanceRetainedSession.MatchesJournals(confirmed,new(confirmed.CpuSession,confirmed.GpuSession)),
                PerformanceUpdating = _performanceUpdating,
                PlatformRetention = null,
                CpuState = p?.CpuState ?? (_performance.HasProcess || recovery?.Pending==true ? "Recovering" : "Disabled"), GpuState = p?.GpuState ?? (_performance.HasProcess || recovery?.Pending==true ? "Recovering" : "Disabled"),
                CtgpState = _wmiLifetime?.RecoveryState.Pending==true?"Recovering":_wmiLifetime is null&&_functionRequests.Ctgp?"Unsupported":CtgpPresentationState(_ctgpFaulted,_wmi?.ReleaseEvidence?.GuardianLeaseRetired==true,_controller.Mode,_ctgpRequested,source,ctgp,
                    _wmiLifetime?.KeepCtgpAfterFanRelease==true||_functionPolicies.Ctgp.Binding==ProductFunctionBinding.FollowInternalFans&&_controller.Mode==AdaptiveFanProductionMode.Manual),
                CtgpFailure = ctgp?.Failure ?? (_ctgpFaulted ? _wmi?.ReleaseEvidence?.SupervisionCtgp?.Failure : null),
                CtgpObservation = ctgp, FanGuardianSessionDirectory = _fanGuardian?.SessionDirectory,
                LastFanGuardianReportPath = _fanGuardian?.LastReleasedReportPath,
                CtgpFaulted = _ctgpFaulted,
                CtgpIndependent=!_functionPolicies.StopAutomaticOnCtgpFailure,
                FunctionRequests=_functionRequests,FunctionFaults=_functionFaults,FunctionPolicies=_functionPolicies,FunctionCoordinationExplicit=_functionCoordinationExplicit,
                RequestedFanMode=_functionFanMode,
                FunctionRetries=Array.AsReadOnly(_functionRetries.Select((r,i)=>i==(int)ProductFunction.Ctgp&&_wmiLifetime is not null?_wmiLifetime.RecoveryState:r.State).ToArray()),
                MaintenanceActive=_maintenanceActive,
                SharedWmiRecoveryPending=_fanGuardian is not null&&File.Exists(Path.Combine(_fanGuardian.SessionDirectory,"native-uncertain.signal")),
                CpuStatus = p?.CpuStatus, GpuStatus = p?.GpuStatus, AppliedPerformanceSource = p?.PowerSource ?? "Unknown", AppliedPerformance = _performance.AppliedConfiguration ?? (p is { CpuState: "Active" } or { GpuState: "ActiveUnverified" } ? _requestedPerformance : null),
                GuardianState = recovery?.Pending==true ? "Recuperación pendiente" : p?.RuntimeFailure is not null ? "Failed" : _performance.HasProcess ? p?.Phase ?? "Recovering" : "Sin sesión",
                AppliedFanProfile = _fans.Authority == FanAuthority.Custom ? _selectedFanProfile : null,
                LifecycleBlocked = _lifecycleBlocked, LifecycleBlockReason = _lifecycleBlockReason, Message = message ?? _state.Message, Failure = failure ?? (clearFailure ? null : _state.Failure)
            }; state = _state;
        }
        RuntimeProgressJournal.Record("Control.Publication",state.AutomaticSessionId,state.PublicationSequence,state.Snapshot?.Timestamp);
        _presentation.Post(state);
    }
    public async Task RetireFanRuntimeAsync()
    {
        lock(_disposalSync)if(_fullDisposal is not null)throw new IOException("El cierre global ya está en marcha.");
        try{await DisposeLocalAsync();}catch when(_localDrainConfirmed){}
        var receipt=await _performance.VerifyRetainedSessionAsync(_retainedJournalReader);
        lock(_disposalSync)
        {
            if(_fullDisposal is not null||!_ownsPerformanceLifetime||_performanceLifetime.Closing)
                throw new IOException("La conservación CPU/GPU quedó cancelada por el cierre global.");
            _retainedPerformance=receipt;
            _performanceLifetime.Start();
        }
    }
    public async Task VerifyRetainedPerformanceAsync()
    {
        var receipt=await _performance.VerifyRetainedSessionAsync(_retainedJournalReader);
        lock(_disposalSync)
        {
            if(_fullDisposal is not null||!_releaseCompleted||!_ownsPerformanceLifetime)throw new IOException("No hay una sesión conservada disponible.");
            _retainedPerformance=receipt;
        }
    }
    private ProductPerformanceLifetime RetainedPerformanceForConstruction()
    {
        lock(_disposalSync){EnsureRetainedPerformance();return _performanceLifetime;}
    }
    private void EnsureRetainedPerformance()
    {
        if(!_releaseCompleted||_fullDisposal is not null||!_ownsPerformanceLifetime||_performanceLifetime.Closing||
            _retainedPerformance is not{} receipt||!_performance.StillOwns(receipt))
            throw new IOException("La sesión CPU/GPU conservada cambió o no tiene una confirmación vigente.");
        var journals=_retainedJournalReader();
        if(journals.Cpu!=receipt.Cpu||journals.Gpu!=receipt.Gpu)throw new IOException("Los registros CPU/GPU cambiaron antes de transferir el propietario.");
        _wmiLifetime?.EnsureTransferAllowed();
    }
    private void TransferPerformanceTo(ProductRuntime next,Action commitFanBoundary)
    {
        lock(_disposalSync)
        {
            EnsureRetainedPerformance();
            if(!ReferenceEquals(next._performanceLifetime,_performanceLifetime))throw new IOException("El destinatario tiene otro propietario CPU/GPU.");
            if(!ReferenceEquals(next._wmiLifetime,_wmiLifetime))throw new IOException("El destinatario tiene otra frontera WMI.");
            commitFanBoundary();
            _ownsPerformanceLifetime=false;next._ownsPerformanceLifetime=true;
            if(_wmiLifetime is not null){_ownsWmiLifetime=false;next._ownsWmiLifetime=true;}
        }
    }
    public ValueTask DisposeAsync()
    {
        lock(_disposalSync)return new(_fullDisposal??=Task.Run(DisposeFullAsync));
    }
    private Task DisposeLocalAsync()
    {
        lock(_disposalSync)return _localDisposal??=Task.Run(DisposeFanAndTelemetryAsync);
    }
    private async Task DisposeFanAndTelemetryAsync()
    {
        _automaticActivation.Cancel();
        _closing = true; _lifetime.Cancel(); _fans.CloseCustomAdmissionForLifecycleBoundary();
        _performanceLifetime.Changed-=PerformanceChanged;
        if(_wmiLifetime is not null)_wmiLifetime.Changed-=WmiChanged;
        _presentation.Dispose();
        var failures = new List<Exception>();
        // Drain commands as well as fan dispatch before a retained owner can
        // cross to another runtime. No incoming command can pass _closing.
        await _commands.WaitAsync();
        try { await _fanCommands.WaitAsync(); try { await _fans.DisposeAsync(); } finally { _fanCommands.Release(); } }
        catch (Exception ex) { failures.Add(ex); }
        finally{_commands.Release();}
        try { await _worker.DisposeAsync(); }
        catch (Exception ex) when (_worker.ShutdownReport is { ResourcesDrained:true, CleanupFailures.Count:0 })
        {
            // A completed task fault remains diagnostic evidence. The explicit
            // local cleanup receipt, not the exception itself, proves drainage.
            // Fan/CPU/GPU release is still independently required below.
            AppLog.Write("Telemetry task fault retained after confirmed local cleanup: " + ex);
        }
        catch (Exception ex) { failures.Add(ex); }
        if(_wmiLifetime is not null)try{await _wmiLifetime.DrainFanReadsAsync();}catch(Exception error){failures.Add(error);}
        if (_poll is not null) try { await _poll; } catch (OperationCanceledException) { } catch (Exception ex) { failures.Add(ex); }
        if (_thermalDeadline is not null) try { await _thermalDeadline; } catch (OperationCanceledException) { } catch (Exception ex) { failures.Add(ex); }
        try { await _recoveryPreview.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
        try { await _performanceRecoveryPreview.DisposeAsync(); } catch (Exception ex) { failures.Add(ex); }
        _lifetime.Dispose();
        if (failures is [WmiFanReadDrainPendingException pending]) _lateReadDrain = pending;
        if (failures.Count > 0) throw new AggregateException("Liberación incompleta. Conserva los journals y revisa los informes.",failures);
        _releaseCompleted=true;
    }
    private async Task DisposeFullAsync()
    {
        var failures=new List<Exception>();
        try{await DisposeLocalAsync();}
        catch(Exception error){if(!_localDrainConfirmed)failures.Add(error);}
        if(_ownsWmiLifetime&&_wmiLifetime is not null)
        {
            try{await _wmiLifetime.DisposeAsync();}
            catch(Exception error){_lateReadDrain=null;failures.Add(error);}
        }
        if(_ownsPerformanceLifetime)
        {
            try{await _performanceLifetime.DisposeAsync();}
            catch(Exception error)
            {
                _lateReadDrain=null; // A read receipt cannot hide a separate performance release failure.
                if(failures.Count==0)_drainedGpuSession=_performance.DrainedGpuSession;
                failures.Add(error);
            }
        }
        if(failures.Count>0){_releaseCompleted=false;throw new AggregateException("Liberación global incompleta; se conservan los registros.",failures);}
        _releaseCompleted=true;
    }
}

internal interface IProductLateReadDrain
{
    bool HasPendingReadDrain { get; }
    Task ConfirmReadDrainAsync();
}
internal interface IProductFanRetry
{
    Task RetireFanRuntimeAsync();
    Task VerifyRetainedPerformanceAsync();
    void EnsureFanRetryInventoryClear()=>ProductSessionRestart.EnsureNoFanRecoveryRecords();
}
