using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private ExternalCoolingManager? _externalCooling;
    private ExternalCoolingSettings _externalCoolingSettings=new();
    private Func<byte[]?> _externalPairingReader=()=>null;
    private string? _externalCoolingError;
    internal string? ExternalCoolingError=>_externalCoolingError;
    private async Task InitializeExternalCoolingAsync()
    {
        if(_isolatedRuntime||_externalCooling is not null)return;
        _externalCooling=new(new TelemetryExportService(()=>Volatile.Read(ref _runtime)?.State??new(),new WindowsPhysicalMemoryUsage()),()=>_externalPairingReader());
        await ExternalCoolingOperationAsync(()=>_externalCooling.ConfigureAsync(_externalCoolingSettings));
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
        if(_externalCooling is not null)await ExternalCoolingOperationAsync(async()=>await _externalCooling.DisposeAsync());
    }
}
