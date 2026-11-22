using System.Drawing.Drawing2D;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private static GraphicsPath Beveled(RectangleF r,float cut)
    {
        var p=new GraphicsPath();p.AddPolygon(new PointF[]{new(r.Left+cut,r.Top),new(r.Right-cut,r.Top),new(r.Right,r.Top+cut),
            new(r.Right,r.Bottom-cut),new(r.Right-cut,r.Bottom),new(r.Left+cut,r.Bottom),new(r.Left,r.Bottom-cut),new(r.Left,r.Top+cut)});return p;
    }
    private static void PanelCornerMarks(Graphics g,RectangleF r)
    {
        using var light=new SolidBrush(Color.FromArgb(235,239,242));
        g.FillPolygon(light,new PointF[]{new(r.Left+7,r.Top+21),new(r.Left+21,r.Top+7),new(r.Left+28,r.Top+7),new(r.Left+7,r.Top+28)});
        g.FillPolygon(light,new PointF[]{new(r.Right-7,r.Bottom-21),new(r.Right-21,r.Bottom-7),new(r.Right-28,r.Bottom-7),new(r.Right-7,r.Bottom-28)});
        using var fine=new Pen(Color.FromArgb(90,210,213,217),1);
        g.DrawLines(fine,new PointF[]{new(r.Left+6,r.Top+41),new(r.Left+6,r.Top+20),new(r.Left+20,r.Top+6),new(r.Left+41,r.Top+6)});
        g.DrawLines(fine,new PointF[]{new(r.Right-6,r.Bottom-41),new(r.Right-6,r.Bottom-20),new(r.Right-20,r.Bottom-6),new(r.Right-41,r.Bottom-6)});
    }
    private static void DrawBrandV(Graphics g,RectangleF r)
    {
        PointF[] shape=[new(r.Left,r.Top),new(r.Left+r.Width*.28f,r.Top),new(r.Left+r.Width*.5f,r.Top+r.Height*.46f),
            new(r.Left+r.Width*.72f,r.Top),new(r.Right,r.Top),new(r.Left+r.Width*.5f,r.Bottom)];
        using var path=new GraphicsPath();path.AddPolygon(shape);
        foreach(var stroke in new[]{(12f,8),(7f,14),(3f,24)})
        {using var pen=new Pen(Color.FromArgb(stroke.Item2,Color.White),stroke.Item1){LineJoin=LineJoin.Round};g.DrawPath(pen,path);}
        using var ink=new SolidBrush(Color.White);g.FillPath(ink,path);
    }
    private static void Pattern(Graphics g,RectangleF rect,int alpha=24)
    {
        var saved=g.Save();g.SetClip(rect,CombineMode.Intersect);
        using var pen=new Pen(Color.FromArgb(alpha,177,182,187),1);
        for(float x=rect.Left-30;x<rect.Right+80;x+=66)
        {
            for(var i=0;i<4;i++)
            {
                var w=104-i*14;var y=rect.Top-12+i*4;
                g.DrawPolygon(pen,new PointF[]{new(x-w/2,y),new(x+w/2,y),new(x,y+w*.74f)});
            }
            for(var row=0;row<5;row++)for(var col=0;col<7;col++)
            {
                var tx=x-35+col*11;var ty=rect.Bottom-45+row*10;
                if((row+col)%3!=0)g.DrawPolygon(pen,new PointF[]{new(tx,ty),new(tx+6,ty),new(tx+3,ty+5)});
            }
        }
        g.Restore(saved);
    }
    private void DrawReferenceFrame(Graphics g)
    {
        Pattern(g,new(875,0,520,63),23);Pattern(g,new(0,832,278,50),19);
        using var line=new Pen(Color.FromArgb(160,172,177,181),1);
        g.DrawLine(line,0,64,1672,64);g.DrawLine(line,279,64,279,882);g.DrawLine(line,0,882,1370,882);
        g.DrawLines(line,new PointF[]{new(1370,882),new(1390,864),new(1672,864)});
        using var stripes=new Pen(Color.FromArgb(95,151,155,159),4);
        for(var x=1257;x<1358;x+=10)g.DrawLine(stripes,x,914,x+28,888);
        DrawBrandV(g,new(1260,15,66,36));
        if(Page==ProductPage.Settings)
        {Pattern(g,new(1000,66,645,74),19);DrawBrandV(g,new(1340,84,66,36));}
    }
    private static void DrawTraceGlow(Graphics g,PointF[] points,Color color)
    {
        if(points.Length<2)return;
        foreach(var stroke in new[]{(10f,12),(6f,25),(3.5f,45)})
        {using var pen=new Pen(Color.FromArgb(stroke.Item2,color),stroke.Item1){LineJoin=LineJoin.Round};g.DrawLines(pen,points);}
    }
}
