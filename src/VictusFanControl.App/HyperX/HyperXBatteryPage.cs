using System.Globalization;

namespace VictusFanControl.App;

/// <summary>Presentation-only draft, rendered by the common ProductCanvas.
/// Editing never queries USB or changes the notebook's control preferences.</summary>
internal sealed class HyperXBatteryPage(ProductCanvas canvas,HyperXSettings settings,Func<HyperXSettings,Task> persist) : IDisposable
{
    internal const string ValuePrefix="hyperx-value-";
    private HyperXSettings saved=settings;
    private bool disposed;
    internal HyperXSettings Draft {get;private set;}=settings;
    internal HyperXSnapshot Reading {get;private set;}=new();
    internal DateTimeOffset PresentedAt {get;private set;}=DateTimeOffset.UtcNow;
    internal int Tab {get;private set;}
    internal bool Selected {get;set;}
    internal bool Busy {get;private set;}
    internal bool Dirty=>Draft!=saved;
    internal string Notice {get;private set;}="Los cambios se aplican al guardar. La batería es una estimación por voltaje.";
    private void Refresh(){if(!disposed&&!canvas.IsDisposed)canvas.Invalidate();}
    internal void SetNotice(string? text){if(text is not null){Notice=text;Refresh();}}
    internal void UpdateReading(HyperXSnapshot reading,DateTimeOffset now)
    {Reading=reading;PresentedAt=now;if(Selected)Refresh();}
    internal int Value(string key)=>key switch
    {
        "low"=>Draft.LowPercent,"critical"=>Draft.CriticalPercent,"full"=>Draft.FullPercent,
        "low-seconds"=>Draft.LcdLowSeconds,"critical-seconds"=>Draft.LcdCriticalSeconds,
        "low-cycles"=>Draft.LcdLowBlinkCycles,"critical-cycles"=>Draft.LcdCriticalBlinkCycles,
        _=>throw new ArgumentException("Ajuste HyperX desconocido.")
    };
    internal static (int Min,int Max) Range(string key)=>key switch
    {
        "low"=>(11,99),"critical"=>(10,98),"full"=>(12,100),
        "low-seconds" or "critical-seconds"=>(1,120),"low-cycles" or "critical-cycles"=>(1,20),
        _=>throw new ArgumentException("Ajuste HyperX desconocido.")
    };
    internal void EditValue(string key,int value)
    {
        if(disposed||Busy)return;
        var range=Range(key);value=Math.Clamp(value,range.Min,range.Max);
        Draft=key switch
        {
            "low"=>Draft with{LowPercent=value},"critical"=>Draft with{CriticalPercent=value},"full"=>Draft with{FullPercent=value},
            "low-seconds"=>Draft with{LcdLowSeconds=value},"critical-seconds"=>Draft with{LcdCriticalSeconds=value},
            "low-cycles"=>Draft with{LcdLowBlinkCycles=value},"critical-cycles"=>Draft with{LcdCriticalBlinkCycles=value},_=>Draft
        };
        Notice="Cambios pendientes. Guarda para aplicarlos en Windows y enviarlos al ESP32.";Refresh();
    }
    internal string? EditNumeric(string key,string text)
    {
        if(disposed||Busy)return "Espera a que termine el guardado.";
        var r=Range(key);
        if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out int value)||value<r.Min||value>r.Max)
            return $"Introduce un entero entre {r.Min} y {r.Max}.";
        EditValue(key,value);return null;
    }
    internal async Task ExecuteAsync(string id)
    {
        if(disposed||Busy)return;
        if(id.StartsWith("value-",StringComparison.Ordinal))
        {
            foreach(var suffix in new[]{"-plus","-minus","-text"})
            {
                if(!id.EndsWith(suffix,StringComparison.Ordinal))continue;
                var key=id[6..^suffix.Length];var r=Range(key);
                if(suffix=="-text")
                {
                    using var dialog=new ProductNumericDialog("HyperX Battery · "+Label(key),Value(key),r.Min,r.Max,text=>EditNumeric(key,text),
                        help:"Editar solo cambia el borrador. Los umbrales y los tiempos se validan al guardar.");
                    dialog.ShowDialog(canvas.FindForm());Refresh();
                }
                else EditValue(key,Value(key)+(suffix=="-plus"?1:-1));
                return;
            }
        }
        switch(id)
        {
            case "info":case "notifications":case "lcd":
                canvas.Capture=false;Tab=id=="info"?0:id=="notifications"?1:2;break;
            case "low-enabled":Draft=Draft with{LowEnabled=!Draft.LowEnabled};break;
            case "critical-enabled":Draft=Draft with{CriticalEnabled=!Draft.CriticalEnabled};break;
            case "full-enabled":Draft=Draft with{FullEnabled=!Draft.FullEnabled};break;
            case "mute-enabled":Draft=Draft with{MuteEnabled=!Draft.MuteEnabled};break;
            case "discard":Draft=saved;Notice="Ajustes HyperX guardados restaurados.";break;
            case "reset":Draft=new();Notice="Valores iniciales en edición. Guarda para aplicarlos.";break;
            case "save":await SaveAsync();return;
        }
        Refresh();
    }
    internal static string Label(string key)=>key switch
    {
        "low"=>"Batería baja","critical"=>"Batería crítica","full"=>"Nivel completo",
        "low-seconds" or "critical-seconds"=>"Duración total del aviso LCD",
        "low-cycles" or "critical-cycles"=>"Ciclos de parpadeo",_=>key
    };
    internal async Task SaveAsync()
    {
        if(disposed||Busy)return;
        try
        {
            var candidate=Draft;candidate.Validate();Busy=true;Refresh();
            await persist(candidate);saved=candidate;Notice="Ajustes HyperX guardados.";
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidDataException)
        {Notice=e is InvalidDataException?e.Message:"No se pudieron guardar los ajustes. Se conserva la configuración anterior.";}
        finally{Busy=false;Refresh();}
    }
    public void Dispose(){disposed=true;}
}
