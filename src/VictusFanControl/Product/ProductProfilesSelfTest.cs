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
        var json=ProductProfilesStore.Serialize(original);
        foreach(var invalid in new[]{"{}","null","[]",json.Insert(1,"\"schemaVersion\":1,"),json.Replace("\"tuning\": {","\"tuning\": null, \"oldTuning\": {"),json.Replace("\"cpuPl1Watts\": 35","\"cpuPl1Watts\": -1"),json.Replace("\"gpuMaximumMHz\": 1850","\"gpuMaximumMHz\": 9999"),json.Replace("\"input\": 40","\"input\": 40, \"input\": 40")})Reject(()=>ProductProfilesStore.Parse(invalid));
        Reject(()=>(original with{Ac=original.Ac with{Fan=original.Ac.Fan with{Profile=original.Ac.Fan.Profile with{Config=original.Ac.Fan.Profile.Config with{CpuTemperatureCurve=[null!]}}}}}).Validate());
        var dir = Path.Combine(Path.GetTempPath(), "vfc-product-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir,"profiles.json"); ProductProfilesStore.Save(roundtrip,path);
            var loaded = ProductProfilesStore.Load(path,out _); if (loaded.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 31) throw new InvalidOperationException("Persistence lost AC.");
            var intact=File.ReadAllText(path);Reject(()=>ProductProfilesStore.Save(original with{Ac=original.Ac with{CpuPl1Watts=99}},path));
            if(File.ReadAllText(path)!=intact||Directory.GetFiles(dir,"*.tmp").Length!=0)throw new InvalidOperationException("Invalid save modified original or left a temporary file.");
            var absent=ProductProfilesStore.Load(Path.Combine(dir,"missing.json"),out var absentNotice);
            if(absentNotice is not null||absent.Ac.CpuPl1Watts!=35||File.Exists(Path.Combine(dir,"missing.json")))throw new InvalidOperationException("Missing file granted state or wrote defaults.");
            foreach(var invalid in new[]{"{broken}",json.Replace("\"schemaVersion\": 1","\"schemaVersion\": 0"),json.Replace("\"gpuMaximumMHz\": 1850","\"gpuMaximumMHz\": 9999")})
            {
                File.WriteAllText(path,invalid);var safe=ProductProfilesStore.Load(path,out var warning);safe.Validate();
                if(warning is null||File.ReadAllText(path)!=invalid||safe.Ac.GpuMaximumMHz!=1850)throw new InvalidOperationException("Invalid settings did not fall back and retain original.");
            }
            File.WriteAllText(path,"broken"); _ = ProductProfilesStore.Load(path,out var notice);
            if (notice is null || File.ReadAllText(path) != "broken") throw new InvalidOperationException("Corrupt settings were not retained.");
        }
        finally { Directory.Delete(dir,true); }
        output.WriteLine("PASS  Product AC/Battery isolation, strict/null/duplicate schema, atomic failure preservation, PL1/PL2 and closed custom GPU gate");
    }
}
