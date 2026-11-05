namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    // Same renderer for notebook and external curves. Distinct radii keep
    // target and applied visible when they share the exact data coordinates.
    internal static void DrawDemandMarker(Graphics g,PointF point,bool applied)
    {
        float radius=applied?7:12;
        using var halo=new Pen(Background,6);
        g.DrawEllipse(halo,point.X-radius,point.Y-radius,2*radius,2*radius);
        using var ring=new Pen(applied?Green:Yellow,3);
        g.DrawEllipse(ring,point.X-radius,point.Y-radius,2*radius,2*radius);
        if(applied) {
            using var fill=new SolidBrush(Green);
            g.FillEllipse(fill,point.X-4,point.Y-4,8,8);
        }
    }
}
