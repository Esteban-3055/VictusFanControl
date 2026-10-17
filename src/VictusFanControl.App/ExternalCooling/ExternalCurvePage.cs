using System.Globalization;
using VictusFanControl.ExternalCooling;

namespace VictusFanControl.App;

/// <summary>External-only draft. Editing never invokes the notebook runtime.</summary>
internal sealed class ExternalCurvePage(ProductCanvas canvas,Func<ExternalCoolingSettings> settings,
    Func<ExternalCurveConfiguration,Task> save,Func<ExternalCoolingDiagnostics?> diagnostics)
{
    private ExternalCurveConfiguration _saved=settings().Curve;
    internal ExternalCurveConfiguration Draft {get;private set;}=settings().Curve;
    internal int Selected {get;private set;}=0;
    internal bool Tuning {get;private set;}
    internal bool Busy {get;private set;}
    internal bool Dirty=>Draft!=_saved;
    internal int Preview {get;private set;}=50;
    internal string Notice {get;private set;}="Configuración independiente del notebook · 18 kHz · salida lógica 30–255.";
    internal string AppliedState
    {
        get
        {
            var d=diagnostics();
            if(d?.State!=ExternalCoolingConnectionState.Connected||d.LastValidReceivedUtc is not{} at||DateTimeOffset.UtcNow-at>TimeSpan.FromSeconds(3))return "Sin confirmación actual del ESP32";
            if(d.Device?.ExternalCurveVersion!=1)return "El dispositivo requiere firmware compatible con Curva Externa";
            return d.Device.AppliedConfigRevision==_saved.Revision?"Configuración confirmada por el ESP32":"Guardada · esperando confirmación del ESP32";
        }
    }
    internal void EditNode(int index,double input,int output)
    {
        if(Busy||index<0||index>=Draft.Points.Length||!double.IsFinite(input))return;
        var points=Draft.Points.ToArray();
        var min=index==0?0:points[index-1].Input+1;var max=index==points.Length-1?100:points[index+1].Input-1;
        if(min>max)return;
        points[index]=new(index==0?0:index==points.Length-1?100:Math.Clamp((int)Math.Round(input),min,max),
            Math.Clamp(output,index==0?30:points[index-1].Output,index==points.Length-1?255:points[index+1].Output));
        Draft=Draft with{Points=points};Selected=index;canvas.Invalidate();
    }
    internal int Value(string id)=>id switch
    {
        "rise"=>Draft.Rise,"fall"=>Draft.Fall,"filter"=>Draft.FilterMs,"hysteresis"=>Draft.Hysteresis,"hold"=>Draft.DownHoldMs,
        "start"=>Draft.StartMs,"start-output"=>Draft.StartOutput,"manual"=>Draft.ManualOutput,"preview"=>Preview,_=>0
    };
    internal void EditValue(string id,int value)
    {
        if(Busy)return;
        var next=id switch
        {
            "rise"=>Draft with{Rise=value},"fall"=>Draft with{Fall=value},"filter"=>Draft with{FilterMs=value},
            "hysteresis"=>Draft with{Hysteresis=value},"hold"=>Draft with{DownHoldMs=value},"start"=>Draft with{StartMs=value},
            "start-output"=>Draft with{StartOutput=value},"manual"=>Draft with{ManualOutput=value},_=>Draft
        };
        try{next.Validate();Draft=next;}catch(ArgumentException){return;}
        if(id=="preview")Preview=Math.Clamp(value,0,100);
        canvas.Invalidate();
    }
    internal static (int Min,int Max) Range(string id)=>id switch
    {
        "rise" or "fall"=>(1,100),"filter"=>(0,10000),"hysteresis"=>(0,30),"hold"=>(0,30000),
        "start"=>(0,3000),"start-output" or "manual"=>(30,255),_=>throw new ArgumentException("Ajuste desconocido.")
    };
    internal string? EditNumeric(string id,string text)
    {
        if(Busy)return "Espera a que termine el guardado.";
        var range=Range(id);
        if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var value)||value<range.Min||value>range.Max)
            return $"Introduce un entero entre {range.Min} y {range.Max}.";
        EditValue(id,value);return null;
    }
    internal async Task ExecuteAsync(string id)
    {
        if(Busy)return;
        if(id.StartsWith("value-",StringComparison.Ordinal))
        {
            if(id.EndsWith("-plus",StringComparison.Ordinal)||id.EndsWith("-minus",StringComparison.Ordinal))
            {
                var plus=id.EndsWith("-plus",StringComparison.Ordinal);var key=id[6..^(plus?5:6)];
                var range=Range(key);EditValue(key,Math.Clamp(Value(key)+(plus?1:-1),range.Min,range.Max));return;
            }
            if(id.EndsWith("-text",StringComparison.Ordinal))
            {
                var key=id[6..^5];var range=Range(key);
                using var dialog=new ProductNumericDialog("Ajuste de curva externa",Value(key),range.Min,range.Max,text=>EditNumeric(key,text));
                dialog.ShowDialog(canvas.FindForm());canvas.Invalidate();return;
            }
        }
        if(id.StartsWith("node-",StringComparison.Ordinal)&&int.TryParse(id[5..],out var node))Selected=Math.Clamp(node,0,Draft.Points.Length-1);
        else switch(id)
        {
            case "points":Tuning=false;break;
            case "tuning":Tuning=true;break;
            case "demand":Draft=Draft with{Input=0};break;
            case "request":Draft=Draft with{Input=1};break;
            case "add":
                if(Draft.Points.Length>=16)break;
                var at=Math.Clamp(Selected,0,Draft.Points.Length-2);var a=Draft.Points[at];var b=Draft.Points[at+1];
                if(b.Input-a.Input<2){Notice="Selecciona un segmento con espacio para un punto.";break;}
                var points=Draft.Points.ToList();points.Insert(at+1,new((a.Input+b.Input)/2,(a.Output+b.Output)/2));
                Draft=Draft with{Points=points.ToArray()};Selected=at+1;break;
            case "remove":
                if(Selected>0&&Selected<Draft.Points.Length-1){Draft=Draft with{Points=Draft.Points.Where((_,i)=>i!=Selected).ToArray()};Selected--;}
                break;
            case "previous":Selected=Math.Max(0,Selected-1);break;
            case "next":Selected=Math.Min(Draft.Points.Length-1,Selected+1);break;
            case "reset":Draft=new();Selected=0;Notice="Valores iniciales en edición; guarda para aplicarlos.";break;
            case "discard":Draft=_saved;Selected=0;Notice="Se recuperó la curva externa guardada.";break;
            case "x":case "y":case "minimum":case "maximum":
                var value=id=="x"?Draft.Points[Selected].Input:id=="y"?Draft.Points[Selected].Output:id=="minimum"?Draft.RequestMinimum:Draft.RequestMaximum;
                using(var dialog=new ProductNumericDialog(id=="x"?"Entrada del punto (%)":id=="y"?"Solicitud externa (30–255)":"Calibración de Solicitud del notebook",value,
                    id=="y"?30:0,id=="y"?255:id=="x"?100:65535,text=>
                    {
                        if(!int.TryParse(text,NumberStyles.None,CultureInfo.InvariantCulture,out var n))return "Introduce un entero.";
                        if(id is "minimum" or "maximum")
                        {
                            var next=id=="minimum"?Draft with{RequestMinimum=n}:Draft with{RequestMaximum=n};
                            try{next.Validate();Draft=next;}catch(ArgumentException){return "El mínimo debe ser menor que el máximo (0–65535).";}
                        }
                        else
                        {
                            var p=Draft.Points[Selected];
                            var arr=Draft.Points.ToArray();arr[Selected]=id=="x"?p with{Input=n}:p with{Output=n};
                            var next=Draft with{Points=arr};try{next.Validate();Draft=next;}catch(ArgumentException){return "Conserva extremos, orden ascendente y salida 30–255.";}
                        }
                        return null;
                    }))dialog.ShowDialog(canvas.FindForm());
                break;
            case "save":
                Busy=true;canvas.Invalidate();
                try
                {
                    var next=Draft with{Revision=Guid.NewGuid().ToString("N")};next.Validate();await save(next);
                    Draft=_saved=next;Notice="Curva guardada. El ESP32 confirma su aplicación al recibirla.";
                }
                catch(Exception){Notice="No se pudo guardar la curva externa. Se conserva el borrador.";}
                finally{Busy=false;}
                break;
        }
        canvas.Invalidate();
    }
}
