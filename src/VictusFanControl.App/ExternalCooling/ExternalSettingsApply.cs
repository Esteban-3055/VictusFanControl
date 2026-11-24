namespace VictusFanControl.App;

internal sealed class ExternalConnectionAfterSaveException(Exception inner)
    : IOException("Preferencias guardadas. No se pudo iniciar la conexión: "+inner.Message,inner);

internal static class ExternalSettingsApply
{
    internal static async Task PersistAndConfigureAsync(Func<Task> persist,Func<Task> configure)
    {
        await persist();
        try{await configure();}
        catch(Exception error){throw new ExternalConnectionAfterSaveException(error);}
    }
}
