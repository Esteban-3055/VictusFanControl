using VictusFanControl.ExternalCooling;
namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    internal ExternalCurvePage? ExternalCurve {get;set;}
    private static readonly RectangleF ExternalPlot=new(955,330,637,325);
    private int _externalDrag=-1;
    private PointF ExternalPosition(ExternalCurvePoint p)=>new(ExternalPlot.Left+p.Input*ExternalPlot.Width/100,ExternalPlot.Bottom-p.Output*ExternalPlot.Height/255);
    private void ExternalCurveView(Graphics g)
    {
        DrawText(g,"Curva Externa",319,90,31,null,740,true);
        var p=ExternalCurve;if(p is null){DrawText(g,"Preparando configuración…",331,190,24,Muted,900);return;}
        void Action(string id,string text,RectangleF r,bool selected=false,bool enabled=true)=>Button(g,"external-curve-"+id,text,r,selected,enabled&&!p.Busy);
        Action("points","Editor gráfico",new(1120,87,247,45),!p.Tuning);Action("tuning","Respuesta y arranque",new(1383,87,264,45),p.Tuning);
        Card(g,new(308,153,1339,83));DrawText(g,p.AppliedState,334,163,23,Muted,1287);
        DrawText(g,"18 Hz · señal invertida · la curva y sus ajustes se aplican en el ESP32.",334,203,17,Muted,1287);
        if(p.Tuning)
        {
            Card(g,new(308,252,654,507));Card(g,new(980,252,667,507));
            var labels=new[]{("rise","Subida (%/s)",1,100),("fall","Bajada (%/s)",1,100),("filter","Filtro EMA (ms)",0,10000),("hysteresis","Histéresis (niveles externos)",0,30),("hold","Confirmar descenso (ms)",0,30000),
                ("start","Duración de arranque (ms)",0,3000),("start-output","Salida de arranque",0,255),("manual","Salida externa en Manual",0,255)};
            for(int i=0;i<labels.Length;i++)
            {
                var l=labels[i];var x=i<5?334:1004;var y=271+(i<5?i:i-5)*91;var width=i<5?604:617;
                Slider(g,"external-curve-value-"+l.Item1,l.Item2,new(x,y,width,83),p.Value(l.Item1),l.Item3,l.Item4,"",true);
                Hit("external-curve-value-"+l.Item1+"-text",new(x+width-220,y-9,155,34),"Escribir "+l.Item2,!p.Busy);
            }
            DrawText(g,"Arranque al encender y tras parada · NTC inválida o ≥45 °C: máximo.",1004,570,18,Yellow,610,height:52);
            DrawText(g,"La salida aplicada se suaviza en el ESP32. Sin RPM, el duty no confirma que el ventilador esté girando.",1004,640,19,Muted,610,height:80);
        }
        else
        {
            Card(g,new(308,252,539,507));Card(g,new(867,252,780,507));
            Action("demand","Demanda",new(331,270,235,40),p.Draft.Input==0);Action("request","Solicitud aceptada",new(581,270,242,40),p.Draft.Input==1);
            var points=p.Draft.Points;var start=Math.Clamp(p.Selected-2,0,Math.Max(0,points.Length-5));
            DrawText(g,"#",345,329,20,Muted,43);DrawText(g,"Entrada (%)",404,329,20,Muted,192);DrawText(g,"Salida",669,329,20,Muted,130);
            for(int i=start;i<Math.Min(points.Length,start+5);i++)
            {
                var row=new RectangleF(331,366+(i-start)*45,492,40);Card(g,row,p.Selected==i,7);
                DrawText(g,(i+1).ToString(),345,row.Y+7,20,Muted,43);
                DrawText(g,points[i].Input.ToString(),445,row.Y+7,20,Ink,168);
                DrawText(g,points[i].Output.ToString(),669,row.Y+7,20,Ink,130);
                Hit("external-curve-node-"+i,row,"Punto externo "+(i+1),!p.Busy);
            }
            Action("previous","‹",new(331,604,52,38),enabled:p.Selected>0);Action("next","›",new(395,604,52,38),enabled:p.Selected<points.Length-1);
            Action("x","Editar entrada",new(463,604,175,38));Action("y","Editar salida",new(650,604,173,38));
            Action("add","+ Añadir punto",new(331,657,236,38),enabled:points.Length<16);Action("remove","− Quitar",new(582,657,241,38),enabled:p.Selected>0&&p.Selected<points.Length-1);
            if(p.Draft.Input==1){Action("minimum","Mínimo: "+p.Draft.RequestMinimum,new(331,706,236,34));Action("maximum","Máximo: "+p.Draft.RequestMaximum,new(582,706,241,34));}
            else DrawText(g,"Manual usa la salida configurada en Respuesta.",331,714,17,Muted,490);
            DrawText(g,"Arrastra los puntos · flechas para ajustar",889,271,24,null,735,true);
            using var grid=new Pen(Border);
            for(int i=0;i<=5;i++){var x=ExternalPlot.Left+i*ExternalPlot.Width/5;g.DrawLine(grid,x,ExternalPlot.Top,x,ExternalPlot.Bottom);DrawText(g,(i*20).ToString(),x-12,ExternalPlot.Bottom+10,17,Muted,50);}
            for(int i=0;i<=5;i++){var y=ExternalPlot.Bottom-i*ExternalPlot.Height/5;g.DrawLine(grid,ExternalPlot.Left,y,ExternalPlot.Right,y);DrawText(g,(i*51).ToString(),891,y-10,17,Muted,59);}
            using var line=new Pen(Accent,3);g.DrawLines(line,points.Select(ExternalPosition).ToArray());
            for(int i=0;i<points.Length;i++){var pos=ExternalPosition(points[i]);using var b=new SolidBrush(i==p.Selected?Yellow:Ink);g.FillEllipse(b,pos.X-7,pos.Y-7,14,14);Hit("external-curve-node-"+i,new(pos.X-14,pos.Y-14,28,28),"Punto externo "+(i+1),!p.Busy);}
            var markers=p.Markers(DateTimeOffset.UtcNow);
            foreach(var marker in markers.OrderBy(m=>m.IsApplied)) {
                var pos=new PointF(ExternalPlot.Left+(float)marker.Input*ExternalPlot.Width/100,
                    ExternalPlot.Bottom-(float)marker.Output*ExternalPlot.Height/255);
                using var halo=new Pen(Background,6);g.DrawEllipse(halo,pos.X-9,pos.Y-9,18,18);
                using var ring=new Pen(marker.IsApplied?Green:Yellow,3);g.DrawEllipse(ring,pos.X-9,pos.Y-9,18,18);
                if(marker.IsApplied){using var fill=new SolidBrush(Green);g.FillEllipse(fill,pos.X-5,pos.Y-5,10,10);}
            }
            var objective=markers.FirstOrDefault(m=>!m.IsApplied);var applied=markers.FirstOrDefault(m=>m.IsApplied);
            DrawText(g,objective is null?"Sin entrada reciente para esta curva":
                $"○ {(p.Dirty?"Borrador":"Objetivo")}: {objective.Input:0.0}% → {objective.Output:0}"+
                (applied is null?" · ● Aplicado: —":$" · ● Aplicado: {applied.Output:0}"),
                955,702,16,applied is null?Muted:Green,637,height:24);
            DrawText(g,(p.Draft.Input==0?"Demanda (%)":"Solicitud normalizada (%)")+" → Solicitud externa (0–255)",955,734,18,Muted,637);
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
        curve.EditNode(_externalDrag,(p.X-ExternalPlot.Left)/ExternalPlot.Width*100,(int)Math.Round((ExternalPlot.Bottom-p.Y)/ExternalPlot.Height*255));return true;
    }
}
