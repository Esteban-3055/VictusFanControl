namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private readonly Func<Task<bool>>? _startupRegistrationReader;
    private Task? _startupRegistrationCheck;
    internal Task CheckStartupRegistrationAsync()
    {
        if(_closing||IsDisposed||_startupRegistrationReader is null)return Task.CompletedTask;
        if(_startupRegistrationCheck is {IsCompleted:false})return _startupRegistrationCheck;
        return _startupRegistrationCheck=CheckStartupRegistrationCoreAsync();
    }
    private async Task CheckStartupRegistrationCoreAsync()
    {
        await Task.Yield();
        try
        {
            var enabled=await _startupRegistrationReader!();
            if(_closing||IsDisposed)return;
            _canvas.StartupEnabled=enabled;_canvas.StartupKnown=true;
        }
        catch(Exception ex)
        {
            AppLog.Write("Product optional startup registration query failed; runtime unchanged: "+ex);
            if(_closing||IsDisposed)return;
            _canvas.StartupKnown=false;
            _canvas.Notice="No se pudo consultar el inicio con Windows. Puedes reintentar la consulta en Configuración. "+ex.Message;
        }
        finally{if(!_closing&&!IsDisposed)_canvas.Invalidate();}
    }
}
