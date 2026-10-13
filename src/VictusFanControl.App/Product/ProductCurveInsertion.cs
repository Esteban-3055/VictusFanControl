using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal static class ProductCurveInsertion
{
    internal static bool CanInsert(IReadOnlyList<AdaptiveFanCurvePoint> points,int selected) =>
        points.Count < 64 && selected >= 0 && selected < points.Count-1 && points[selected+1].Input-points[selected].Input >= 2;
    internal static AdaptiveFanCurvePoint[] Insert(IReadOnlyList<AdaptiveFanCurvePoint> points,int selected)
    {
        if(!CanInsert(points,selected))throw new InvalidOperationException("Selecciona un punto con espacio a su derecha.");
        var x=Math.Floor((points[selected].Input+points[selected+1].Input)/2);
        var y=Math.Round(AdaptiveCurveProfiles.Interpolate(points,x));
        var result=points.ToList();result.Insert(selected+1,new(x,y));return result.ToArray();
    }
}
