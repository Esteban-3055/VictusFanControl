using System.Diagnostics;
using System.Runtime.InteropServices;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

/// <summary>New normal product GUI. Legacy MainForm is reachable only through explicit qualification entries.</summary>
internal sealed partial class ProductForm : Form
{
    private static readonly Size DesignWindowSize = new(1344,756);
    private readonly ProductCanvas _canvas = new();
    private readonly string _modules;
    private readonly ProductAutomaticReviewMode? _automaticReview;
    private IProductRuntime? _runtime;
    private ProductProfiles _draft;
    private ProductProfiles _saved;
    private bool _hasSavedBaseline;
    private readonly string? _profilesPath;
    private ProductFanAnimationSettings? _fanAnimationSettings;
    private Task<ProductFanAnimationSettings>? _animationLoad;
    private Task<bool>? _animationReload;
    private bool _animationNeedsApply;
    private sealed record LoadedPreferences(ProductProfiles Profiles,string? Notice,bool Baseline);
    private Task<LoadedPreferences>? _preferencesLoad;
    private bool _startupResourcesAcquired;
    private VictusFanControl.Recovery.ProductRecoveryInstallationGate? _installationGate;
    private bool _schedulerRetryRequested;
    private readonly System.Windows.Forms.Timer _presentationTimer = new() { Interval = 1000 };
    private TaskCompletionSource? _commandsDrained;
    private readonly NotifyIcon _tray;
    private readonly ProductIcons _icons = new();
    internal ProductTrayState TrayState { get; private set; }
    private bool _closing, _exitRequested, _disposedRuntime, _creating;
    private IntPtr _displayRegistration, _suspendRegistration;
    private bool _displayOff;
    private bool _systemSuspended,_powerFenceFailure;
    private long _minimumRuntimePublication;
    private Task _lifecycleRelease = Task.CompletedTask;
    private int _pendingCommands;
    private readonly bool _requirePowerNotifications;
    internal Action? PowerRegistrationFixture { get; set; }
    private Task _startup = Task.CompletedTask;
    private ProductStartupAutomatic? _startupAutomatic;
    private ProductProfiles? _startupPreferences;
    private string? _startupFailure;
    private string? _lastEditingSource;
    private long _startupRetryAt;
    private string? _startupWaitCodes;
    private long _startupWaitLoggedAt;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ProductCanvas Canvas => _canvas;
    internal ProductProfiles Draft => ProductProfilesStore.Copy(_draft);
    internal bool Dirty => _canvas.Dirty;
    internal bool UserRequestedExit=>_exitRequested&&!_schedulerRetryRequested;
    internal ProductForm(string modules,bool minimized=false,IProductRuntime? fixture=null,ProductProfiles? fixtureProfiles=null,Func<Task<IProductRuntime>>? runtimeFactory=null,string? profilesPath=null,bool registerPowerNotificationsInFixture=false,ProductAutomaticReviewMode? automaticReview=null,ProductRestartState? restartState=null,string? restartDirectory=null,bool enableStartupAutomaticInFixture=false,Func<ProductProfiles,Task<IProductRuntime>>? retryRuntimeFactory=null,Func<PerformanceRecoveryPreview>? recoveryPreflight=null,Func<ProductDisplayMetrics>? displayMetricsInFixture=null,string? fanAnimationPath=null,string? externalCoolingPath=null,Func<Task<bool>>? startupRegistrationReader=null)
    {
        SuspendLayout();
        _automaticRecovery=new(_recoveryEpisode);_automaticResumption=new(_recoveryEpisode);
        _externalCoolingPath=externalCoolingPath;
        _retryRuntimeFactory=retryRuntimeFactory;_modules=modules;_automaticReview=automaticReview;_runtime=fixture;_profilesPath=profilesPath;
        _isolatedRuntime=fixture is not null||runtimeFactory is not null;_restartDirectory=restartDirectory;_restartOpening=restartState is not null;
        _requirePowerNotifications=!_isolatedRuntime||registerPowerNotificationsInFixture;
        _startupRegistrationReader = startupRegistrationReader;
        if (!_isolatedRuntime && _startupRegistrationReader is null) _startupRegistrationReader = ()=>WindowsStartupRegistration.IsEnabledAsync(_modules);
        _startupRecoveryPreviewReader = recoveryPreflight;
        if (!_isolatedRuntime && _startupRecoveryPreviewReader is null) _startupRecoveryPreviewReader = () => PerformanceRecoveryPreview.Read();
        // Recovery fixtures must not inspect the user's real ownership records.
        // Tests that exercise pending recovery supply their own selection reader.
        if (_isolatedRuntime) RecoverySelectionReader = () => new(Guid.Empty, Guid.Empty);
        // Isolated fixtures never read or create the real user's presentation file.
        if (!_isolatedRuntime || fanAnimationPath is not null)
        {
            var animationPath=fanAnimationPath??ProductFanAnimationSettings.DefaultPath;
            _animationLoad=Task.Factory.StartNew(()=>new ProductFanAnimationSettings(animationPath),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        if(displayMetricsInFixture is not null&&!_isolatedRuntime)throw new ArgumentException("La geometría simulada requiere un runtime aislado.");
        _displayMetricsInFixture=displayMetricsInFixture;
        _canvas.AutomaticRetryAvailable=!ProductFanRangePreview.Enabled&&automaticReview is null&&(!_isolatedRuntime||retryRuntimeFactory is not null);
        string? notice=null;
        if(fixtureProfiles is null&&(!_isolatedRuntime||profilesPath is not null))
        {
            _preferencesLoad=Task.Factory.StartNew(()=>
            {
                var loaded=ProductProfilesStore.Load(profilesPath,out var detail,Migrate);
                return new LoadedPreferences(loaded,detail,File.Exists(profilesPath??ProductProfilesStore.DefaultPath)&&detail is null);
            },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        }
        _draft=ProductProfilesStore.Copy(fixtureProfiles??new ProductProfiles());
        _saved=ProductProfilesStore.Copy(_draft);
        _hasSavedBaseline=fixtureProfiles is not null;
        if(restartState is not null)
        {
            _draft=ProductProfilesStore.Parse(restartState.DraftJson);_saved=ProductProfilesStore.Parse(restartState.SavedJson);_hasSavedBaseline=restartState.HasSavedBaseline;
            _canvas.Page=restartState.Page;_canvas.Editing=restartState.Editing;
            notice="Sesión reiniciada en Firmware. Borrador conservado; los límites y Automático requieren una nueva aplicación explícita.";
            AppLog.Write("PRODUCT SESSION RESTART OPENED: previous="+restartState.PreviousSessionId+"; current="+AppLog.SessionId+"; no authority transferred.");
        }
        _presentationTimer.Tick+=(_,_)=>PresentationTick();
        Text="VictusFanControl";Icon=_icons.Program;FormBorderStyle=FormBorderStyle.None;BackColor=ProductCanvas.Background;AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        MinimumSize=new(1040,660);ClientSize=DesignWindowSize;StartPosition=FormStartPosition.CenterScreen;
        _canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=notice is not null;_canvas.Notice=notice??AppLog.InitializationFailure??"";Controls.Add(_canvas);
        _canvas.PageChanged+=(_,_)=>{LayoutExternalCoolingPage();LayoutHyperXPage();};_canvas.SizeChanged+=(_,_)=>{LayoutExternalCoolingPage();LayoutHyperXPage();};
        if(restartState is not null)_canvas.Dirty=restartState.Dirty;
        _canvas.Command+=HandleCommand;_canvas.ValueEdited+=EditValue;_canvas.NodeEdited+=EditNode;
        _canvas.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left&&_canvas.IsHeaderDrag(e.Location)){ReleaseCapture();SendMessage(Handle,0xA1,2,0);}};
        _canvas.MouseDoubleClick+=(_,e)=>{if(_canvas.IsHeaderDrag(e.Location))ToggleMaximize();};
        var menu=new ContextMenuStrip();menu.Items.Add("Abrir VictusFanControl",null,(_,_)=>ShowFromTray());
        menu.Items.Add("Volver a Firmware",null,(_,_)=>HandleCommand("firmware"));
        menu.Items.Add("Liberar CPU / GPU",null,(_,_)=>HandleCommand("performance-release"));
        menu.Items.Add("Recuperar sesión definitiva",null,async(_,_)=>await ForceSessionRecoveryAsync());
        menu.Items.Add("Reiniciar sesión",null,async(_,_)=>await RestartSessionAsync());
        menu.Items.Add("Salir",null,(_,_)=>{_exitRequested=true;Close();});
        _tray=new(){Icon=_icons.Default,Text="VictusFanControl · Firmware",ContextMenuStrip=menu,Visible=fixture is null&&runtimeFactory is null};
        _tray.DoubleClick+=(_,_)=>ShowFromTray();
        Shown+=(_,_)=>
        {
            if(_creating)return;_creating=true;
            _ = RestoreDownloadedUpdateAsync();
            _startup=InitializeAsync(minimized,fixture is not null||runtimeFactory is not null,runtimeFactory,registerPowerNotificationsInFixture,enableStartupAutomaticInFixture,recoveryPreflight);
            _externalCoolingStartup=InitializeExternalCoolingAsync();
            _hyperXStartup=InitializeHyperXAsync();
        };
        FormClosing+=OnClosing;FormClosed+=(_,_)=>{UnregisterPowerNotifications();_tray.Visible=false;};
        ResumeLayout(false);PerformLayout();
    }
    private async Task InitializeAsync(bool minimized,bool isolated,Func<Task<IProductRuntime>>? runtimeFactory,bool registerPowerNotificationsInFixture,bool enableStartupAutomaticInFixture,Func<PerformanceRecoveryPreview>? recoveryPreflight=null)
    {
        var initialPowerBoundary=_updateLifecycleBoundary;
        try
        {
            _canvas.Busy=true;
            _canvas.Notice="Preparando preferencias y comprobando la sesión…";
            if(!isolated)
            {
                var gate=await Task.Factory.StartNew(()=>VictusFanControl.Recovery.ProductRecoveryInstallationGate.EnterApplication(),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                if(_closing||_restarting||IsDisposed){gate.Dispose();return;}
                _installationGate=gate;
            }
            if(_preferencesLoad is not null)
            {
                var loaded=await _preferencesLoad;
                if(_closing||_restarting||IsDisposed)return;
                if(!_restartOpening)
                {
                    _draft=loaded.Profiles;_saved=ProductProfilesStore.Copy(_draft);_hasSavedBaseline=loaded.Baseline;
                    _canvas.Profiles=_draft;_canvas.Dirty=loaded.Notice is not null;_canvas.Notice=loaded.Notice??"Comprobando registros de recuperación…";ResetSimulation();
                }
            }
            if(!isolated||recoveryPreflight is not null)
            {
                var pending=await Task.Factory.StartNew(recoveryPreflight??(()=>PerformanceRecoveryPreview.Read()),CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
                if(_closing||_restarting||IsDisposed)return;
                if(pending.Pending)
                {
                    _canvas.State=_canvas.State with{PerformanceRecovery=pending,LifecycleBlocked=true,Runtime="RecoveryRequired",Failure=pending.Detail,GuardianState="Recuperación pendiente",CanApplyPerformance=false,
                        FanMode=pending.FanRecords?.Count>0?"RecoveryRequired":"Firmware",FanAuthority=pending.FanRecords?.Count>0?"Unknown":"Firmware",
                        CpuState=pending.CpuSession.HasValue?"Recovering":"Disabled",GpuState=pending.GpuSession.HasValue?"Recovering":"Disabled"};
                    _canvas.Page=ProductPage.Settings;_canvas.Notice=pending.Detail;_canvas.AutomaticRetryAvailable=false;
                    _recoveryAtStartup = true;
                    if(_requirePowerNotifications)RegisterPowerNotifications();UpdateTray(_canvas.State);_presentationTimer.Start();
                    if((minimized||_draft.StartMinimized)&&!ProductFanRangePreview.Enabled&&!_restartOpening)Hide();
                    return; // The recovery monitor opens no telemetry/controller until release succeeds.
                }
            }
            _startupResourcesAcquired=true;
            if(_runtime is null)
            {
                var startupProfiles=Draft;
                _runtime=await (runtimeFactory?.Invoke()??Task.Run<IProductRuntime>(()=>new ProductRuntime(_modules,startupProfiles,_automaticReview)));
            }
            // A close during construction waits for this task, then disposes the returned service without starting it.
            if(_closing||_restarting||IsDisposed)return;
            if(_displayOff||initialPowerBoundary!=_updateLifecycleBoundary)
                _runtime.FenceLifecycle("Cambio de energía durante la creación de la sesión");
            if((!isolated||enableStartupAutomaticInFixture)&&_automaticReview is null&&!ProductFanRangePreview.Enabled&&!_restartOpening&&_saved.ActivateAutomaticOnStart)
            { _startupPreferences=ProductProfilesStore.Copy(_saved); _startupAutomatic=new(Environment.TickCount64,protections:_saved.Protections,resumption:initialPowerBoundary!=_updateLifecycleBoundary); }
            AttachRuntime(_runtime);UpdateState(_runtime.State);
            if(_requirePowerNotifications)RegisterPowerNotifications();
            _runtime.Start();_presentationTimer.Start();
            if(initialPowerBoundary!=_updateLifecycleBoundary&&!_displayOff)
            { _ = QueueLifecycleRelease("Power boundary during construction");_ = ObservePowerTaskAsync(ResumeAfterReleaseAsync()); }
            _ = CheckStartupRegistrationAsync(); // Optional status must never own or fail hardware startup/exit.
            if(!_closing&&!IsDisposed&&(minimized||_draft.StartMinimized)&&!ProductFanRangePreview.Enabled&&!_restartOpening)Hide();
        }
        catch(Exception ex)
        {
            if(ex is VictusFanControl.Recovery.ProductInstallationBusyException)
            {
                _schedulerRetryRequested=true;_exitRequested=true;Environment.ExitCode=172;
                AppLog.Write("Startup deferred by active installation; no controller opened.");Close();return;
            }
            _startupFailure="No se pudo iniciar: "+ex.Message;
            if(_runtime is { } incomplete)
            {
                try { incomplete.FenceLifecycle("Startup incompleto"); }
                catch(Exception fence) { AppLog.Write("Product startup fence failed; cleanup still required: "+fence); }
                _lifecycleRelease=Task.Run(()=>incomplete.ReleaseForLifecycleAsync("Startup incompleto"));
            }
            AppLog.Write("Product GUI startup: "+ex);
            if(!_closing&&!IsDisposed){UpdateState(_runtime?.State??_canvas.State);_presentationTimer.Start();}
        }
        finally{if(!IsDisposed){_canvas.Busy=_pendingCommands>0;_canvas.Invalidate();}}
    }
    private static ProductProfiles Migrate()
    {
        var previous=File.Exists(FanConfigurationStore.DefaultPath)?FanConfigurationStore.Load(null,out _):null;var performance=PerformanceUiSettingsStore.Load();
        return new()
        {
            Ac=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac,previous) with {CpuPl1Watts=performance.AcPl1Watts,CpuPl2Watts=performance.AcPl2Watts},
            Battery=ProductProfiles.DefaultProfile(ProductPowerProfile.Battery,previous) with {CpuPl1Watts=performance.BatteryPl1Watts,CpuPl2Watts=performance.BatteryPl2Watts}
        };
    }
    private void UpdateState(ProductRuntimeState state)
    {
        if(IsDisposed||_closing)return;
        if(InvokeRequired){if(IsHandleCreated)BeginInvoke(()=>UpdateState(state));return;}
        if(_startupFailure is not null)state=state with{Failure=state.Failure??_startupFailure,
            Runtime="Failed",LifecycleBlocked=true,LifecycleBlockReason=_startupFailure,CanApplyPerformance=false};
        ObserveAutomaticResumption(state);
        _canvas.State=state;
        VictusFanControl.Runtime.RuntimeProgressJournal.Record("UI.Update",state.AutomaticSessionId,state.PublicationSequence,state.Snapshot?.Timestamp);
        if(state.Source is "Ac" or "Battery" && _lastEditingSource!=state.Source)
        {
            _lastEditingSource=state.Source;
            _canvas.Editing=state.Source=="Ac"?ProductPowerProfile.Ac:ProductPowerProfile.Battery;
            _canvas.SelectedNode=-1;ResetSimulation();
        }
        if(state.Snapshot is not null)_canvas.AddSnapshot(state.Snapshot);
        UpdateTray(state);
        if(state.Failure is not null)_canvas.Notice=state.Failure;
        TryStartupAutomatic();
        _canvas.Invalidate();
    }
    private void UpdateTray(ProductRuntimeState state)
    {
        var next=ProductIcons.Select(state,TrayState);
        if(next!=TrayState){TrayState=next;_tray.Icon=_icons.For(TrayState);}
        var trayText="VictusFanControl · "+(TrayState==ProductTrayState.Error?"Error / sesión interrumpida":state.FanMode+" · "+state.FanAuthority);
        _tray.Text=trayText[..Math.Min(63,trayText.Length)];
    }
    private void TryStartupAutomatic()
    {
        // Retry construction can complete without a WinForms synchronization context.
        // Observe only after queued state publications have reached the UI thread.
        if(InvokeRequired){if(IsHandleCreated&&!IsDisposed)BeginInvoke(TryStartupAutomatic);return;}
        if(_closing||_restarting||_displayOff||_systemSuspended||_startupAutomatic is null||_startupAutomatic.Finished||_runtime is null||_canvas.Busy||Environment.TickCount64<_startupRetryAt)return;
        var observedAt=_startupFixtureClock?.Utc??DateTimeOffset.UtcNow;var clock=_startupFixtureClock?.Tick??Environment.TickCount64;
        _runtime.RequestAutomaticTelemetry();
        var activate=_startupAutomatic.Observe(_canvas.State,observedAt,clock);
        _canvas.Notice=_startupAutomatic.Status;
        var waitCodes=string.Join(";",_startupAutomatic.Issues.Select(i=>i.Code));
        if(waitCodes.Length>0&&(waitCodes!=_startupWaitCodes||clock<_startupWaitLoggedAt||clock-_startupWaitLoggedAt>=10000))
        {
            _startupWaitCodes=waitCodes;_startupWaitLoggedAt=clock;
            AppLog.WriteStructured("PRODUCT AUTOMATIC WAIT",new
                {phase=_startupAutomatic.Phase,evaluatedAt=observedAt,issues=_startupAutomatic.Issues,
                    _canvas.State.Runtime,_canvas.State.Source,_canvas.State.Target,snapshot=_canvas.State.Snapshot});
        }
        if(waitCodes.Length==0)_startupWaitCodes=null;
        if(activate)
        {
            var profiles=ProductProfilesStore.Copy(_startupPreferences!);
            var request=_startupAutomatic;
            if(!_recoveryEpisode.TryPrepare(clock))
            { _startupAutomatic=new(clock,protections:profiles.Protections,resumption:true);return; }
            _ = RunAsync(async()=>
            {
                try
                {
                    var owner=_runtime;
                    await owner.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
                    RememberConfirmedAutomaticActivation(owner,profiles);_recoveryEpisode.Finish();
                    if(request.Phase!="resumption")_recoveryEpisode.CompleteUserActivation();
                }
                catch(Exception activationError)
                {
                    // Release both domains after a failed unattended preparation; journals survive a release failure.
                    await _runtime.ReleasePerformanceAsync();
                    UpdateState(_runtime.State with{Failure="Preparación de Automático no completada: "+activationError.Message});
                    _recoveryEpisode.Finish(ownerConfirmed:!_runtime.State.PerformanceProcessPresent&&_runtime.State.PerformanceRecovery?.Pending!=true);
                    if(!request.Cancelled && !_closing && !_displayOff && !_runtime.State.LifecycleBlocked)
                    {
                        _startupRetryAt=Environment.TickCount64+_recoveryEpisode.DelayMilliseconds;
                        _startupAutomatic=new(Environment.TickCount64,protections:profiles.Protections,resumption:true);
                    }
                    throw;
                }
            });
        }
    }
    internal void HandleCommand(string id)
    {
        if(_closing||IsDisposed)return;
        if(id.StartsWith("hyperx-",StringComparison.Ordinal)&&_hyperXPage is{} headset){if(!_canvas.Busy)_=headset.ExecuteAsync(id[7..]);return;}
        if(id.StartsWith("external-curve-",StringComparison.Ordinal)&&_canvas.ExternalCurve is{} curve){_=curve.ExecuteAsync(id[15..]);return;}
        if(id!="external-cooling"&&id.StartsWith("external-",StringComparison.Ordinal)&&_externalCoolingPage is{} external){_=external.ExecuteAsync(id);return;}
        if (id is "firmware" or "fan-mode-0" or "fan-mode-1" or "session-restart")
            _automaticRecoveryResumeSuppressed = true;
        if (_recoveryTask is not null && id is "firmware" or "fan-mode-0" or "fan-mode-1" or "performance-release")
        {
            _recoveryResumeCancelled = true; _automaticResumption.Cancel();
            _startupAutomatic?.Cancel("Reanudación cancelada por el usuario.");
            _canvas.Notice = "Reanudación cancelada. La recuperación terminará liberando el control.";
            _canvas.Invalidate(); return;
        }
        if(_closing||_restarting||IsDisposed)return;
        if((_systemSuspended||_displayOff||_powerFenceFailure)&&
            (id is "fan-mode-1" or "fan-mode-2"||id.EndsWith("-apply",StringComparison.Ordinal)))
        {_canvas.Notice="Aplicación en espera de una reanudación y liberación confirmadas.";_canvas.Invalidate();return;}
        if (_runtime is null && _canvas.State.PerformanceRecovery?.Pending == true && id is "firmware" or "fan-mode-0")
        {
            _automaticResumption.Cancel(); _canvas.Notice="Se recuperará la sesión sin activar Automático."; _canvas.Invalidate(); return;
        }
        if(id is "session-restart" or "firmware" or "fan-mode-0" or "fan-mode-1" or "fan-mode-2" or "performance-apply" or "performance-release")
            _startupAutomatic?.Cancel("Automático al iniciar cancelado por el usuario.");
        if(id is "session-restart" or "firmware" or "fan-mode-0" or "fan-mode-1")_automaticResumption.Cancel();
        if(id=="session-restart"){_ = RestartSessionAsync();return;}
        if(id=="session-force-recovery"){_ = ForceSessionRecoveryAsync();return;}
        if(id.StartsWith("page-")){_canvas.Page=(ProductPage)int.Parse(id[5..]);_canvas.SelectedNode=-1;_canvas.Invalidate();return;}
        if(id is "profile-ac" or "profile-battery") {_canvas.Editing=id=="profile-ac"?ProductPowerProfile.Ac:ProductPowerProfile.Battery;_canvas.SelectedNode=-1;ResetSimulation();_canvas.Invalidate();return;}
        if(HandleAdvancedCommand(id))return;
        if(HandleRetentionCommand(id))return;
        if(id=="recovery-run"){_automaticResumption.Cancel();_ = RecoverPerformanceAsync();return;}
        if(HandleProtectionCommand(id)){_canvas.Invalidate();return;}
        if(id=="recovery-details")
        {
            var preview=PerformanceRecoveryPreview.Read();
            var instructions=preview.Instructions();
            bool copied=false;
            try {Clipboard.SetText(instructions);copied=true;} catch(System.Runtime.InteropServices.ExternalException) { }
            MessageBox.Show(this,instructions+(copied?"\n\nInstrucciones copiadas al portapapeles.":"\n\nNo se pudo copiar; conserva estas instrucciones."),"Recuperación explícita CPU/GPU",MessageBoxButtons.OK,MessageBoxIcon.Information);
            return;
        }
        if(id is "curve-influences" or "curve-points"){_canvas.ShowCurvePoints=id=="curve-points";_canvas.Invalidate();return;}
        if(id.StartsWith("fan-tab-")){_canvas.FanTab=Math.Clamp(int.Parse(id[8..]),0,2);_canvas.Invalidate();return;}
        if(id.StartsWith("perf-tab-")){_canvas.PerformanceTab=int.Parse(id[9..]);_canvas.Invalidate();return;}
        if(id.StartsWith("monitor-tab-")){_canvas.MonitorTab=int.Parse(id[12..]);_canvas.Invalidate();return;}
        if(id is "node-previous" or "node-next")
        {
            var count=_draft.Get(_canvas.Editing).Fan.UnifiedDemand!.Curve.Count;
            _canvas.SelectedNode=Math.Clamp(_canvas.SelectedNode+(id=="node-next"?1:-1),0,count-1);_canvas.Invalidate();return;
        }
        if(id.StartsWith("node-" )&&int.TryParse(id[5..],out var node)){_canvas.SelectedNode=node;_canvas.Invalidate();return;}
        if(id.EndsWith("-minus")||id.EndsWith("-plus"))
        {
            var plus=id.EndsWith("-plus");var key=id[..(id.Length-(plus?5:6))];var p=_draft.Get(_canvas.Editing);
            var current=key.StartsWith("influence-")?p.Fan.UnifiedDemand!.Influence(int.Parse(key[10..])):key.StartsWith("sim-input-")?_canvas.SimulationInputs.Value(int.Parse(key[10..])):key switch{"manual"=>_canvas.ManualLevel,"pl1"=>p.CpuPl1Watts,"pl2"=>p.CpuPl2Watts,"gpu"=>p.GpuMaximumMHz,_=>0};
            EditValue(key,current+(plus?1:-1));return;
        }
        if(_canvas.Busy&&id is not("firmware" or "fan-mode-0" or "window-minimize" or "window-maximize" or "window-close"))return;
        if(id=="fan-mode-1"&&(!_canvas.State.ManualAuthorized||_canvas.State.LifecycleBlocked))return;
        if(ProductFanRangePreview.Enabled&&id=="fan-mode-2")return;
        if(id=="fan-mode-2") { _automaticRecoveryResumeSuppressed=false; _automaticResumption.Arm(); }
        if(id=="fan-mode-2"&&_canvas.State.LifecycleBlocked){_ = RetryAutomaticAsync();return;}
        if(id=="manual-apply"&&(!_canvas.State.ManualAuthorized||_canvas.State.FanMode!="Manual"||_canvas.State.Runtime!="Healthy"||_canvas.State.LifecycleBlocked))return;
        if(id=="performance-apply"&&!_canvas.CanApplyPerformance)return;
        if(id=="performance-release"&&!_canvas.State.PerformanceProcessPresent)return;
        if(id is "pl1-text" or "pl2-text" or "gpu-text")
        {
            var editSource=_canvas.Editing;var key=id[..^5];var p=_draft.Get(editSource);var range=NumericRange(key);
            var title=key switch{"pl1"=>"CPU PL1 (W)","pl2"=>"CPU PL2 (W)",_=>"GPU máximo (MHz)"};
            var value=key switch{"pl1"=>p.CpuPl1Watts,"pl2"=>p.CpuPl2Watts,_=>p.GpuMaximumMHz};
            using var dialog=new ProductNumericDialog(title+" · "+(_canvas.Editing==ProductPowerProfile.Ac?"AC":"Batería"),value,range.Min,range.Max,
                text=>TryEditNumericValueForProfile(key,text,editSource,out var error)?null:error,
                help:key=="gpu"?null:"PL1 puede llegar hasta PL2. Si reduces PL2, PL1 baja cuando sea necesario. Editar solo cambia el borrador.");
            dialog.ShowDialog(this);_canvas.Invalidate();return;
        }
        switch(id)
        {
            case "external-cooling":ShowExternalCooling();break;
            case "interruption-details":
                if(_canvas.State.InterruptionDetails is { } details)
                    MessageBox.Show(this,details,"Sesión interrumpida",MessageBoxButtons.OK,MessageBoxIcon.Information);
                break;
            case "window-minimize":Hide();break;
            case "window-maximize":ToggleMaximize();break;
            case "window-close":Hide();break;
            case "curve-editor":_canvas.SimulationVisible=false;break;
            case "curve-simulator":_canvas.SimulationVisible=true;break;
            case "sim-1":case "sim-60":case "sim-1200":try{_canvas.Simulation.Advance(_canvas.SimulationInputs,int.Parse(id[4..]));}catch(Exception ex){_canvas.Notice=ex.Message;}break;
            case "sim-reset":ResetSimulation();break;
            case "sim-run":_canvas.SimulationRunning=!_canvas.SimulationRunning;break;
            case "edit-curve":_canvas.Page=ProductPage.Curves;break;
            case "edit-performance":_canvas.Page=ProductPage.Performance;break;
            case "cpu-toggle":Change(_draft with{CpuEnabled=!_draft.CpuEnabled});break;
            case "gpu-toggle":Change(_draft with{GpuEnabled=!_draft.GpuEnabled});break;
            case "automatic-start-toggle":Change(_draft with{ActivateAutomaticOnStart=!_draft.ActivateAutomaticOnStart});break;
            case "recovery-auto-toggle":Change(_draft with{RecoverSessionsAutomatically=!_draft.RecoverSessionsAutomatically});break;
            case "minimized-toggle":Change(_draft with{StartMinimized=!_draft.StartMinimized});break;
            case "save":_ = SaveAsync();break;
            case "curve-apply":
                if(!_canvas.CanApplyCurve){_canvas.Notice="Selecciona el perfil de la fuente real y espera a que Automatic esté controlando.";break;}
                var source=_canvas.Editing;var demand=Draft.Get(source).Fan.UnifiedDemand!;
                _ = RunAsync(async()=>{if(_runtime is null)throw new InvalidOperationException("Runtime no disponible.");await _runtime.ApplyFanCurveAsync(source,demand);
                    _canvas.Notice="Curva e influencias aplicadas en Automatic. Guardar conserva los cambios; CPU/GPU mantienen sus límites actuales.";});break;
            case "discard":if(_draft.IncludePrereleaseUpdates!=_saved.IncludePrereleaseUpdates)ResetUpdateSelection();_draft=ProductProfilesStore.Copy(_saved);_canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=!_hasSavedBaseline;_canvas.SelectedNode=-1;_canvas.Notice=_hasSavedBaseline?"Se recuperaron las preferencias guardadas.":"Se recuperó la configuración inicial; falta guardarla.";break;
            case "updates-check":_ = CheckUpdateAsync();break;
            case "updates-prerelease-toggle":if(!_canvas.UpdateBusy)Change(_draft with{IncludePrereleaseUpdates=!_draft.IncludePrereleaseUpdates});break;
            case "updates-download":_ = DownloadUpdateAsync();break;
            case "updates-install":_ = InstallDownloadedUpdateAsync();break;
            case "updates-page":_canvas.Page=ProductPage.Updates;break;
            case "startup-toggle":_ = ToggleStartupAsync();break;
            case "firmware":case "fan-mode-0":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,Draft)??Task.CompletedTask);break;
            case "fan-mode-1":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Manual,Draft)??Task.CompletedTask);break;
            case "fan-mode-2":
                if(!_canvas.State.AutomaticAuthorized){_canvas.Notice="Automático no está disponible para este equipo.";break;}
                if(_canvas.State.FanMode=="Automatic"){_canvas.Notice="Automatic ya está seleccionado; usa Aplicar en Curvas para actualizar curva e influencias. El plazo no se renueva.";break;}
                _ = RunAsync(async()=>
                {
                    if(_runtime is not {} current)throw new InvalidOperationException("No hay una sesión preparada.");
                    var profiles=Draft;
                    await current.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
                    RememberConfirmedAutomaticActivation(current,profiles);_recoveryEpisode.CompleteUserActivation();
                });break;
            case "manual-apply":_ = RunAsync(()=>_runtime?.ApplyManualAsync(_canvas.ManualLevel)??Task.CompletedTask);break;
            case "performance-apply":_ = RunAsync(()=>{var profiles=_canvas.PerformanceDraft;profiles.Validate();profiles.PerformanceConfiguration().Validate();return _runtime?.ApplyPerformanceAsync(profiles)??Task.CompletedTask;});break;
            case "performance-release":
                Change(_draft with{CpuEnabled=false,GpuEnabled=false});
                _ = RunAsync(()=>_runtime?.ReleasePerformanceAsync()??Task.CompletedTask);break;
            case "node-add":AddNode();break;
            case "node-remove":RemoveNode();break;
            case "curve-reset":ResetCurve(false);break;
            case "curve-defaults":ResetCurve(true);break;
            case "profiles-export":using(var dialog=new SaveFileDialog{Filter="Perfiles JSON (*.json)|*.json",FileName="VictusFanControl-perfiles.json",AddExtension=true,DefaultExt="json"})if(dialog.ShowDialog(this)==DialogResult.OK)_ = ExportProfilesAsync(dialog.FileName);break;
            case "profiles-import":using(var dialog=new OpenFileDialog{Filter="Perfiles JSON (*.json)|*.json",CheckFileExists=true})if(dialog.ShowDialog(this)==DialogResult.OK)_ = ImportProfilesAsync(dialog.FileName);break;
            case "diagnostics-export":using(var dialog=new SaveFileDialog{Filter="Diagnóstico ZIP (*.zip)|*.zip",FileName="VictusFanControl-diagnostico-"+AppLog.SessionId+".zip",AddExtension=true,DefaultExt="zip"})if(dialog.ShowDialog(this)==DialogResult.OK)_ = ExportDiagnosticsAsync(dialog.FileName);break;
            case "open-logs":try {Process.Start(new ProcessStartInfo(Path.GetDirectoryName(AppLog.CurrentLogPath)!){UseShellExecute=true});}catch(Exception ex){_canvas.Notice=ex.Message;}break;
        }
        _canvas.Invalidate();
    }
    private void Change(ProductProfiles next)
    {
        if(_canvas.Busy||_closing||IsDisposed)return;_startupAutomatic?.Cancel("Automático al iniciar cancelado al editar preferencias.");next.Validate();if(next.IncludePrereleaseUpdates!=_draft.IncludePrereleaseUpdates)ResetUpdateSelection();_draft=next;_canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=true;_canvas.Notice="Cambios en edición; no aplicados al hardware.";_canvas.Invalidate();
    }
    internal void EditValue(string key,int value)
    {
        if(key.StartsWith(HyperXBatteryPage.ValuePrefix,StringComparison.Ordinal)){if(!_closing&&!IsDisposed&&!_canvas.Busy)_hyperXPage?.EditValue(key[HyperXBatteryPage.ValuePrefix.Length..],value);return;}
        if(key.StartsWith("external-curve-value-",StringComparison.Ordinal)){if(!_closing&&!IsDisposed&&!_canvas.Busy)_canvas.ExternalCurve?.EditValue(key[21..],value);return;}
        if(_canvas.Busy||_closing||IsDisposed)return;
        if(key.StartsWith("sim-input-")){_canvas.SimulationInputs=_canvas.SimulationInputs.With(int.Parse(key[10..]),value);_canvas.Invalidate();return;}
        var slot=_canvas.Editing;var p=_draft.Get(slot);
        if(key.StartsWith("influence-"))
        {
            var axis=int.Parse(key[10..]);if(axis is <0 or >5)return;
            var model=p.Fan.UnifiedDemand!;var nextModel=model.WithInfluence(axis,Math.Clamp(value,axis<2?100:0,UnifiedFanDemand.MaximumInfluencePercent));
            Change(_draft.With(slot,p with{Fan=p.Fan with{UnifiedDemand=nextModel}}));return;
        }
        if(key=="manual") {_canvas.ManualLevel=Math.Clamp(value,10,_canvas.State.ManualMaximumLevel);_canvas.Invalidate();return;}
        var next=key switch
        {
            "pl1"=>ProductCpuPowerEditor.EditPl1(p,value),
            "pl2"=>ProductCpuPowerEditor.EditPl2(p,value),
            "gpu"=>p with{GpuMaximumMHz=Math.Clamp(value,GpuProductPreferences.MinimumMHz,GpuProductPreferences.Maximum(slot))},
            _=>p
        };
        Change(_draft.With(slot,next));
    }
    private (int Min,int Max) NumericRange(string key)=>key switch
    {
        "pl1"=>(CpuPowerProductDefaults.MinimumPl1Watts,ProductCpuPowerEditor.MaximumPl1(_draft.Get(_canvas.Editing))),
        "pl2"=>(CpuPowerProductDefaults.MinimumPl2Watts,CpuPowerProductDefaults.MaximumConfigurablePl2Watts),
        "gpu"=>(GpuProductPreferences.MinimumMHz,GpuProductPreferences.Maximum(_canvas.Editing)),
        _=>throw new ArgumentException("Campo numérico desconocido.",nameof(key))
    };
    internal bool TryEditNumericValueForProfile(string key,string text,ProductPowerProfile source,out string error)
    {
        var selected=_canvas.Editing;
        try { _canvas.Editing=source;return TryEditNumericValue(key,text,out error); }
        finally { _canvas.Editing=selected;ResetSimulation(); }
    }
    internal bool TryEditNumericValue(string key,string text,out string error)
    {
        error="";
        if(_canvas.Busy||_closing||IsDisposed){error="Espera a que termine la operación actual.";return false;}
        if(key is not("pl1" or "pl2" or "gpu")){error="Campo numérico desconocido.";return false;}
        var range=NumericRange(key);
        if(!int.TryParse(text,System.Globalization.NumberStyles.Integer,System.Globalization.CultureInfo.InvariantCulture,out var value)||value<range.Min||value>range.Max)
        {error=$"Introduce un número entero entre {range.Min} y {range.Max}.";return false;}
        EditValue(key,value);return true;
    }
    internal void EditNode(int index,double input,int level)
    {
        if(_canvas.Busy||_closing||IsDisposed||!double.IsFinite(input))return;
        var slot=_canvas.Editing;var p=_draft.Get(slot);var model=p.Fan.UnifiedDemand!;var points=model.Curve.ToArray();
        if(index<0||index>=points.Length)return;
        var lo=index==0?0:points[index-1].Input+1;var hi=index==points.Length-1?100:points[index+1].Input-1;
        var lowLevel=index==0?10:points[index-1].Level;var highLevel=index==points.Length-1?54:points[index+1].Level;
        var x=index==0?0:index==points.Length-1?100:Math.Clamp(Math.Round(input),lo,hi);
        var y=Math.Clamp(level,(int)Math.Max(index==points.Length-1?50:10,lowLevel),(int)highLevel);
        points[index]=new(x,y);
        Change(_draft.With(slot,p with{Fan=p.Fan with{UnifiedDemand=model with{Curve=points}}}));
    }
    private void AddNode()
    {
        var p=_draft.Get(_canvas.Editing);var model=p.Fan.UnifiedDemand!;
        if(!ProductCurveInsertion.CanInsert(model.Curve,_canvas.SelectedNode))
        {_canvas.Notice="Selecciona un punto con espacio a su derecha para añadir otro.";_canvas.Invalidate();return;}
        var selected=_canvas.SelectedNode;
        var points=ProductCurveInsertion.Insert(model.Curve,selected);
        Change(_draft.With(_canvas.Editing,p with{Fan=p.Fan with{UnifiedDemand=model with{Curve=points}}}));
        _canvas.SelectedNode=selected+1;_canvas.ShowCurvePoints=true;
    }
    private void RemoveNode()
    {
        var p=_draft.Get(_canvas.Editing);var model=p.Fan.UnifiedDemand!;var points=model.Curve.ToList();
        if(points.Count<=2||_canvas.SelectedNode<=0||_canvas.SelectedNode>=points.Count-1)return;
        points.RemoveAt(_canvas.SelectedNode);_canvas.SelectedNode=-1;
        Change(_draft.With(_canvas.Editing,p with{Fan=p.Fan with{UnifiedDemand=model with{Curve=points.ToArray()}}}));
    }
    private void ResetCurve(bool all)
    {
        var p=_draft.Get(_canvas.Editing);var defaults=ProductProfiles.DefaultProfile(_canvas.Editing).Fan;
        var fan=p.Fan with{UnifiedDemand=all?defaults.UnifiedDemand:p.Fan.UnifiedDemand! with{Curve=defaults.UnifiedDemand!.Curve},Tuning=all?defaults.Tuning:p.Fan.Tuning};
        Change(_draft.With(_canvas.Editing,p with{Fan=fan}));_canvas.SelectedNode=-1;
    }
    internal Task ExportProfilesAsync(string path)=>RunAsync(async()=>{var draft=Draft;await Task.Run(()=>ProductProfilesStore.Save(draft,path));_canvas.Notice="Respaldo exportado; las preferencias en edición no se han aplicado.";});
    internal Task ImportProfilesAsync(string path)=>RunAsync(async()=>
    {
        var imported=await Task.Run(()=>{if(new FileInfo(path).Length>1024*1024)throw new InvalidDataException("El archivo de perfiles supera 1 MiB.");return ProductProfilesStore.Parse(File.ReadAllText(path));});
        if(_closing)return;imported.Validate();if(imported.IncludePrereleaseUpdates!=_draft.IncludePrereleaseUpdates)ResetUpdateSelection();_draft=imported;_canvas.Profiles=_draft;ResetSimulation();_canvas.SelectedNode=-1;_canvas.Dirty=true;_canvas.Notice="Perfiles importados en edición; falta Guardar. No se aplicó hardware.";
    });
    internal Task ExportDiagnosticsAsync(string path,string? fixtureLog=null)=>RunAsync(async()=>
    {
        var state=_canvas.State;var draft=Draft;var display=DisplayDiagnostic();
        var updater=new{status=_canvas.UpdateStatus,checkedAt=_canvas.UpdateCheckedAt,progressPercent=_canvas.UpdateProgressPercent,
            availableVersion=_canvas.AvailableUpdate?.Version.ToString(),downloadedVersion=_canvas.DownloadedUpdate?.Version.ToString(),
            operationInProgress=_canvas.UpdateBusy,recoveryInProgress=_recoveryTask is not null,
            recoveryMonitorActive=RecoveryMonitorActive,automaticRecoveryEnabled=draft.RecoverSessionsAutomatically};
        var export=ProductDiagnostics.TryExportAsync(path,state,draft,fixtureLog??AppLog.CurrentLogPath,display,updater);
        var completed=await Task.WhenAny(export,Task.Delay(2000));
        _canvas.Notice=ReferenceEquals(completed,export)&&await export?"Diagnóstico exportado. Revisa rutas locales antes de compartirlo.":
            "Diagnóstico pendiente o no disponible. La sesión de control continúa; no se crearán exportaciones paralelas.";
    });
    internal void PresentationTick()
    {
        if(_closing||IsDisposed)return;
        ObserveExternalCoolingDiagnostics();
        RefreshAnimationSettings();
        TryStartupAutomatic();TryAutomaticRecovery();TryAutomaticResumption();
        // Only the visible offline simulator consumes virtual time. No catch-up
        // on returning from the tray, another page or the editor.
        if(Visible&&WindowState!=FormWindowState.Minimized&&!_canvas.Busy&&_canvas.Page==ProductPage.Curves&&_canvas.SimulationVisible&&_canvas.SimulationRunning)
        {
            try{_canvas.Simulation.Advance(_canvas.SimulationInputs,1);}
            catch(Exception ex){_canvas.SimulationRunning=false;_canvas.Notice=ex.Message;}
        }
        _canvas.Invalidate();
    }
    private void RefreshAnimationSettings()
    {
        var active=Visible&&WindowState!=FormWindowState.Minimized&&_canvas.Page==ProductPage.Home;
        if(_animationLoad is {IsCompleted:true} load)
        {
            if(load.IsCompletedSuccessfully){_fanAnimationSettings=load.Result;_animationNeedsApply=true;}
            else AppLog.Write("Optional animation settings unavailable: "+load.Exception?.GetBaseException().GetType().Name);
            _animationLoad=null;
        }
        if(_animationReload is {IsCompleted:true} reload)
        {
            if(reload.IsCompletedSuccessfully&&reload.Result)_animationNeedsApply=true;
            _animationReload=null;
        }
        if(active&&_animationNeedsApply&&_fanAnimationSettings is {} applied)
        {_canvas.SetHomeAnimationSecondsPerRevolution(applied.SecondsPerRevolution);_animationNeedsApply=false;}
        if(active&&_animationReload is null&&_fanAnimationSettings is {} current)
            _animationReload=Task.Run(current.Reload);
    }
    private void ResetSimulation()=>_canvas.Simulation=new(_draft.Get(_canvas.Editing).Fan);
    private async Task SaveAsync() => await RunAsync(async()=>{var settings=Draft;await Task.Run(()=>ProductProfilesStore.Save(settings,_profilesPath));_saved=ProductProfilesStore.Copy(settings);_hasSavedBaseline=true;_canvas.Dirty=false;if(_canvas.StartupEnabled)await WriteStartupRegistrationAsync(true,settings.StartMinimized);_canvas.Notice="Perfiles guardados. No se ha aplicado hardware.";});
    private async Task ToggleStartupAsync()
    {
        if(!_canvas.StartupKnown){await CheckStartupRegistrationAsync();return;}
        await RunAsync(async()=>
        {
            var requested=!_canvas.StartupEnabled;
            try
            {
                await WriteStartupRegistrationAsync(requested,_draft.StartMinimized);
                _canvas.StartupEnabled=await ReadStartupRegistrationAsync();
            }
            catch { _canvas.StartupKnown=false;throw; }
            _canvas.StartupKnown=true;
            if(_canvas.StartupEnabled!=requested){_canvas.StartupKnown=false;throw new IOException("Windows no confirmó el estado solicitado del inicio. Vuelve a consultar el registro.");}
            _canvas.Notice=requested?"Inicio con Windows activado.":"Inicio con Windows desactivado.";
        });
    }
    private async Task RunAsync(Func<Task> command)
    {
        if(_closing)return;
        if(_pendingCommands++==0)_commandsDrained=new(TaskCreationOptions.RunContinuationsAsynchronously);_canvas.Busy=true;_canvas.Notice="";_canvas.Invalidate();
        try{await command();}catch(Exception ex){_canvas.Notice=ex.Message;AppLog.Write("Product UI command failed: "+ex);}
        finally{if(--_pendingCommands==0)_commandsDrained?.TrySetResult();_canvas.Busy=_restarting||_pendingCommands>0;if(!IsDisposed)_canvas.Invalidate();}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing)_sessionCooling?.Cancel();
        if(disposing){_hyperXTimer.Dispose();_hyperXPage?.Dispose();_=DisposeHyperXAsync();_externalCoolingPage?.Dispose();_externalCooling?.Suspend();_=DisposeExternalCoolingAsync();}
        if(disposing){ClearSessionEndReason();DisposeUpdateCancellation();_presentationTimer.Dispose();UnregisterPowerNotifications();DetachRuntime();_tray.Visible=false;_tray.ContextMenuStrip?.Dispose();_tray.Dispose();_icons.Dispose();_installationGate?.Dispose();}
        base.Dispose(disposing);
    }
    private void ToggleMaximize()=>WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
    private void ShowFromTray(){Show();WindowState=FormWindowState.Normal;Activate();}
    internal Task RequestExitAsync(){_exitRequested=true;Close();return _shutdown.Task;}
    private async void OnClosing(object? sender,FormClosingEventArgs e)
    {
        if(_disposedRuntime)return;
        if(e.CloseReason is not(CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)&&!_exitRequested){e.Cancel=true;Hide();return;}
        e.Cancel=true;await BeginExitAsync();
    }
    private Task? _exitTask;
    private Task BeginExitAsync(bool closeWindow = true) => _exitTask ??= ExitCoreAsync(closeWindow);
    private async Task ExitCoreAsync(bool closeWindow)
    {
        _sessionCooling?.Cancel();
        _closing=true;_canvas.Busy=true;_canvas.Notice="Liberando ventiladores, CPU/GPU y telemetría…";_canvas.Invalidate();
        try { _updateCancellation.Cancel(); } catch(Exception ex) { AppLog.Write("Product update cancellation failed during exit: "+ex); }
        try { _runtime?.FenceLifecycle("Cierre de la aplicación"); } catch(Exception ex) { AppLog.Write("Product shutdown fence unresolved: "+ex); }
        if(_sessionCooling is{} cooling)await ObserveCoolingPreparationAsync(cooling);
        // A completed coordinator failure must not skip final cleanup. Pending
        // operations still retain ownership and must finish before disposal.
        foreach(var operation in new[]{_automaticRetryTask,_restartTask,_recoveryTask,_updateInstallTask})
        {
            try { if(operation is not null)await operation; }
            catch(Exception ex) { AppLog.Write("Product coordinator failed before final shutdown: "+ex); }
        }
        try {await ShutdownRuntimeAsync();}
        catch(Exception ex){Environment.ExitCode=171;AppLog.Write("Product shutdown unresolved: "+ex);}
        finally
        {
            try { await DisposeHyperXAsync(); await DisposeExternalCoolingAsync(); }
            catch(Exception ex) { Environment.ExitCode=171;AppLog.Write("External cooling shutdown failed: "+ex); }
            finally
            {
                // HP resources have already been drained. An explicitly requested
                // OS task mutation retains installer exclusion until it actually
                // returns, even if its optional UI wait budget expired earlier.
                var registration=WindowsStartupRegistration.PendingChange;
                if(!registration.IsCompleted)_canvas.Notice="Control liberado. Windows aún está confirmando el cambio de inicio…";
                try{await registration;}catch(Exception error){AppLog.Write("Startup registration completed with error during exit: "+error.GetType().Name);}
                _disposedRuntime=true;_tray.Visible=false;try { if(closeWindow)Close(); } finally { _shutdown.TrySetResult(); }
            }
        }
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x11) // Optional AC cooling precedes acknowledgement; cleanup belongs to WM_ENDSESSION.
        {
            PrepareWindowsSessionEnd(m.LParam.ToInt64());m.Result=(IntPtr)1;return;
        }
        if(m.Msg==0x16) // WM_ENDSESSION: do not return with only an async-void cleanup scheduled.
        {
            if(m.WParam!=IntPtr.Zero)
            {_sessionEndConfirmed=true;_sessionCooling?.Cancel();RegisterSessionEndReason();WaitForWindowsSessionEnd(TimeSpan.FromSeconds(20));}
            else CancelWindowsSessionEnd();
            ClearSessionEndReason();m.Result=IntPtr.Zero;return;
        }
        if(m.Msg==0x84&&FormBorderStyle==FormBorderStyle.None&&WindowState!=FormWindowState.Maximized)
        {
            var p=PointToClient(new((short)(m.LParam.ToInt64()&0xFFFF),(short)((m.LParam.ToInt64()>>16)&0xFFFF)));var edge=Math.Max(6,DeviceDpi/12);
            bool l=p.X<edge,r=p.X>=ClientSize.Width-edge,t=p.Y<edge,b=p.Y>=ClientSize.Height-edge;
            int code=l&&t?13:r&&t?14:l&&b?16:r&&b?17:l?10:r?11:t?12:b?15:1;if(code!=1){m.Result=(IntPtr)code;return;}
        }
        if(m.Msg==0x218)
        {
            var code=m.WParam.ToInt32();int? display=null;
            if(code==0x8013&&m.LParam!=IntPtr.Zero)
            {
                var setting=Marshal.PtrToStructure<PowerSetting>(m.LParam);
                if(setting.Id==SessionDisplay&&setting.Length>=4)display=Marshal.ReadInt32(m.LParam,Marshal.SizeOf<PowerSetting>());
            }
            var operation=HandlePowerEventAsync(code,display);
            // Admission is fenced synchronously; the retained task still owns
            // native drainage. This acknowledgement never asserts release.
            _ = ObservePowerTaskAsync(operation);
        }
        base.WndProc(ref m);
        if(m.Msg==0x24)ApplyFixtureTrackingLimits(m.LParam); // WM_GETMINMAXINFO; isolated display fixtures only.
        if(m.Msg==0x7E||m.Msg==0x1A)QueueDisplayRefresh(m.Msg==0x7E?"resolution":"desktop-settings");
    }
    internal Task HandlePowerEventAsync(int code,int? display=null)
    {
        if(code==4)_systemSuspended=true;
        else if(code is 6 or 7 or 18)_systemSuspended=false;
        try{ExternalCoolingPowerEvent(code);}
        catch(Exception error){AppLog.Write("External power notification failed; internal fence still required: "+error.GetType().Name);}
        var wasDisplayOff=_displayOff;
        if(code==4||code==0x8013&&display==0)_updateLifecycleBoundary++;
        if(code==0x8013&&display==0)_displayOff=true;
        else if(code==0x8013&&display==1)_displayOff=false;
        if(_restarting)
        {
            if(code==4||code==0x8013&&display==0)_restartLifecycleInterrupted=true;
            // Cleanup owns the old runtime now; do not queue release/resume against a disposed service.
            return Task.CompletedTask;
        }
        if(_closing||_runtime is null)return Task.CompletedTask;
        if(code==4||code==0x8013&&display==0)_startupAutomatic?.Pause("Automático en pausa por suspensión o pantalla apagada; se necesitan muestras nuevas.");
        if(code==0x8013)
        {
            if(display==0&&!wasDisplayOff){_displayOff=true;FencePowerBoundary("SESSION_DISPLAY_STATUS/Off");return QueueLifecycleRelease("Display Off lifecycle");}
            if(display==1&&wasDisplayOff){_displayOff=false;return ResumeAfterReleaseAsync();}
        }
        else if(code==4){FencePowerBoundary("PBT_APMSUSPEND");return QueueLifecycleRelease("System suspend");}
        else if(code is 6 or 7 or 18){if(!_displayOff)return ResumeAfterReleaseAsync();}
        return Task.CompletedTask;
    }
    private Task QueueLifecycleRelease(string reason)
    {
        if(!_lifecycleRelease.IsCompleted||_lifecycleRelease.IsFaulted)return _lifecycleRelease;
        var previous=_lifecycleRelease;var runtime=_runtime!;
        // Each boundary fences immediately; all queued releases finish before resume or exit.
        return _lifecycleRelease=Task.Run(async()=>
        {
            try{await previous;await runtime.ReleaseForLifecycleAsync(reason);}
            catch(Exception ex){AppLog.Write("Product lifecycle release failed: "+ex);UpdateState(runtime.State with{LifecycleBlocked=true,Failure="Liberación lifecycle no resuelta: "+ex.Message});throw;}
        });
    }
    private void FencePowerBoundary(string reason)
    {
        try{_runtime!.FenceLifecycle(reason);}
        catch(Exception error)
        {
            _powerFenceFailure=true;_canvas.State=_canvas.State with{Runtime="Failed",LifecycleBlocked=true,Failure="No se confirmó el bloqueo de energía: "+error.Message};
            AppLog.Write("Power fence failed; normal release still requested: "+error.GetType().Name);
        }
        _minimumRuntimePublication=Math.Max(_minimumRuntimePublication,_runtime!.State.PublicationSequence);
        _runtimeMailbox?.DiscardBefore(_minimumRuntimePublication);
    }
    private long _lastResumeBoundary=-1,_lastResumeTick;
    private static async Task ObservePowerTaskAsync(Task operation)
    {try{await operation;}catch(Exception error){AppLog.Write("Power lifecycle operation remains unresolved: "+error.GetType().Name);}}
    private async Task ResumeAfterReleaseAsync()
    {
        var runtime=_runtime;var boundary=_updateLifecycleBoundary;
        try{await _lifecycleRelease;}catch(Exception error){AppLog.Write("Resume fenced by unresolved release: "+error.GetType().Name);return;}
        var now=Environment.TickCount64;
        if(_closing||_displayOff||_systemSuspended||_powerFenceFailure||!ReferenceEquals(runtime,_runtime)||boundary!=_updateLifecycleBoundary||
            _lastResumeBoundary==_updateLifecycleBoundary&&now-_lastResumeTick<10000)return;
        _lastResumeBoundary=_updateLifecycleBoundary;
        _lastResumeTick=now;
        _runtime?.ResumeTelemetry("Interactive resume");
        if(_startupAutomatic is {Finished:false}&&_startupPreferences is {} preferences)
            await RetryAutomaticAsync(applyProtections:true,unattended:true,preferences:preferences);
    }
    private static readonly Guid SessionDisplay=new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    [StructLayout(LayoutKind.Sequential)]private struct PowerSetting{internal Guid Id;internal uint Length;}
    private void RegisterPowerNotifications()
    {
        if(_isolatedRuntime&&PowerRegistrationFixture is { } fixture){fixture();return;}
        if(_suspendRegistration!=IntPtr.Zero&&_displayRegistration!=IntPtr.Zero)return;
        UnregisterPowerNotifications();
        try
        {
            _suspendRegistration=RegisterSuspendResumeNotification(Handle,0);
            if(_suspendRegistration==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"No se pudieron registrar las notificaciones de suspensión/reanudación.");
            var id=SessionDisplay;_displayRegistration=RegisterPowerSettingNotification(Handle,ref id,0);
            if(_displayRegistration==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"No se pudieron registrar las notificaciones de pantalla.");
        }
        catch{UnregisterPowerNotifications();throw;}
    }
    private void UnregisterPowerNotifications(){if(_displayRegistration!=IntPtr.Zero){UnregisterPowerSettingNotification(_displayRegistration);_displayRegistration=IntPtr.Zero;}if(_suspendRegistration!=IntPtr.Zero){UnregisterSuspendResumeNotification(_suspendRegistration);_suspendRegistration=IntPtr.Zero;}}
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,int msg,int w,int l);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr RegisterPowerSettingNotification(IntPtr h,ref Guid id,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnregisterPowerSettingNotification(IntPtr h);
    [DllImport("user32.dll",ExactSpelling=true,SetLastError=true)]private static extern IntPtr RegisterSuspendResumeNotification(IntPtr h,uint flags);
    [DllImport("user32.dll",ExactSpelling=true,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool UnregisterSuspendResumeNotification(IntPtr h);
}

internal sealed class ProductNumericDialog : Form
{
    internal TextBox Input {get;}=new(){Dock=DockStyle.Fill,AccessibleName="Valor exacto"};
    private readonly Label _error=new(){Dock=DockStyle.Fill,ForeColor=ProductCanvas.Yellow,AutoSize=true};
    private readonly Func<string,string?> _commit;
    internal ProductNumericDialog(string title,double value,double min,double max,Func<string,string?> commit,bool integer=true,string? help=null)
    {
        SuspendLayout();
        _commit=commit;Text=title;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new(460,245);BackColor=ProductCanvas.Background;ForeColor=ProductCanvas.Ink;Font=new("Segoe UI",10);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(20),ColumnCount=1,RowCount=5};
        layout.RowStyles.Add(new(SizeType.Absolute,34));layout.RowStyles.Add(new(SizeType.Absolute,34));
        layout.RowStyles.Add(new(SizeType.Absolute,42));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,36));
        layout.Controls.Add(new Label{Text=$"Valor {(integer?"entero":"numérico")} · {min}–{max}",Dock=DockStyle.Fill},0,0);
        Input.Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture);layout.Controls.Add(Input,0,1);
        layout.Controls.Add(_error,0,2);
        layout.Controls.Add(new Label{Text=help??"Editar solo cambia el borrador.",Dock=DockStyle.Fill,ForeColor=ProductCanvas.Muted},0,3);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var accept=new Button{Text="Aceptar",AutoSize=true};var cancel=new Button{Text="Cancelar",AutoSize=true,DialogResult=DialogResult.Cancel};
        accept.Click+=(_,_)=>TryCommit();buttons.Controls.Add(cancel);buttons.Controls.Add(accept);layout.Controls.Add(buttons,0,4);
        AcceptButton=accept;CancelButton=cancel;Controls.Add(layout);Shown+=(_,_)=>{Input.Focus();Input.SelectAll();};
        ResumeLayout(false);PerformLayout();
    }
    internal bool TryCommit()
    {
        var error=_commit(Input.Text);_error.Text=error??"";
        if(error is not null){Input.Focus();Input.SelectAll();return false;}
        DialogResult=DialogResult.OK;return true;
    }
}
