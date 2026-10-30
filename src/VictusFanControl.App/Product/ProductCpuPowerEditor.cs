using VictusFanControl.Performance;
using VictusFanControl.Product;

namespace VictusFanControl.App;

// Draft editing only. The existing CPU admission and execution limits are unchanged.
internal static class ProductCpuPowerEditor
{
    internal static int MaximumPl1(ProductProfile profile)=>
        Math.Min(CpuPowerProductDefaults.MaximumConfigurablePl1Watts,profile.CpuPl2Watts);

    internal static ProductProfile EditPl1(ProductProfile profile,int value)=>profile with
    {
        CpuPl1Watts=Math.Clamp(value,CpuPowerProductDefaults.MinimumPl1Watts,MaximumPl1(profile))
    };

    internal static ProductProfile EditPl2(ProductProfile profile,int value)
    {
        var pl2=Math.Clamp(value,CpuPowerProductDefaults.MinimumPl2Watts,CpuPowerProductDefaults.MaximumConfigurablePl2Watts);
        return profile with{CpuPl2Watts=pl2,CpuPl1Watts=Math.Min(profile.CpuPl1Watts,pl2)};
    }
}
