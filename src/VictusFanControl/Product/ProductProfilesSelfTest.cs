using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Telemetry;
namespace VictusFanControl.Product;

internal static class ProductProfilesSelfTest
{
    internal static void Run(TextWriter output)
    {
        var original = new ProductProfiles(); var edited = ProductProfilesStore.Copy(original);
        var cpu = AdaptiveCurveProfiles.Curve(edited.Ac.Fan.BuildPolicy(), AdaptiveCurveAxis.CpuTemperature).ToArray(); cpu[3] = cpu[3] with { Level = 31 };
        edited = edited with { Ac = edited.Ac with { Fan = edited.Ac.Fan with { Profile = AdaptiveCurveProfiles.WithCurve(edited.Ac.Fan.Profile, AdaptiveCurveAxis.CpuTemperature, cpu) } } };
        if (original.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 28 || edited.Battery.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != original.Battery.Fan.BuildPolicy().CpuTemperatureCurve[3].Level)
            throw new InvalidOperationException("AC editing mutated another profile.");
        foreach(var source in Enum.GetValues<ProductPowerProfile>())
        {
            var profile=original.Get(source);profile.Validate(source);
            var policy=profile.Fan.BuildPolicy();
            if(policy.CpuTemperatureCurve[0].Level>=25||policy.GpuTemperatureCurve[0].Level>=23||policy.CpuPowerCurve[0].Level!=10||policy.GpuPowerCurve[0].Level!=10||policy.CpuLoadCurve[0].Level!=10||policy.GpuLoadCurve[0].Level!=10)
                throw new InvalidOperationException("Quiet profile is defeated by a feed-forward floor.");
            var quiet=new ProductCurveSimulation(profile.Fan);quiet.Advance(new(40,35,5,5,5,0),300);
            var expected=source==ProductPowerProfile.Ac?12:10;
            if(quiet.History.Any(p=>p.Decision.EqualFanLevel!=expected))throw new InvalidOperationException("Cold quiet preset hunts or loses its source-specific floor.");
            quiet.Advance(new(90,81,60,75,100,100),10);
            if(quiet.Current?.EqualFanLevel!=50)throw new InvalidOperationException("Quiet preset delayed hot endpoint cooling.");
        }
        if(original.Ac.Fan.Profile.Id==original.Battery.Fan.Profile.Id)throw new InvalidOperationException("Quiet defaults lost AC/Battery identity.");
        var roundtrip = ProductProfilesStore.Parse(ProductProfilesStore.Serialize(edited));
        if (roundtrip.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level != 31) throw new InvalidOperationException("Profile roundtrip lost curve.");
        static void Reject(Action a) { try { a(); } catch (Exception e) when (e is ArgumentException or System.Text.Json.JsonException or InvalidDataException or IOException or InvalidOperationException) { return; } throw new InvalidOperationException("Invalid product setting accepted."); }
        Reject(() => (original with { Ac = original.Ac with { CpuPl1Watts = 40, CpuPl2Watts = 20 } }).Validate());
        Reject(() => ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 999")));
        Reject(() => ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original).Insert(1,"\"authority\":true,")));
        var customGpu=new PerformanceGuiSessionConfiguration { AcGpuMaximumMHz = 1800, BatteryGpuMaximumMHz = 1000 };
        customGpu.Validate();
        if(customGpu.GpuPresets().Ac.MaxGraphicsClockMHz!=1800||customGpu.GpuPresets().Battery.MaxGraphicsClockMHz!=1000)throw new InvalidOperationException("Custom GPU preferences did not reach the preset controller.");
        var expandedGpu=new PerformanceGuiSessionConfiguration { AcGpuMaximumMHz = 2500, BatteryGpuMaximumMHz = 2500 };
        expandedGpu.Validate();
        if(expandedGpu.GpuPresets().Ac.MinGraphicsClockMHz!=210||expandedGpu.GpuPresets().Ac.MaxGraphicsClockMHz!=2500||expandedGpu.GpuPresets().Battery.MaxGraphicsClockMHz!=2500)
            throw new InvalidOperationException("Expanded GPU range did not reach NVML presets.");
        Reject(() => new PerformanceGuiSessionConfiguration { AcGpuMaximumMHz = 2501 }.Validate());
        Reject(() => new PerformanceGuiSessionConfiguration { BatteryGpuMaximumMHz = 2501 }.Validate());
        Reject(() => new PerformanceGuiSessionConfiguration { AcGpuMaximumMHz = 209 }.Validate());
        new PerformanceGuiSessionConfiguration().Validate();
        var sample=new TelemetrySnapshot(DateTimeOffset.UtcNow,"fixture",90,18,20,"fixture",45,40,100,3100,3100)
        {CpuExpectedPhysicalCoreCount=3,CpuCoreTemperatures=[new(0,0,"Performance",80),new(1,2,"Performance",70),new(2,4,"Performance",60)]};
        var liveDraft=original.Ac.Fan with{Tuning=original.Ac.Fan.Tuning with{CpuTemperatureSource=CpuDemandTemperatureSource.PackageOrHottestCore}};
        var markers=ProductCurveMarkers.Build(liveDraft,sample,original.Ac.Fan,sample,31,Enum.GetValues<AdaptiveCurveAxis>());
        var appliedMarker=markers.Single(m=>m.Axis==AdaptiveCurveAxis.CpuTemperature&&m.IsApplied);
        var previewMarker=markers.Single(m=>m.Axis==AdaptiveCurveAxis.CpuTemperature&&!m.IsApplied);
        if(markers.Count!=12||appliedMarker.Input!=70||appliedMarker.Level!=31||previewMarker.Input!=90||previewMarker.Level!=50)
            throw new InvalidOperationException("Curve marker mixed draft input, raw safety temperature or executed level.");
        if(ProductCurveMarkers.Build(liveDraft,sample with{GpuPowerW=double.NaN},null,null,null,AdaptiveCurveAxis.GpuPower).Count!=0||
            ProductCurveMarkers.Build(liveDraft,null,null,null,null,AdaptiveCurveAxis.CpuTemperature).Count!=0)
            throw new InvalidOperationException("Invalid/missing sensor produced a live marker.");
        var expandedProfiles=original with{Ac=original.Ac with{GpuMaximumMHz=2500},Battery=original.Battery with{GpuMaximumMHz=210}};
        var expandedRoundtrip=ProductProfilesStore.Parse(ProductProfilesStore.Serialize(expandedProfiles));
        if(expandedRoundtrip.Ac.GpuMaximumMHz!=2500||expandedRoundtrip.Battery.GpuMaximumMHz!=210||original.Ac.GpuMaximumMHz!=1850||original.Battery.GpuMaximumMHz!=1200)
            throw new InvalidOperationException("GPU range changed defaults or lost independent persisted values.");
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
        var lowProfile=original.Ac with { Fan=original.Ac.Fan with { Profile=AdaptiveCurveProfiles.Create("8349d1c765b948a4976ea9664ad578ba","Low",original.Ac.Fan.BuildPolicy() with {
            CpuTemperatureCurve=[new(0,10),new(110,50)],GpuTemperatureCurve=[new(0,10),new(100,50)],
            CpuPowerCurve=[new(0,10),new(150,50)],GpuPowerCurve=[new(0,10),new(200,50)],
            CpuLoadCurve=[new(0,10),new(100,50)],GpuLoadCurve=[new(0,10),new(100,50)] }) } };
        var low=ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original with{Ac=lowProfile}));
        var lowSimulation=new ProductCurveSimulation(low.Ac.Fan);lowSimulation.Advance(new(0,0,0,0,0,0),1);
        if(lowSimulation.Current?.EqualFanLevel!=10||low.Ac.Fan.BuildPolicy().CpuTemperatureCurve[0].Level!=10||Hp8C40AutomaticPolicy.Create(low.Ac.Fan.BuildPolicy()).MinimumLevel!=30)throw new InvalidOperationException("Offline 10-level curve lost range or changed production envelope.");
        var legacy=ProductProfilesStore.Parse(ProductProfilesStore.Serialize(original with { Ac=original.Ac with { Fan=original.Ac.Fan with { Tuning=original.Ac.Fan.Tuning with { MinimumLevel=30 } } } }));
        if(legacy.Ac.Fan.Tuning.MinimumLevel!=10||AdaptiveCurveProfiles.Validate(legacy.Ac.Fan.Profile).CpuTemperatureCurve[0].Level!=AdaptiveCurveProfiles.Validate(original.Ac.Fan.Profile).CpuTemperatureCurve[0].Level)throw new InvalidOperationException("Legacy editor range migration lost stored curve.");
        var fan=original.Ac.Fan;var simulator=new ProductCurveSimulation(fan);var reference=new AdaptiveFanInertiaPolicy(fan.BuildPolicy(),fan.Tuning);
        var unchanged=ProductProfilesStore.Serialize(original);var elapsed=0;
        foreach(var phase in new[]{(new ProductSimulationInputs(),1),(new ProductSimulationInputs(80,70,40,110,100,100),1201),(new ProductSimulationInputs(),180),(new ProductSimulationInputs(90,80,60,130,100,100),10)})
        {
            simulator.Advance(phase.Item1,phase.Item2);AdaptiveFanInertiaDecision? expected=null;
            for(int tick=0;tick<phase.Item2;tick++){elapsed++;var v=phase.Item1;expected=reference.Evaluate(new(DateTimeOffset.UnixEpoch.AddSeconds(elapsed),v.CpuTemperature,v.CpuPower,v.CpuLoad,v.GpuTemperature,v.GpuPower,v.GpuLoad));}
            if(simulator.Current!=expected||simulator.ElapsedSeconds!=elapsed||simulator.History.Any(p=>p.Decision.EqualFanLevel is <10 or >50)||simulator.History.Count>600)throw new InvalidOperationException("Simulation diverged from editable inertia policy.");
            if(elapsed==1202&&simulator.Current?.SustainedLoadCooling!=true)throw new InvalidOperationException("Simulation lost sustained-load history.");
        }
        if(unchanged!=ProductProfilesStore.Serialize(original))throw new InvalidOperationException("Simulation mutated configuration.");
        var before=simulator.ElapsedSeconds;Reject(()=>simulator.Advance(new(CpuTemperature:999),1));Reject(()=>simulator.Advance(new(),3601));
        if(simulator.ElapsedSeconds!=before)throw new InvalidOperationException("Invalid simulation advanced virtual time.");
        output.WriteLine("PASS  Offline simulation matches editable inertia across rise/load/cooling/thermal phases; bounded history and no configuration effects");
        output.WriteLine("PASS  Product AC/Battery isolation, strict/null/duplicate schema, atomic failure preservation, PL1/PL2 and bounded configurable GPU execution");
    }
}
