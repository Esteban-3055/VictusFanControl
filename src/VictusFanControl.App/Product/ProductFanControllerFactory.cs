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
        IExperimentalFanPolicy? experimentalPolicy)
    {
        var exactTarget=target==Hp8C40TargetProfile.Instance;
        var automaticAuthorized=!upperRangePreview && fans.BackendCanWrite &&
            (ProductRelease.IsAutomaticAuthorized(target?.Id) || automaticReview);
        return new(fans,Hp8C40AdaptiveCandidateV1.Create() with
            {MaximumLevel=upperRangePreview?Hp8C40FanPreviewTargets.MaximumLogicalLevel:50},
            fans.BackendCanWrite && Hp8C40PostM9UserControlGate.IsManualAuthorizedForTarget(target?.Id),
            automaticAuthorized,
            automaticHardware:exactTarget?hardware:null,
            automaticConfiguration:profiles.Ac.Fan,
            automaticMinimumLevel:exactTarget?10:Hp8C40AutomaticPolicy.MinimumLevel,
            useRawCpuThermalResponse:true,
            // Manual preview and closed-backend startup cannot host an Automatic policy.
            experimentalPolicy:automaticAuthorized?experimentalPolicy:null,
            productTelemetryTolerance:!automaticReview && exactTarget);
    }
}
