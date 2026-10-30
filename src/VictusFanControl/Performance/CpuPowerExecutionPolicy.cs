namespace VictusFanControl.Performance;

// The expanded policy is selected only by the exact-target product GUI host.
// Historical probes/qualification retain DownwardOnly. Recovery remains release-only;
// a journal never grants permission to resume or increase limits after restart.
internal enum CpuPowerExecutionPolicy { DownwardOnly, ProductPl1ToPl2 }

internal static class CpuPowerExecutionRules
{
    internal static bool ValidRequest(CpuPowerLimitRequest request,CpuPowerExecutionPolicy policy)=>
        double.IsFinite(request.Pl1Watts)&&double.IsFinite(request.Pl2Watts)&&
        request.Pl1Watts>=CpuPowerProductDefaults.MinimumPl1Watts&&request.Pl2Watts>=request.Pl1Watts&&
        (policy==CpuPowerExecutionPolicy.DownwardOnly||policy==CpuPowerExecutionPolicy.ProductPl1ToPl2&&
            request.Pl2Watts<=CpuPowerProductDefaults.MaximumConfigurablePl2Watts);

    internal static bool ValidPlan(double pl1,double pl2,CpuPowerLimitSnapshot baseline,CpuPowerExecutionPolicy policy)=>
        double.IsFinite(pl1)&&double.IsFinite(pl2)&&pl1>=CpuPowerProductDefaults.MinimumPl1Watts&&pl2>=pl1&&
        pl2<baseline.Pl2Watts&&(policy switch
        {
            CpuPowerExecutionPolicy.DownwardOnly=>pl1<baseline.Pl1Watts,
            CpuPowerExecutionPolicy.ProductPl1ToPl2=>baseline.Pl1Watts==45&&baseline.Pl2Watts==115&&
                pl2<=CpuPowerProductDefaults.MaximumConfigurablePl2Watts,
            _=>false
        });
}
