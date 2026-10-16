using VictusFanControl.ExternalCooling;
namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    internal ExternalCurvePage? ExternalCurve {get;set;}
    private static readonly RectangleF ExternalPlot=new(955,330,637,325);
    private int _externalDrag=-1;
    private PointF ExternalPosition(ExternalCurvePoint p)=>new(ExternalPlot.Left+p.Input*ExternalPlot.Width/100,ExternalPlot.Bottom-(p.Output-30)*ExternalPlot.Height/225);
    private void ExternalCurveView(Graphics g)
    {
        DrawText(g,"Curva Externa",319,90,31,null,740,true);
        var p=ExternalCurve;if(p is null){DrawText(g,"Preparando configuración…",331,190,24,Muted,900);return;}
        void Action(string id,string text,RectangleF r,bool selected=false,bool enabled=true)=>Button(g,"external-curve-"+id,text,r,selected,enabled&&!p.Busy);
        Action("points","Editor gráfico",new(1120,87,247,45),!p.Tuning);Action("tuning","Respuesta y arranque",new(1383,87,264,45),p.Tuning);
        Card(g,new(308,153,1339,83));DrawText(g,p.AppliedState,334,163,23,Muted,1287);
        DrawText(g,"18 kHz · señal invertida · la curva y sus ajustes se aplican en el ESP32.",334,203,17,Muted,1287);
        if(p.Tuning)
        {
            Card(g,new(308,252,654,507));Card(g,new(980,252,667,507));
            var labels=new[]{("rise","Subida (%/s)",1,100),("fall","Bajada (%/s)",1,100),("filter","Filtro EMA (ms)",0,10000),("hysteresis","Histéresis (niveles externos)",0,30),("hold","Confirmar descenso (ms)",0,30000),
                ("start","Duración de arranque (ms)",0,3000),("start-output","Salida de arranque",30,255),("manual","Salida externa en Manual",30,255)};
            for(int i=0;i<labels.Length;i++) {var l=labels[i];var x=i<5?334:1004;var y=271+(i<5?i:i-5)*91;Slider(g,"external-curve-value-"+l.Item1,l.Item2,new(x,y,i<5?604:617,83),p.Value(l.Item1),l.Item3,l.Item4,"",true);}
            DrawText(g,"Arranque al encender · NTC inválida o ≥45 °C: máximo.",1004,570,18,Yellow,610,height:52);
            DrawText(g,"La salida aplicada se suaviza en el ESP32. Sin RPM, el duty no confirma que el ventilador esté girando.",1004,640,19,Muted,610,height:80);
        }
        else
        {
            Card(g,new(308,252,539,507));Card(g,new(867,252,780,507));
            Action("demand","Demanda",new(331,270,235,40),p.Draft.Input==0);Action("request","Solicitud aceptada",new(581,270,242,40),p.Draft.Input==1);
            var points=p.Draft.Points;var start=Math.Clamp(p.Selected-2,0,Math.Max(0,points.Length-5));
            DrawText(g,"#       Entrada (%)       Solicitud externa",331,329,20,Muted,490);
            for(int i=start;i<Math.Min(points.Length,start+5);i++)
                Action("node-"+i,$"{i+1}             {points[i].Input}                       {points[i].Output}",new(331,366+(i-start)*45,492,40),p.Selected==i);
            Action("previous","‹",new(331,604,52,38),enabled:p.Selected>0);Action("next","›",new(395,604,52,38),enabled:p.Selected<points.Length-1);
            Action("x","Editar entrada",new(463,604,175,38));Action("y","Editar salida",new(650,604,173,38));
            Action("add","+ Añadir punto",new(331,657,236,38),enabled:points.Length<16);Action("remove","− Quitar",new(582,657,241,38),enabled:p.Selected>0&&p.Selected<points.Length-1);
            if(p.Draft.Input==1){Action("minimum","Mínimo: "+p.Draft.RequestMinimum,new(331,706,236,34));Action("maximum","Máximo: "+p.Draft.RequestMaximum,new(582,706,241,34));}
            else DrawText(g,"Manual usa la salida configurada en Respuesta.",331,714,17,Muted,490);
            DrawText(g,"Arrastra los puntos · flechas para ajustar",889,271,24,null,735,true);
            using var grid=new Pen(Border);
            for(int i=0;i<=5;i++){var x=ExternalPlot.Left+i*ExternalPlot.Width/5;g.DrawLine(grid,x,ExternalPlot.Top,x,ExternalPlot.Bottom);DrawText(g,(i*20).ToString(),x-12,ExternalPlot.Bottom+10,17,Muted,50);}
            for(int i=0;i<=5;i++){var y=ExternalPlot.Bottom-i*ExternalPlot.Height/5;g.DrawLine(grid,ExternalPlot.Left,y,ExternalPlot.Right,y);DrawText(g,(30+i*45).ToString(),891,y-10,17,Muted,59);}
            using var line=new Pen(Accent,3);g.DrawLines(line,points.Select(ExternalPosition).ToArray());
            for(int i=0;i<points.Length;i++){var pos=ExternalPosition(points[i]);using var b=new SolidBrush(i==p.Selected?Yellow:Ink);g.FillEllipse(b,pos.X-7,pos.Y-7,14,14);Hit("external-curve-node-"+i,new(pos.X-14,pos.Y-14,28,28),"Punto externo "+(i+1),!p.Busy);}
            DrawText(g,(p.Draft.Input==0?"Demanda (%)":"Solicitud normalizada (%)")+" → Solicitud externa (30–255)",955,702,18,Muted,637);
        }
        DrawText(g,p.Notice,331,770,18,p.Dirty?Yellow:Muted,1293,height:34);
        Action("discard","Descartar cambios",new(331,816,293,42),enabled:p.Dirty);
        Action("save","Guardar y enviar curva",new(640,816,565,42),true,p.Dirty);
        Action("reset","Restablecer",new(1221,816,403,42));
    }
    private bool ExternalPointerDown(PointF p)
    {
        if(Page!=ProductPage.ExternalCurve||ExternalCurve is not{Tuning:false,Busy:false} curve)return false;
        for(int i=0;i<curve.Draft.Points.Length;i++) {var n=ExternalPosition(curve.Draft.Points[i]);if(Math.Abs(p.X-n.X)<17&&Math.Abs(p.Y-n.Y)<17){_externalDrag=i;_keyboardId="external-curve-node-"+i;curve.EditNode(i,curve.Draft.Points[i].Input,curve.Draft.Points[i].Output);Capture=true;return true;}}
        return false;
    }
    private bool ExternalPointerMove(PointF p)
    {
        if(_externalDrag<0)return false;
        if(Page!=ProductPage.ExternalCurve||ExternalCurve is not{Busy:false} curve||Busy){_externalDrag=-1;Capture=false;return true;}
        curve.EditNode(_externalDrag,(p.X-ExternalPlot.Left)/ExternalPlot.Width*100,(int)Math.Round(30+(ExternalPlot.Bottom-p.Y)/ExternalPlot.Height*225));return true;
    }
}
