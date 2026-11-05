using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private ExternalCoolingManager? _externalCooling;
    private ExternalCoolingSettings _externalCoolingSettings=new();
    private Func<byte[]?> _externalPairingReader=()=>null;
    private string? _externalCoolingError;
    private string? _externalCoolingLoadNotice;
    internal string? ExternalCoolingError=>_externalCoolingError??_externalCoolingLoadNotice;
    internal ExternalCoolingDiagnostics? ExternalCoolingStatus=>_externalCooling?.Diagnostics;
    private ExternalCoolingSettingsStore? _externalCoolingStore;
    private ExternalCoolingPage? _externalCoolingPage;
    internal ExternalCoolingPage? ExternalPage=>_externalCoolingPage;
    private readonly string? _externalCoolingPath;
    private Task _externalCoolingStartup=Task.CompletedTask;
    private readonly SemaphoreSlim _externalSettingsGate=new(1,1);
    private (ExternalCoolingConnectionState State,long Failures)? _externalLogged;
    internal static string ExternalCoolingLogLine(ExternalCoolingDiagnostics d)=>
        $"PRODUCT EXTERNAL LINK: state={d.State};failure={d.LastFailureKind};failures={d.Failures};connections={d.Connections};sent={d.Sent};received={d.Received}";
    private void ObserveExternalCoolingDiagnostics()
    {
        if(_externalCooling is null)return;
        var d=_externalCooling.Diagnostics;var identity=(d.State,d.Failures);
        if(_externalLogged==identity)return;
        _externalLogged=identity;AppLog.Write(ExternalCoolingLogLine(d));
    }
    private async Task InitializeExternalCoolingAsync()
    {
        if((_isolatedRuntime&&_externalCoolingPath is null)||_externalCooling is not null)return;
        var settingsPath=_externalCoolingPath;
        var loaded=await Task.Factory.StartNew(()=>
        {
            var store=new ExternalCoolingSettingsStore(settingsPath);
            var settings=store.Load(out var notice);return(Store:store,Settings:settings,Notice:notice);
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
        if(_closing||IsDisposed)return;
        _externalCoolingStore=loaded.Store;_externalCoolingSettings=loaded.Settings;_externalCoolingLoadNotice=loaded.Notice;
        AppLog.Write($"PRODUCT EXTERNAL STARTUP: mode={_externalCoolingSettings.Mode};autoReconnect={_externalCoolingSettings.AutoReconnect};paired={_externalCoolingStore.Paired};invalidSettings={_externalCoolingLoadNotice is not null}");
        _externalPairingReader=_externalCoolingStore.ReadKey;
        _externalCooling=new(new TelemetryExportService(()=>Volatile.Read(ref _runtime)?.State??new(){Runtime="Unavailable",FanAuthority="Unknown"},new WindowsPhysicalMemoryUsage(),()=>_hyperX?.Snapshot??new(),()=>Volatile.Read(ref _hyperXSettings).DisplayConfiguration()),()=>_externalPairingReader());
        await ExternalCoolingOperationAsync(()=>_externalCooling.ConfigureAsync(_externalCoolingSettings));
        ObserveExternalCoolingDiagnostics();
        if(!_closing&&!IsDisposed)LayoutExternalCoolingPage();
    }
    private async Task ExternalCoolingOperationAsync(Func<Task> operation)
    {
        try{await operation();_externalCoolingError=null;}
        catch(Exception ex){_externalCoolingError="Base externa: revisa configuración y emparejamiento.";AppLog.Write("PRODUCT EXTERNAL OPERATION FAILED: "+ex.GetType().Name);}
    }
    private void ExternalCoolingPowerEvent(int code)
    {
        if(code==4)_externalCooling?.Suspend();
        else if(code is 6 or 7 or 18 && _externalCooling is not null)
            _=ExternalCoolingOperationAsync(()=>_externalCooling.ResumeAsync());
    }
    private async Task DisposeExternalCoolingAsync()
    {
        // A pending settings-only read has no socket, callbacks or authority.
        // Its continuation checks the closed generation before creating a manager.
        var manager=_externalCooling;
        if(manager is null)
        {
            if(_externalCoolingStartup.IsFaulted)AppLog.Write("Optional external settings startup failed before creating a manager: "+_externalCoolingStartup.Exception?.GetBaseException().GetType().Name);
            return;
        }
        try{await _externalCoolingStartup;}
        catch(Exception error){AppLog.Write("External startup failed; manager cleanup still required: "+error.GetType().Name);}
        await manager.DisposeAsync();
    }
    private void ShowExternalCooling()
    {
        _canvas.Page=ProductPage.ExternalCooling;LayoutExternalCoolingPage();_canvas.Invalidate();
    }
    private async Task SaveExternalCurveAsync(ExternalCurveConfiguration curve)
    {
        if(_externalCoolingStore is null||_externalCooling is null)throw new InvalidOperationException();
        await _externalSettingsGate.WaitAsync();
        try
        {
            var next=_externalCoolingSettings with{Curve=curve};
            var connect=_externalCooling.Diagnostics.State is ExternalCoolingConnectionState.Connected or ExternalCoolingConnectionState.Connecting or ExternalCoolingConnectionState.Authenticating or ExternalCoolingConnectionState.Reconnecting;
            await Task.Run(()=>_externalCoolingStore.Save(next));_externalCoolingSettings=next;
            _externalCoolingPage?.UpdateSavedCurve(curve);
            if(!_closing)await _externalCooling.ConfigureAsync(next,connect);
        }
        finally{_externalSettingsGate.Release();}
    }
    private void LayoutExternalCoolingPage()
    {
        if(IsDisposed||_closing||_canvas.IsDisposed)return;
        var selected=_canvas.Page==ProductPage.ExternalCooling;
        if(_canvas.Page==ProductPage.ExternalCurve&&_canvas.ExternalCurve is null&&_externalCoolingStore is not null)
            _canvas.ExternalCurve=new(_canvas,()=>_externalCoolingSettings,SaveExternalCurveAsync,()=>ExternalCoolingStatus);
        if(selected&&_externalCoolingPage is null&&_externalCooling is not null&&_externalCoolingStore is not null)
        {
            _externalCoolingPage=new(_canvas,_externalCoolingSettings,_externalCooling,()=>_externalCoolingStore.Paired,async(settings,key,remove,connect)=>
            {
                await _externalSettingsGate.WaitAsync();
                try
                {
                    settings=settings with{Curve=_externalCoolingSettings.Curve};
                    await _externalCooling.DisconnectAsync();
                    await Task.Run(()=>_externalCoolingStore.Save(settings,key,remove));
                    _externalCoolingLoadNotice=null;_externalCoolingSettings=settings;
                    if(!_closing)await _externalCooling.ConfigureAsync(settings,connect);
                }
                finally{_externalSettingsGate.Release();}
            },ExternalCoolingError);
            _externalCoolingPage.CurrentCurve=()=>_externalCoolingSettings.Curve;
            _canvas.CoolingPage=_externalCoolingPage;
        }
        _externalCoolingPage?.Layout(selected);
    }
}
