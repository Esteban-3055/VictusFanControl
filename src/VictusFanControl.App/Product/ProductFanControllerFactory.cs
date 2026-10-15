using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Product;

namespace VictusFanControl.App;

// Shared by real startup and the no-hardware startup regression fixture.
internal static class ProductFanControllerFactory
{
    internal static AdaptiveFanProductionController Create(FanControlCoordinator fans, ProductProfiles profiles,
        HardwareIdentity hardware, HardwareTargetProfile? target, bool automaticReview, bool upperRangePreview,
        Func<long>? automaticMilliseconds=null, Func<DateTimeOffset>? utcNow=null)
    {
        var exactTarget=target==Hp8C40TargetProfile.Instance;
        var maximum=exactTarget && !automaticReview || upperRangePreview ? Hp8C40ProductFanTargets.MaximumLogicalLevel : 50;
        var automaticAuthorized=!upperRangePreview && fans.BackendCanWrite &&
            (ProductRelease.IsAutomaticAuthorized(target?.Id) || automaticReview);
        return new(fans,Hp8C40AdaptiveCandidateV1.Create() with
            {MaximumLevel=maximum},
            fans.BackendCanWrite && Hp8C40PostM9UserControlGate.IsManualAuthorizedForTarget(target?.Id),
            automaticAuthorized,
            automaticHardware:exactTarget?hardware:null,
            automaticMilliseconds:automaticMilliseconds,utcNow:utcNow,
            automaticConfiguration:profiles.Ac.Fan,
            automaticMinimumLevel:exactTarget?10:Hp8C40AutomaticPolicy.MinimumLevel,
            useRawCpuThermalResponse:true,
            // Product control uses only the CPU/GPU baseline; no auxiliary sensor policy.
            experimentalPolicy:null,
            productTelemetryTolerance:!automaticReview && exactTarget,
            automaticMaximumLevel:exactTarget && !automaticReview ? Hp8C40ProductFanTargets.MaximumLogicalLevel : 50);
    }
}
