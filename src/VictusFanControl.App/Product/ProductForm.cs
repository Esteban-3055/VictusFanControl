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
    private IProductRuntime? _runtime;
    private ProductProfiles _draft;
    private readonly NotifyIcon _tray;
    private bool _closing, _exitRequested, _disposedRuntime, _creating;
    private IntPtr _displayRegistration, _suspendRegistration;
    private bool _displayOff;
    private Task _lifecycleRelease = Task.CompletedTask;
    private int _pendingCommands;
    private Task _startup = Task.CompletedTask;
    private readonly TaskCompletionSource _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ProductCanvas Canvas => _canvas;
    internal ProductProfiles Draft => ProductProfilesStore.Copy(_draft);
    internal bool Dirty => _canvas.Dirty;
    internal ProductForm(string modules,bool minimized=false,IProductRuntime? fixture=null,ProductProfiles? fixtureProfiles=null,Func<Task<IProductRuntime>>? runtimeFactory=null)
    {
        _modules=modules;_runtime=fixture;
        string? notice=null;
        _draft=fixtureProfiles is null?ProductProfilesStore.Load(null,out notice,Migrate):ProductProfilesStore.Copy(fixtureProfiles);
        Text="VictusFanControl";FormBorderStyle=FormBorderStyle.None;BackColor=ProductCanvas.Background;AutoScaleMode=AutoScaleMode.Dpi;
        MinimumSize=new(1040,660);ClientSize=new(1344,756);StartPosition=FormStartPosition.CenterScreen;
        _canvas.Profiles=_draft;_canvas.Notice=notice??"";Controls.Add(_canvas);
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
            _startup=InitializeAsync(minimized,fixture is not null||runtimeFactory is not null,runtimeFactory);
        };
        FormClosing+=OnClosing;FormClosed+=(_,_)=>{UnregisterPowerNotifications();_tray.Visible=false;};
    }
    private async Task InitializeAsync(bool minimized,bool isolated,Func<Task<IProductRuntime>>? runtimeFactory)
    {
        try
        {
            if(_runtime is null)_runtime=await (runtimeFactory?.Invoke()??Task.Run<IProductRuntime>(()=>new ProductRuntime(_modules,Draft)));
            // A close during construction waits for this task, then disposes the returned service without starting it.
            if(_closing||IsDisposed)return;
            _runtime.Changed+=UpdateState;UpdateState(_runtime.State);
            if(!isolated)RegisterPowerNotifications();
            _runtime.Start();
            if(!isolated){_canvas.StartupEnabled=await WindowsStartupRegistration.IsEnabledAsync();_canvas.StartupKnown=true;}
            if(!_closing&&!IsDisposed&&(minimized||_draft.StartMinimized))Hide();
        }
        catch(Exception ex)
        {
            if(_runtime is not null){_runtime.FenceLifecycle("Startup incompleto");_lifecycleRelease=Task.Run(()=>_runtime.ReleaseForLifecycleAsync("Startup incompleto"));}
            AppLog.Write("Product GUI startup: "+ex);
            if(!_closing&&!IsDisposed)_canvas.Notice="No se pudo iniciar: "+ex.Message;
        }
        finally{if(!IsDisposed)_canvas.Invalidate();}
    }
    private static ProductProfiles Migrate()
    {
        var previous=FanConfigurationStore.Load(null,out _);var performance=PerformanceUiSettingsStore.Load();
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
        _canvas.State=state;if(state.Snapshot is not null)_canvas.AddSnapshot(state.Snapshot);
        _tray.Text=("VictusFanControl · "+state.FanMode+" · "+state.FanAuthority)[..Math.Min(63,("VictusFanControl · "+state.FanMode+" · "+state.FanAuthority).Length)];
        if(state.Failure is not null)_canvas.Notice=state.Failure;
        _canvas.Invalidate();
    }
    internal void HandleCommand(string id)
    {
        if(_closing||IsDisposed)return;
        if(id.StartsWith("page-")){_canvas.Page=(ProductPage)int.Parse(id[5..]);_canvas.SelectedNode=-1;_canvas.Invalidate();return;}
        if(id is "profile-ac" or "profile-battery") {_canvas.Editing=id=="profile-ac"?ProductPowerProfile.Ac:ProductPowerProfile.Battery;_canvas.SelectedNode=-1;_canvas.Invalidate();return;}
        if(id.StartsWith("axis-")){_canvas.Axis=(AdaptiveCurveAxis)int.Parse(id[5..]);_canvas.SelectedNode=-1;_canvas.Invalidate();return;}
        if(id.StartsWith("fan-tab-")){_canvas.FanTab=int.Parse(id[8..]);_canvas.Invalidate();return;}
        if(id.StartsWith("perf-tab-")){_canvas.PerformanceTab=int.Parse(id[9..]);_canvas.Invalidate();return;}
        if(id.StartsWith("monitor-tab-")){_canvas.MonitorTab=int.Parse(id[12..]);_canvas.Invalidate();return;}
        if(id.StartsWith("node-" )&&int.TryParse(id[5..],out var node)){_canvas.SelectedNode=node;_canvas.Invalidate();return;}
        if(id.EndsWith("-minus")||id.EndsWith("-plus"))
        {
            var plus=id.EndsWith("-plus");var key=id[..(id.Length-(plus?5:6))];var p=_draft.Get(_canvas.Editing);
            var current=key switch{"manual"=>_canvas.ManualLevel,"pl1"=>p.CpuPl1Watts,"pl2"=>p.CpuPl2Watts,"gpu"=>p.GpuMaximumMHz,_=>0};
            EditValue(key,current+(plus?1:-1));return;
        }
        if(_canvas.Busy&&id is not("firmware" or "fan-mode-0" or "window-minimize" or "window-maximize" or "window-close"))return;
        if(id=="fan-mode-1"&&(!_canvas.State.ManualAuthorized||_canvas.State.LifecycleBlocked))return;
        if(id=="manual-apply"&&(!_canvas.State.ManualAuthorized||_canvas.State.FanMode!="Manual"||_canvas.State.Runtime!="Healthy"||_canvas.State.LifecycleBlocked))return;
        if(id=="performance-apply"&&(!_canvas.State.PerformanceSupported||!_canvas.State.CanApplyPerformance||!(_draft.CpuEnabled||_draft.GpuEnabled)))return;
        if(id=="performance-release"&&!_canvas.State.PerformanceProcessPresent)return;
        switch(id)
        {
            case "window-minimize":Hide();break;
            case "window-maximize":ToggleMaximize();break;
            case "window-close":Hide();break;
            case "edit-curve":_canvas.Page=ProductPage.Curves;break;
            case "edit-performance":_canvas.Page=ProductPage.Performance;break;
            case "cpu-toggle":Change(_draft with{CpuEnabled=!_draft.CpuEnabled});break;
            case "gpu-toggle":Change(_draft with{GpuEnabled=!_draft.GpuEnabled});break;
            case "minimized-toggle":Change(_draft with{StartMinimized=!_draft.StartMinimized});break;
            case "save":_ = SaveAsync();break;
            case "startup-toggle":_ = ToggleStartupAsync();break;
            case "firmware":case "fan-mode-0":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,Draft)??Task.CompletedTask);break;
            case "fan-mode-1":_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Manual,Draft)??Task.CompletedTask);break;
            case "fan-mode-2":if(!_canvas.State.AutomaticAuthorized){_canvas.Notice="Automatic normal sigue cerrado hasta su calificación.";break;}_ = RunAsync(()=>_runtime?.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,Draft)??Task.CompletedTask);break;
            case "manual-apply":_ = RunAsync(()=>_runtime?.ApplyManualAsync(_canvas.ManualLevel)??Task.CompletedTask);break;
            case "performance-apply":_ = RunAsync(()=>_runtime?.ApplyPerformanceAsync(Draft)??Task.CompletedTask);break;
            case "performance-release":_ = RunAsync(()=>_runtime?.ReleasePerformanceAsync()??Task.CompletedTask);break;
            case "node-add":AddNode();break;
            case "node-remove":RemoveNode();break;
            case "curve-reset":ResetCurve(false);break;
            case "curve-defaults":ResetCurve(true);break;
            case "open-logs":try {Process.Start(new ProcessStartInfo(Path.GetDirectoryName(AppLog.CurrentLogPath)!){UseShellExecute=true});}catch(Exception ex){_canvas.Notice=ex.Message;}break;
        }
        _canvas.Invalidate();
    }
    private void Change(ProductProfiles next)
    {
        if(_canvas.Busy)return;next.Validate();_draft=next;_canvas.Profiles=_draft;_canvas.Dirty=true;_canvas.Notice="Cambios en edición; no aplicados al hardware.";_canvas.Invalidate();
    }
    internal void EditValue(string key,int value)
    {
        if(_canvas.Busy)return;
        var slot=_canvas.Editing;var p=_draft.Get(slot);
        if(key=="manual") {_canvas.ManualLevel=Math.Clamp(value,30,50);_canvas.Invalidate();return;}
        var next=key switch
        {
            "pl1"=>p with{CpuPl1Watts=Math.Clamp(value,CpuPowerProductDefaults.MinimumPl1Watts,CpuPowerProductDefaults.MaximumConfigurablePl1Watts),CpuPl2Watts=Math.Max(p.CpuPl2Watts,Math.Clamp(value,8,44))},
            "pl2"=>p with{CpuPl2Watts=Math.Clamp(value,Math.Max(p.CpuPl1Watts,CpuPowerProductDefaults.MinimumPl2Watts),CpuPowerProductDefaults.MaximumConfigurablePl2Watts)},
            "gpu"=>p with{GpuMaximumMHz=Math.Clamp(value,GpuProductPreferences.MinimumMHz,GpuProductPreferences.Maximum(slot))},
            _=>p
        };
        Change(_draft.With(slot,next));
    }
    internal void EditNode(int index,double input,int level)
    {
        if(_canvas.Busy)return;
        var slot=_canvas.Editing;var p=_draft.Get(slot);var points=AdaptiveCurveProfiles.Curve(p.Fan.BuildPolicy(),_canvas.Axis).ToArray();
        if(index<0||index>=points.Length)return;
        var lo=index==0?0:points[index-1].Input+1;var hi=index==points.Length-1?AdaptiveCurveProfiles.MaximumInput(_canvas.Axis):points[index+1].Input-1;
        var lowLevel=index==0?30:points[index-1].Level;var highLevel=index==points.Length-1?50:points[index+1].Level;
        points[index]=new(Math.Clamp(Math.Round(input),lo,hi),Math.Clamp(level,(int)lowLevel,(int)highLevel));
        var profile=AdaptiveCurveProfiles.WithCurve(p.Fan.Profile,_canvas.Axis,points);
        Change(_draft.With(slot,p with{Fan=p.Fan with{Profile=profile}}));
    }
    private void AddNode()
    {
        var p=_draft.Get(_canvas.Editing);var points=AdaptiveCurveProfiles.Curve(p.Fan.BuildPolicy(),_canvas.Axis).ToList();if(points.Count>=64)return;
        var gap=Enumerable.Range(0,points.Count-1).OrderByDescending(i=>points[i+1].Input-points[i].Input).First();
        if(points[gap+1].Input-points[gap].Input<2){_canvas.Notice="No queda espacio entre puntos.";return;}
        var x=Math.Floor((points[gap].Input+points[gap+1].Input)/2);var y=Math.Round(AdaptiveCurveProfiles.Interpolate(points,x));points.Insert(gap+1,new(x,y));
        Change(_draft.With(_canvas.Editing,p with{Fan=p.Fan with{Profile=AdaptiveCurveProfiles.WithCurve(p.Fan.Profile,_canvas.Axis,points)}}));_canvas.SelectedNode=gap+1;
    }
    private void RemoveNode()
    {
        var p=_draft.Get(_canvas.Editing);var points=AdaptiveCurveProfiles.Curve(p.Fan.BuildPolicy(),_canvas.Axis).ToList();if(points.Count<=2||_canvas.SelectedNode<0||_canvas.SelectedNode>=points.Count)return;
        points.RemoveAt(_canvas.SelectedNode);_canvas.SelectedNode=-1;Change(_draft.With(_canvas.Editing,p with{Fan=p.Fan with{Profile=AdaptiveCurveProfiles.WithCurve(p.Fan.Profile,_canvas.Axis,points)}}));
    }
    private void ResetCurve(bool all)
    {
        var p=_draft.Get(_canvas.Editing);var defaults=ProductProfiles.DefaultProfile(_canvas.Editing).Fan;
        var fan=all?defaults:p.Fan with{Profile=AdaptiveCurveProfiles.WithCurve(p.Fan.Profile,_canvas.Axis,AdaptiveCurveProfiles.Curve(defaults.BuildPolicy(),_canvas.Axis))};
        Change(_draft.With(_canvas.Editing,p with{Fan=fan}));_canvas.SelectedNode=-1;
    }
    private async Task SaveAsync() => await RunAsync(async()=>{var settings=Draft;await Task.Run(()=>ProductProfilesStore.Save(settings));if(_canvas.StartupEnabled)await WindowsStartupRegistration.SetEnabledAsync(true,_modules,settings.StartMinimized);_canvas.Dirty=false;_canvas.Notice="Perfiles guardados. No se ha aplicado hardware.";});
    private async Task ToggleStartupAsync() => await RunAsync(async()=>{var requested=!_canvas.StartupEnabled;await WindowsStartupRegistration.SetEnabledAsync(requested,_modules,_draft.StartMinimized);_canvas.StartupEnabled=await WindowsStartupRegistration.IsEnabledAsync();_canvas.Notice="Registro de inicio actualizado; el inicio permanece en Firmware.";});
    private async Task RunAsync(Func<Task> command)
    {
        if(_closing)return;
        _pendingCommands++;_canvas.Busy=true;_canvas.Notice="";_canvas.Invalidate();
        try{await command();}catch(Exception ex){_canvas.Notice=ex.Message;AppLog.Write("Product UI command failed: "+ex);}
        finally{_pendingCommands--;_canvas.Busy=_pendingCommands>0;if(!IsDisposed)_canvas.Invalidate();}
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){UnregisterPowerNotifications();if(_runtime is not null)_runtime.Changed-=UpdateState;_tray.Visible=false;_tray.ContextMenuStrip?.Dispose();_tray.Dispose();}
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
        try {await _startup;await _lifecycleRelease;if(_runtime is not null)await Task.Run(async()=>await _runtime.DisposeAsync());}
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
        _suspendRegistration=RegisterSuspendResumeNotification(Handle,0);
        var id=SessionDisplay;_displayRegistration=RegisterPowerSettingNotification(Handle,ref id,0);
        if(_suspendRegistration==IntPtr.Zero||_displayRegistration==IntPtr.Zero){UnregisterPowerNotifications();throw new InvalidOperationException("No se pudieron registrar las notificaciones lifecycle.");}
    }
    private void UnregisterPowerNotifications(){if(_displayRegistration!=IntPtr.Zero){UnregisterPowerSettingNotification(_displayRegistration);_displayRegistration=IntPtr.Zero;}if(_suspendRegistration!=IntPtr.Zero){UnregisterSuspendResumeNotification(_suspendRegistration);_suspendRegistration=IntPtr.Zero;}}
    [DllImport("user32.dll")]private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]private static extern IntPtr SendMessage(IntPtr h,int msg,int w,int l);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr RegisterPowerSettingNotification(IntPtr h,ref Guid id,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnregisterPowerSettingNotification(IntPtr h);
    [DllImport("powrprof.dll",SetLastError=true)]private static extern IntPtr RegisterSuspendResumeNotification(IntPtr h,uint flags);
    [DllImport("powrprof.dll",SetLastError=true)]private static extern bool UnregisterSuspendResumeNotification(IntPtr h);
}
