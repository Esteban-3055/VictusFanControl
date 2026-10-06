using System.Diagnostics;
using System.Runtime.InteropServices;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

/// <summary>New normal product GUI. Legacy MainForm is reachable only through explicit qualification entries.</summary>
internal sealed class ProductForm : Form
{
    private readonly ProductCanvas _canvas = new();
    private readonly string _modules;
    private readonly ProductAutomaticReviewMode? _automaticReview;
    private IProductRuntime? _runtime;
    private ProductProfiles _draft;
    private ProductProfiles _saved;
    private bool _hasSavedBaseline;
    private readonly string? _profilesPath;
    private readonly System.Windows.Forms.Timer _presentationTimer = new() { Interval = 1000 };
    private TaskCompletionSource? _commandsDrained;
    private readonly NotifyIcon _tray;
    private bool _closing, _exitRequested, _disposedRuntime, _creating;
    private IntPtr _displayRegistration, _suspendRegistration;
    private bool _displayOff;
    private Task _lifecycleRelease = Task.CompletedTask;
    private int _pendingCommands;
    private Task _startup = Task.CompletedTask;
    private string? _startupFailure;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ProductCanvas Canvas => _canvas;
    internal ProductProfiles Draft => ProductProfilesStore.Copy(_draft);
    internal bool Dirty => _canvas.Dirty;
    internal ProductForm(string modules,bool minimized=false,IProductRuntime? fixture=null,ProductProfiles? fixtureProfiles=null,Func<Task<IProductRuntime>>? runtimeFactory=null,string? profilesPath=null,bool registerPowerNotificationsInFixture=false,ProductAutomaticReviewMode? automaticReview=null)
    {
        _modules=modules;_automaticReview=automaticReview;_runtime=fixture;_profilesPath=profilesPath;
        string? notice=null;
        _draft=fixtureProfiles is null?ProductProfilesStore.Load(profilesPath,out notice,Migrate):ProductProfilesStore.Copy(fixtureProfiles);
        _saved=ProductProfilesStore.Copy(_draft);
        _hasSavedBaseline=fixtureProfiles is not null||(File.Exists(profilesPath??ProductProfilesStore.DefaultPath)&&notice is null);
        _presentationTimer.Tick+=(_,_)=>PresentationTick();
        Text="VictusFanControl";FormBorderStyle=FormBorderStyle.None;BackColor=ProductCanvas.Background;AutoScaleMode=AutoScaleMode.Dpi;
        MinimumSize=new(1040,660);ClientSize=new(1344,756);StartPosition=FormStartPosition.CenterScreen;
        _canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=notice is not null;_canvas.Notice=notice??"";Controls.Add(_canvas);
        _canvas.Command+=HandleCommand;_canvas.ValueEdited+=EditValue;_canvas.NodeEdited+=EditNode;
        _canvas.MouseDown+=(_,e)=>{if(e.Button==MouseButtons.Left&&_canvas.IsHeaderDrag(e.Location)){ReleaseCapture();SendMessage(Handle,0xA1,2,0);}};
        _canvas.MouseDoubleClick+=(_,e)=>{if(_canvas.IsHeaderDrag(e.Location))ToggleMaximize();};
        var menu=new ContextMenuStrip();menu.Items.Add("Abrir VictusFanControl",null,(_,_)=>ShowFromTray());
        menu.Items.Add("Volver a Firmware",null,async(_,_)=>await RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,Draft)??Task.CompletedTask));
        menu.Items.Add("Liberar CPU / GPU",null,async(_,_)=>await RunAsync(()=>_runtime?.ReleasePerformanceAsync()??Task.CompletedTask));
        menu.Items.Add("Salir",null,(_,_)=>{_exitRequested=true;Close();});
        _tray=new(){Icon=SystemIcons.Application,Text="VictusFanControl · Firmware",ContextMenuStrip=menu,Visible=fixture is null&&runtimeFactory is null};
        _tray.DoubleClick+=(_,_)=>ShowFromTray();
        Shown+=(_,_)=>
        {
            if(_creating)return;_creating=true;
            _startup=InitializeAsync(minimized,fixture is not null||runtimeFactory is not null,runtimeFactory,registerPowerNotificationsInFixture);
        };
        FormClosing+=OnClosing;FormClosed+=(_,_)=>{UnregisterPowerNotifications();_tray.Visible=false;};
    }
    private async Task InitializeAsync(bool minimized,bool isolated,Func<Task<IProductRuntime>>? runtimeFactory,bool registerPowerNotificationsInFixture)
    {
        try
        {
            if(_runtime is null)_runtime=await (runtimeFactory?.Invoke()??Task.Run<IProductRuntime>(()=>new ProductRuntime(_modules,Draft,_automaticReview)));
            // A close during construction waits for this task, then disposes the returned service without starting it.
            if(_closing||IsDisposed)return;
            _runtime.Changed+=UpdateState;UpdateState(_runtime.State);
            if(!isolated||registerPowerNotificationsInFixture)RegisterPowerNotifications();
            _runtime.Start();_presentationTimer.Start();
            if(!isolated){_canvas.StartupEnabled=await WindowsStartupRegistration.IsEnabledAsync();_canvas.StartupKnown=true;}
            if(!_closing&&!IsDisposed&&(minimized||_draft.StartMinimized))Hide();
        }
        catch(Exception ex)
        {
            _startupFailure="No se pudo iniciar: "+ex.Message;
            if(_runtime is not null){_runtime.FenceLifecycle("Startup incompleto");_lifecycleRelease=Task.Run(()=>_runtime.ReleaseForLifecycleAsync("Startup incompleto"));}
            AppLog.Write("Product GUI startup: "+ex);
            if(!_closing&&!IsDisposed)UpdateState(_runtime?.State??_canvas.State);
        }
        finally{if(!IsDisposed)_canvas.Invalidate();}
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
        if(_startupFailure is not null&&state.Failure is null)state=state with{Failure=_startupFailure};
        _canvas.State=state;if(state.Snapshot is not null)_canvas.AddSnapshot(state.Snapshot);
        _tray.Text=("VictusFanControl · "+state.FanMode+" · "+state.FanAuthority)[..Math.Min(63,("VictusFanControl · "+state.FanMode+" · "+state.FanAuthority).Length)];
        if(state.Failure is not null)_canvas.Notice=state.Failure;
        _canvas.Invalidate();
    }
    internal void HandleCommand(string id)
    {
        if(_closing||IsDisposed)return;
        if(id.StartsWith("page-")){_canvas.Page=(ProductPage)int.Parse(id[5..]);_canvas.SelectedNode=-1;_canvas.Invalidate();return;}
        if(id is "profile-ac" or "profile-battery") {_canvas.Editing=id=="profile-ac"?ProductPowerProfile.Ac:ProductPowerProfile.Battery;_canvas.SelectedNode=-1;ResetSimulation();_canvas.Invalidate();return;}
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
        if(id=="fan-mode-2"&&_canvas.State.LifecycleBlocked)return;
        if(id=="manual-apply"&&(!_canvas.State.ManualAuthorized||_canvas.State.FanMode!="Manual"||_canvas.State.Runtime!="Healthy"||_canvas.State.LifecycleBlocked))return;
        if(id=="performance-apply"&&(!_canvas.State.PerformanceSupported||!_canvas.State.CanApplyPerformance||!(_draft.CpuEnabled||_draft.GpuEnabled)))return;
        if(id=="performance-release"&&!_canvas.State.PerformanceProcessPresent)return;
        if(id is "pl1-text" or "pl2-text" or "gpu-text")
        {
            var key=id[..^5];var p=_draft.Get(_canvas.Editing);var range=NumericRange(key);
            var title=key switch{"pl1"=>"CPU PL1 (W)","pl2"=>"CPU PL2 (W)",_=>"GPU máximo (MHz)"};
            var value=key switch{"pl1"=>p.CpuPl1Watts,"pl2"=>p.CpuPl2Watts,_=>p.GpuMaximumMHz};
            using var dialog=new ProductNumericDialog(title+" · "+(_canvas.Editing==ProductPowerProfile.Ac?"AC":"Batería"),value,range.Min,range.Max,
                text=>TryEditNumericValue(key,text,out var error)?null:error);
            dialog.ShowDialog(this);_canvas.Invalidate();return;
        }
        switch(id)
        {
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
            case "minimized-toggle":Change(_draft with{StartMinimized=!_draft.StartMinimized});break;
            case "save":_ = SaveAsync();break;
            case "curve-apply":
                if(!_canvas.CanApplyCurve){_canvas.Notice="Selecciona el perfil de la fuente real y espera a que Automatic esté controlando.";break;}
                var source=_canvas.Editing;var demand=Draft.Get(source).Fan.UnifiedDemand!;
                _ = RunAsync(async()=>{if(_runtime is null)throw new InvalidOperationException("Runtime no disponible.");await _runtime.ApplyFanCurveAsync(source,demand);
                    _canvas.Notice="Curva e influencias aplicadas en Automatic. Guardar conserva los cambios; CPU/GPU mantienen sus límites actuales.";});break;
            case "discard":_draft=ProductProfilesStore.Copy(_saved);_canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=!_hasSavedBaseline;_canvas.SelectedNode=-1;_canvas.Notice=_hasSavedBaseline?"Se recuperaron las preferencias guardadas.":"Se recuperó la configuración inicial; falta guardarla.";break;
            case "startup-toggle":_ = ToggleStartupAsync();break;
            case "firmware":case "fan-mode-0":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,Draft)??Task.CompletedTask);break;
            case "fan-mode-1":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Manual,Draft)??Task.CompletedTask);break;
            case "fan-mode-2":
                if(!_canvas.State.AutomaticAuthorized){_canvas.Notice="Automatic normal sigue cerrado hasta su calificación.";break;}
                if(_canvas.State.FanMode=="Automatic"){_canvas.Notice="Automatic ya está seleccionado; usa Aplicar en Curvas para actualizar curva e influencias. El plazo no se renueva.";break;}
                _ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,Draft)??Task.CompletedTask);break;
            case "manual-apply":_ = RunAsync(()=>_runtime?.ApplyManualAsync(_canvas.ManualLevel)??Task.CompletedTask);break;
            case "performance-apply":_ = RunAsync(()=>{var profiles=Draft;profiles.Validate();profiles.PerformanceConfiguration().Validate();return _runtime?.ApplyPerformanceAsync(profiles)??Task.CompletedTask;});break;
            case "performance-release":_ = RunAsync(()=>_runtime?.ReleasePerformanceAsync()??Task.CompletedTask);break;
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
        if(_canvas.Busy||_closing||IsDisposed)return;next.Validate();_draft=next;_canvas.Profiles=_draft;ResetSimulation();_canvas.Dirty=true;_canvas.Notice="Cambios en edición; no aplicados al hardware.";_canvas.Invalidate();
    }
    internal void EditValue(string key,int value)
    {
        if(_canvas.Busy||_closing||IsDisposed)return;
        if(key.StartsWith("sim-input-")){_canvas.SimulationInputs=_canvas.SimulationInputs.With(int.Parse(key[10..]),value);_canvas.Invalidate();return;}
        var slot=_canvas.Editing;var p=_draft.Get(slot);
        if(key.StartsWith("influence-"))
        {
            var axis=int.Parse(key[10..]);if(axis is <0 or >5)return;
            var model=p.Fan.UnifiedDemand!;var nextModel=model.WithInfluence(axis,Math.Clamp(value,axis<2?100:0,axis<2?150:100));
            Change(_draft.With(slot,p with{Fan=p.Fan with{UnifiedDemand=nextModel}}));return;
        }
        if(key=="manual") {_canvas.ManualLevel=Math.Clamp(value,10,50);_canvas.Invalidate();return;}
        var next=key switch
        {
            "pl1"=>p with{CpuPl1Watts=Math.Clamp(value,CpuPowerProductDefaults.MinimumPl1Watts,CpuPowerProductDefaults.MaximumConfigurablePl1Watts),CpuPl2Watts=Math.Max(p.CpuPl2Watts,Math.Clamp(value,8,44))},
            "pl2"=>p with{CpuPl2Watts=Math.Clamp(value,Math.Max(p.CpuPl1Watts,CpuPowerProductDefaults.MinimumPl2Watts),CpuPowerProductDefaults.MaximumConfigurablePl2Watts)},
            "gpu"=>p with{GpuMaximumMHz=Math.Clamp(value,GpuProductPreferences.MinimumMHz,GpuProductPreferences.Maximum(slot))},
            _=>p
        };
        Change(_draft.With(slot,next));
    }
    private (int Min,int Max) NumericRange(string key)=>key switch
    {
        "pl1"=>(CpuPowerProductDefaults.MinimumPl1Watts,CpuPowerProductDefaults.MaximumConfigurablePl1Watts),
        "pl2"=>(Math.Max(_draft.Get(_canvas.Editing).CpuPl1Watts,CpuPowerProductDefaults.MinimumPl2Watts),CpuPowerProductDefaults.MaximumConfigurablePl2Watts),
        "gpu"=>(GpuProductPreferences.MinimumMHz,GpuProductPreferences.Maximum(_canvas.Editing)),
        _=>throw new ArgumentException("Campo numérico desconocido.",nameof(key))
    };
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
        var lowLevel=index==0?10:points[index-1].Level;var highLevel=index==points.Length-1?50:points[index+1].Level;
        var x=index==0?0:index==points.Length-1?100:Math.Clamp(Math.Round(input),lo,hi);
        var y=index==points.Length-1?50:Math.Clamp(level,(int)lowLevel,(int)highLevel);
        points[index]=new(x,y);
        Change(_draft.With(slot,p with{Fan=p.Fan with{UnifiedDemand=model with{Curve=points}}}));
    }
    private void AddNode()
    {
        var p=_draft.Get(_canvas.Editing);var model=p.Fan.UnifiedDemand!;var points=model.Curve.ToList();if(points.Count>=64)return;
        var gap=Enumerable.Range(0,points.Count-1).OrderByDescending(i=>points[i+1].Input-points[i].Input).First();
        if(points[gap+1].Input-points[gap].Input<2){_canvas.Notice="No queda espacio entre puntos.";return;}
        var x=Math.Floor((points[gap].Input+points[gap+1].Input)/2);var y=Math.Round(AdaptiveCurveProfiles.Interpolate(points,x));points.Insert(gap+1,new(x,y));
        Change(_draft.With(_canvas.Editing,p with{Fan=p.Fan with{UnifiedDemand=model with{Curve=points.ToArray()}}}));_canvas.SelectedNode=gap+1;_canvas.ShowCurvePoints=true;
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
        if(_closing)return;imported.Validate();_draft=imported;_canvas.Profiles=_draft;ResetSimulation();_canvas.SelectedNode=-1;_canvas.Dirty=true;_canvas.Notice="Perfiles importados en edición; falta Guardar. No se aplicó hardware.";
    });
    internal Task ExportDiagnosticsAsync(string path,string? fixtureLog=null)=>RunAsync(async()=>{var state=_canvas.State;var draft=Draft;await Task.Run(()=>ProductDiagnostics.Export(path,state,draft,fixtureLog??AppLog.CurrentLogPath));_canvas.Notice="Diagnóstico exportado. Revisa rutas locales antes de compartirlo.";});
    internal void PresentationTick()
    {
        if(_closing||IsDisposed)return;
        // Only the visible offline simulator consumes virtual time. No catch-up
        // on returning from the tray, another page or the editor.
        if(Visible&&WindowState!=FormWindowState.Minimized&&!_canvas.Busy&&_canvas.Page==ProductPage.Curves&&_canvas.SimulationVisible&&_canvas.SimulationRunning)
        {
            try{_canvas.Simulation.Advance(_canvas.SimulationInputs,1);}
            catch(Exception ex){_canvas.SimulationRunning=false;_canvas.Notice=ex.Message;}
        }
        _canvas.Invalidate();
    }
    private void ResetSimulation()=>_canvas.Simulation=new(_draft.Get(_canvas.Editing).Fan);
    private async Task SaveAsync() => await RunAsync(async()=>{var settings=Draft;await Task.Run(()=>ProductProfilesStore.Save(settings,_profilesPath));_saved=ProductProfilesStore.Copy(settings);_hasSavedBaseline=true;_canvas.Dirty=false;if(_canvas.StartupEnabled)await WindowsStartupRegistration.SetEnabledAsync(true,_modules,settings.StartMinimized);_canvas.Notice="Perfiles guardados. No se ha aplicado hardware.";});
    private async Task ToggleStartupAsync() => await RunAsync(async()=>{var requested=!_canvas.StartupEnabled;await WindowsStartupRegistration.SetEnabledAsync(requested,_modules,_draft.StartMinimized);_canvas.StartupEnabled=await WindowsStartupRegistration.IsEnabledAsync();_canvas.Notice="Registro de inicio actualizado; el inicio permanece en Firmware.";});
    private async Task RunAsync(Func<Task> command)
    {
        if(_closing)return;
        if(_pendingCommands++==0)_commandsDrained=new(TaskCreationOptions.RunContinuationsAsynchronously);_canvas.Busy=true;_canvas.Notice="";_canvas.Invalidate();
        try{await command();}catch(Exception ex){_canvas.Notice=ex.Message;AppLog.Write("Product UI command failed: "+ex);}
        finally{if(--_pendingCommands==0)_commandsDrained?.TrySetResult();_canvas.Busy=_pendingCommands>0;if(!IsDisposed)_canvas.Invalidate();}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){_presentationTimer.Dispose();UnregisterPowerNotifications();if(_runtime is not null)_runtime.Changed-=UpdateState;_tray.Visible=false;_tray.ContextMenuStrip?.Dispose();_tray.Dispose();}
        base.Dispose(disposing);
    }
    private void ToggleMaximize()=>WindowState=WindowState==FormWindowState.Maximized?FormWindowState.Normal:FormWindowState.Maximized;
    private void ShowFromTray(){Show();WindowState=FormWindowState.Normal;Activate();}
    internal Task RequestExitAsync(){_exitRequested=true;Close();return _shutdown.Task;}
    private async void OnClosing(object? sender,FormClosingEventArgs e)
    {
        if(_disposedRuntime)return;
        if(e.CloseReason is not(CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)&&!_exitRequested){e.Cancel=true;Hide();return;}
        e.Cancel=true;if(_closing)return;_closing=true;_canvas.Busy=true;_canvas.Notice="Liberando ventiladores, CPU/GPU y telemetría…";_canvas.Invalidate();
        try {await _startup;if(_commandsDrained is not null)await _commandsDrained.Task;await _lifecycleRelease;if(_runtime is not null)await Task.Run(async()=>await _runtime.DisposeAsync());}
        catch(Exception ex){Environment.ExitCode=171;AppLog.Write("Product shutdown unresolved: "+ex);}
        finally{_disposedRuntime=true;_tray.Visible=false;Close();_shutdown.TrySetResult();}
    }
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==0x84&&FormBorderStyle==FormBorderStyle.None&&WindowState!=FormWindowState.Maximized)
        {
            var p=PointToClient(new((short)(m.LParam.ToInt64()&0xFFFF),(short)((m.LParam.ToInt64()>>16)&0xFFFF)));var edge=Math.Max(6,DeviceDpi/12);
            bool l=p.X<edge,r=p.X>=ClientSize.Width-edge,t=p.Y<edge,b=p.Y>=ClientSize.Height-edge;
            int code=l&&t?13:r&&t?14:l&&b?16:r&&b?17:l?10:r?11:t?12:b?15:1;if(code!=1){m.Result=(IntPtr)code;return;}
        }
        if(m.Msg==0x218&&_runtime is not null)
        {
            var code=m.WParam.ToInt32();int? display=null;
            if(code==0x8013&&m.LParam!=IntPtr.Zero)
            {
                var setting=Marshal.PtrToStructure<PowerSetting>(m.LParam);
                if(setting.Id==SessionDisplay&&setting.Length>=4)display=Marshal.ReadInt32(m.LParam,Marshal.SizeOf<PowerSetting>());
            }
            var operation=HandlePowerEventAsync(code,display);
            if(code==4)operation.GetAwaiter().GetResult();
        }
        base.WndProc(ref m);
    }
    internal Task HandlePowerEventAsync(int code,int? display=null)
    {
        if(_closing||_runtime is null)return Task.CompletedTask;
        if(code==0x8013)
        {
            if(display==0&&!_displayOff){_displayOff=true;_runtime.FenceLifecycle("SESSION_DISPLAY_STATUS/Off");return QueueLifecycleRelease("Display Off lifecycle");}
            if(display==1&&_displayOff){_displayOff=false;return ResumeAfterReleaseAsync();}
        }
        else if(code==4){_runtime.FenceLifecycle("PBT_APMSUSPEND");return QueueLifecycleRelease("System suspend");}
        else if(code is 6 or 7 or 18){if(!_displayOff)return ResumeAfterReleaseAsync();}
        return Task.CompletedTask;
    }
    private Task QueueLifecycleRelease(string reason)
    {
        var previous=_lifecycleRelease;var runtime=_runtime!;
        // Each boundary fences immediately; all queued releases finish before resume or exit.
        return _lifecycleRelease=Task.Run(async()=>
        {
            try{await previous;await runtime.ReleaseForLifecycleAsync(reason);}
            catch(Exception ex){AppLog.Write("Product lifecycle release failed: "+ex);UpdateState(runtime.State with{LifecycleBlocked=true,Failure="Liberación lifecycle no resuelta: "+ex.Message});}
        });
    }
    private async Task ResumeAfterReleaseAsync()
    {
        await _lifecycleRelease;
        if(!_closing&&!_displayOff)_runtime?.ResumeTelemetry("Interactive resume");
    }
    private static readonly Guid SessionDisplay=new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    [StructLayout(LayoutKind.Sequential)]private struct PowerSetting{internal Guid Id;internal uint Length;}
    private void RegisterPowerNotifications()
    {
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
    internal ProductNumericDialog(string title,int value,int min,int max,Func<string,string?> commit)
    {
        _commit=commit;Text=title;StartPosition=FormStartPosition.CenterParent;FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;MinimizeBox=false;ShowInTaskbar=false;AutoScaleMode=AutoScaleMode.Dpi;
        ClientSize=new(460,245);BackColor=ProductCanvas.Background;ForeColor=ProductCanvas.Ink;Font=new("Segoe UI",10);
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new(20),ColumnCount=1,RowCount=5};
        layout.RowStyles.Add(new(SizeType.Absolute,34));layout.RowStyles.Add(new(SizeType.Absolute,34));
        layout.RowStyles.Add(new(SizeType.Absolute,42));layout.RowStyles.Add(new(SizeType.Percent,100));layout.RowStyles.Add(new(SizeType.Absolute,36));
        layout.Controls.Add(new Label{Text=$"Valor entero · {min}–{max}",Dock=DockStyle.Fill},0,0);
        Input.Text=value.ToString(System.Globalization.CultureInfo.InvariantCulture);layout.Controls.Add(Input,0,1);
        layout.Controls.Add(_error,0,2);
        layout.Controls.Add(new Label{Text="Editar solo cambia el borrador. Si aumentas PL1 por encima de PL2, PL2 sube al mismo valor.",Dock=DockStyle.Fill,ForeColor=ProductCanvas.Muted},0,3);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft};
        var accept=new Button{Text="Aceptar",AutoSize=true};var cancel=new Button{Text="Cancelar",AutoSize=true,DialogResult=DialogResult.Cancel};
        accept.Click+=(_,_)=>TryCommit();buttons.Controls.Add(cancel);buttons.Controls.Add(accept);layout.Controls.Add(buttons,0,4);
        AcceptButton=accept;CancelButton=cancel;Controls.Add(layout);Shown+=(_,_)=>{Input.Focus();Input.SelectAll();};
    }
    internal bool TryCommit()
    {
        var error=_commit(Input.Text);_error.Text=error??"";
        if(error is not null){Input.Focus();Input.SelectAll();return false;}
        DialogResult=DialogResult.OK;return true;
    }
}
