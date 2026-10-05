using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
namespace VictusFanControl.Product;

internal static class ProductProfilesSelfTest
{
    internal static void Run(TextWriter output)
    {
        var original = new ProductProfiles(); var edited = ProductProfilesStore.Copy(original);
        var cpu = AdaptiveCurveProfiles.Curve(edited.Ac.Fan.BuildPolicy(), AdaptiveCurveAxis.CpuTemperature).ToArray(); cpu[3] = cpu[3] with { Level = 31 };
        edited = edited with { Ac = edited.Ac with { Fan = edited.Ac.Fan with { Profile = AdaptiveCurveProfiles.WithCurve(edited.Ac.Fan.Profile, AdaptiveCurveAxis.CpuTemperature, cpu) } } };
        if (original.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 30 || edited.Battery.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 30)
            throw new InvalidOperationException("AC editing mutated another profile.");
        var roundtrip = ProductProfilesStore.Parse(ProductProfilesStore.Serialize(edited));
        if (roundtrip.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 31) throw new InvalidOperationException("Profile roundtrip lost curve.");
        static void Reject(Action a) { try { a(); } catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or InvalidDataException or IOException or InvalidOperationException) { return; } throw new InvalidOperationException("Invalid product setting accepted."); }
        Reject(() => (original with { Ac = original.Ac with { CpuPl1Watts = 40, CpuPl2Watts = 20 } }).Validate());
        Reject(() => ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999")));
        Reject(() => ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original).Insert(1,"\"authority\":true,")));
        Reject(() => new PerformanceGuiSessionConfiguration { AcGpuMaximumMHz = 1800 }.Validate());
        new PerformanceGuiSessionConfiguration().Validate();
        var dir = Path.Combine(Path.GetTempPath(), "vfc-product-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir,"profiles.json"); ProductProfilesStore.Save(roundtrip,path);
            var loaded = ProductProfilesStore.Load(path,out _); if (loaded.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 31) throw new InvalidOperationException("Persistence lost AC.");
            File.WriteAllText(path,"broken"); _ = ProductProfilesStore.Load(path,out var notice);
            if (notice is null || File.ReadAllText(path) != "broken") throw new InvalidOperationException("Corrupt settings were not retained.");
        }
        finally { Directory.Delete(dir,true); }
        output.WriteLine("PASS  Product AC/Battery isolation, strict persistence, PL1/PL2 and closed custom GPU gate");
    }
}
