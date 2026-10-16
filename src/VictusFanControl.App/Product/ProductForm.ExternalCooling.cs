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
    private async Task InitializeExternalCoolingAsync()
    {
        if((_isolatedRuntime&&_externalCoolingPath is null)||_externalCooling is not null)return;
        _externalCoolingStore=new(_externalCoolingPath);
        _externalCoolingSettings=_externalCoolingStore.Load(out _externalCoolingLoadNotice);
        _externalPairingReader=_externalCoolingStore.ReadKey;
        _externalCooling=new(new TelemetryExportService(()=>Volatile.Read(ref _runtime)?.State??new(){Runtime="Unavailable",FanAuthority="Unknown"},new WindowsPhysicalMemoryUsage()),()=>_externalPairingReader());
        await ExternalCoolingOperationAsync(()=>_externalCooling.ConfigureAsync(_externalCoolingSettings));
        if(!_closing&&!IsDisposed)LayoutExternalCoolingPage();
    }
    private async Task ExternalCoolingOperationAsync(Func<Task> operation)
    {
        try{await operation();_externalCoolingError=null;}
        catch(Exception){_externalCoolingError="Base externa: revisa configuración y emparejamiento.";}
    }
    private void ExternalCoolingPowerEvent(int code)
    {
        if(code==4)_externalCooling?.Suspend();
        else if(code is 6 or 7 or 18 && _externalCooling is not null)
            _=ExternalCoolingOperationAsync(()=>_externalCooling.ResumeAsync());
    }
    private async Task DisposeExternalCoolingAsync()
    {
        await _externalCoolingStartup;
        if(_externalCooling is not null)await ExternalCoolingOperationAsync(async()=>await _externalCooling.DisposeAsync());
    }
    private void ShowExternalCooling()
    {
        _canvas.Page=ProductPage.ExternalCooling;LayoutExternalCoolingPage();_canvas.Invalidate();
    }
    private void LayoutExternalCoolingPage()
    {
        if(IsDisposed||_closing||_canvas.IsDisposed)return;
        var selected=_canvas.Page==ProductPage.ExternalCooling;
        if(selected&&_externalCoolingPage is null&&_externalCooling is not null&&_externalCoolingStore is not null)
        {
            _externalCoolingPage=new(_canvas,_externalCoolingSettings,_externalCooling,()=>_externalCoolingStore.Paired,async(settings,key,remove,connect)=>
            {
                await _externalCooling.DisconnectAsync();
                await Task.Run(()=>_externalCoolingStore.Save(settings,key,remove));
                _externalCoolingLoadNotice=null;
                _externalCoolingSettings=settings;
                await _externalCooling.ConfigureAsync(settings,connect);
            },ExternalCoolingError);
            _canvas.CoolingPage=_externalCoolingPage;
        }
        _externalCoolingPage?.Layout(selected);
    }
}
