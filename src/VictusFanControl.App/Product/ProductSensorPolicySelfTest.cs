using System.Text.Json.Nodes;
using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static class ProductSensorPolicySelfTest
{
    internal static void Run(Action<bool,string> require)
    {
        var profiles=new ProductProfiles
        {
            Ac=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac) with{CpuPl1Watts=28,CpuPl2Watts=38},
            StartMinimized=true,ActivateAutomaticOnStart=true
        };
        var baseline=ProductProfilesStore.Serialize(profiles);
        var legacy=JsonNode.Parse(baseline)!;
        legacy["experimentalPlatformRetention"]=true;
        var imported=ProductProfilesStore.Parse(legacy.ToJsonString());
        require(!imported.ExperimentalPlatformRetention && ProductProfilesStore.Serialize(imported)==baseline,
            "Legacy retention import changed a curve, cap, protection or startup preference.");
        require(!ProductProfilesStore.Copy(profiles with{ExperimentalPlatformRetention=true}).ExperimentalPlatformRetention,
            "Copy/save restored the retired retention flag.");
        legacy.AsObject().Remove("experimentalPlatformRetention");
        require(ProductProfilesStore.Serialize(ProductProfilesStore.Parse(legacy.ToJsonString()))==baseline,
            "Profiles without the retired field changed during import.");
        bool refused=false;
        try{PlatformThermalExperimentForm.Run("fixture://missing-modules","fixture://unused-output");}
        catch(NotSupportedException){refused=true;}
        require(refused,"Retired platform experiment remained reachable.");
        refused=false;
        using var experiment=new PhysicalPlatformExperiment(
            Path.Combine("logs","retired-platform-policy-"+Guid.NewGuid().ToString("N")),profiles.Ac.Fan);
        try{_ = new ProductRuntime("fixture://missing-modules",profiles,
            platformExperiment:experiment);}
        catch(NotSupportedException){refused=true;}
        require(refused,"Runtime accepted the retired platform policy before hardware construction.");
        ProductFanControllerFactorySelfTest.Run(require);
        Console.WriteLine("Product sensor policy: PASS (legacy flag cleared, preferences preserved, retired live entry rejected, CPU/GPU baseline without auxiliary sensors; zero hardware IO).");
    }
}
