using VictusFanControl.Control.Adaptive;
using VictusFanControl.Performance;
using VictusFanControl.Telemetry;
namespace VictusFanControl.Product;

internal static class ProductProfilesSelfTest
{
    private static void TestUnifiedDemand(TextWriter output)
    {
        static void Check(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
        static void Reject(Action action){try{action();}catch(Exception ex) when(ex is InvalidDataException or ArgumentException){return;}throw new InvalidOperationException("Invalid unified setting accepted.");}
        var ac=UnifiedFanDemand.Default(false);var battery=UnifiedFanDemand.Default(true);
        AdaptiveFanPolicyInput Input(double cpu=60,double gpu=50,double cpuW=20,double gpuW=35,double cpuLoad=50,double gpuLoad=90)=>new(DateTimeOffset.UtcNow,cpu,cpuW,cpuLoad,gpu,gpuW,gpuLoad);
        var observation=ac.Evaluate(Input());
        Check(Math.Abs(observation.Percent-40)<1e-9&&observation.DominantVariable==0&&Math.Abs(observation.CpuPower-13.3333333333)<1e-6&&Math.Abs(observation.GpuPower-28)<1e-9,"Normalized MAX demand failed a mixed workload.");
        var noPower=ac with{CpuPowerInfluence=0,GpuPowerInfluence=0,CpuLoadInfluence=0,GpuLoadInfluence=0};
        Check(noPower.Evaluate(Input(cpu:70,gpu:35,cpuW:0,gpuW:0,cpuLoad:0,gpuLoad:0)).Percent==60,"Cold variables diluted a hot CPU.");
        Check(ac.Evaluate(Input(cpu:40,gpu:35,cpuW:0,gpuW:0,cpuLoad:0,gpuLoad:100)).Percent==20,"GPU utilization incorrectly forces full cooling.");
        Check((ac with{CpuTemperatureInfluence=150}).Evaluate(Input(cpu:65,gpu:35,cpuW:0,gpuW:0,cpuLoad:0,gpuLoad:0)).Percent==75,"Thermal sensitivity gain lost its independent meaning.");
        Check(ac.Evaluate(Input(gpuW:72.485)).GpuPower>ac.Evaluate(Input(gpuW:70)).GpuPower,"GPU sensor power was clipped to 70 W nominal.");
        Check(battery.Evaluate(Input()).Level<ac.Evaluate(Input()).Level,"Battery preset is not quieter under identical light input.");
        var flat=noPower with{Curve=[new(0,10),new(90,10),new(100,50)]};
        Check(flat.Evaluate(Input(cpu:50,gpu:35) with{CpuRawControlTemperatureC=85}).Level==44&&flat.Evaluate(Input(cpu:50,gpu:35) with{CpuRawControlTemperatureC=90}).Level==50,"Edited curve hid raw CPU thermal floors.");
        Check(flat.Evaluate(Input(cpu:40,gpu:78)).Level>=44&&flat.Evaluate(Input(cpu:40,gpu:81)).Level==50,"Disabled feed-forward or flat curve hid GPU heat.");
        foreach(var invalid in new[]{ac with{CpuTemperatureInfluence=99},ac with{GpuTemperatureInfluence=151},ac with{CpuPowerInfluence=-1},ac with{GpuLoadInfluence=101},ac with{Curve=null!},ac with{Curve=[null!,new(100,50)]},ac with{Curve=[new(1,10),new(100,50)]},ac with{Curve=[new(0,10),new(100,49)]},ac with{Curve=[new(0,20),new(50,10),new(100,50)]},ac with{Curve=[new(0,10),new(50.5,30),new(100,50)]}})Reject(invalid.Validate);
        Reject(()=>ac.Evaluate(Input(cpuW:double.NaN)));Reject(()=>ac.Evaluate(Input() with{CpuRawControlTemperatureC=double.PositiveInfinity}));
        var model=ac;
        for(int i=0;i<6;i++)
        {
            var stronger=model.WithInfluence(i,150*(i<2?1:0)+100*(i>=2?1:0));
            Check(stronger.Evaluate(Input()).Percent>=model.Evaluate(Input()).Percent,"Increasing influence reduced MAX demand.");
        }
        var profiles=new ProductProfiles();var frozen=FanConfigurationStore.Copy(profiles.Ac.Fan);
        var edited=profiles.Ac.Fan with{UnifiedDemand=noPower};
        Check(frozen.UnifiedDemand!.GpuPowerInfluence==60&&edited.UnifiedDemand!.GpuPowerInfluence==0,"Draft influence mutated frozen configuration.");
        var dir=Path.Combine(Path.GetTempPath(),"vfc-unified-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var legacy=profiles with{SchemaVersion=1,Ac=profiles.Ac with{Fan=profiles.Ac.Fan with{UnifiedDemand=null},CpuPl1Watts=30,CpuPl2Watts=35,GpuMaximumMHz=1800},Battery=profiles.Battery with{Fan=profiles.Battery.Fan with{UnifiedDemand=null}}};
            var text=ProductProfilesStore.Serialize(legacy);var path=Path.Combine(dir,"profiles.json");File.WriteAllText(path,text);
            var loaded=ProductProfilesStore.Load(path,out var notice);
            Check(loaded.SchemaVersion==2&&notice is not null&&loaded.Ac.LegacyFan is not null&&File.ReadAllText(path)==text,"Loading legacy changed disk or lost backup.");
            Check(FanConfigurationStore.Serialize(loaded.Ac.LegacyFan!)==FanConfigurationStore.Serialize(legacy.Ac.Fan)&&loaded.Ac.CpuPl1Watts==30&&loaded.Ac.CpuPl2Watts==35&&loaded.Ac.GpuMaximumMHz==1800,"Migration lost legacy curves/tuning or caps.");
            ProductProfilesStore.Save(loaded,path);var backups=Directory.GetFiles(dir,"*.v1-backup-*.json");Check(backups.Length==1&&File.ReadAllText(backups[0])==text,"Legacy save did not preserve exact original bytes.");
            ProductProfilesStore.Save(loaded,path);Check(Directory.GetFiles(dir,"*.v1-backup-*.json").Length==1,"Repeated save created or overwrote backup.");
            var again=ProductProfilesStore.Load(path,out var againNotice);Check(againNotice is null&&ProductProfilesStore.Serialize(again)==ProductProfilesStore.Serialize(loaded),"Unified roundtrip reapplied migration or lost influence.");
            foreach(var encoding in new System.Text.Encoding[]{new System.Text.UTF8Encoding(true),System.Text.Encoding.Unicode,System.Text.Encoding.BigEndianUnicode,System.Text.Encoding.UTF32})
            {
                var encodedDir=Path.Combine(dir,encoding.WebName);Directory.CreateDirectory(encodedDir);
                var encodedPath=Path.Combine(encodedDir,"profiles.json");File.WriteAllText(encodedPath,text,encoding);
                var originalBytes=File.ReadAllBytes(encodedPath);
                var migrated=ProductProfilesStore.Load(encodedPath,out var encodedNotice);
                Check(encodedNotice is not null&&migrated.Ac.CpuPl1Watts==30,"Encoded legacy profile did not load for migration.");
                ProductProfilesStore.Save(migrated,encodedPath);
                var encodedBackups=Directory.GetFiles(encodedDir,"*.v1-backup-*.json");
                Check(encodedBackups.Length==1&&File.ReadAllBytes(encodedBackups[0]).SequenceEqual(originalBytes),"Migration lost exact legacy bytes for "+encoding.WebName);
                ProductProfilesStore.Save(migrated,encodedPath);
                Check(Directory.GetFiles(encodedDir,"*.v1-backup-*.json").Length==1,"Encoded migration repeated its backup.");
                // A mismatching existing backup must keep the original and clean its staged replacement.
                File.WriteAllBytes(encodedPath,originalBytes);File.WriteAllText(encodedBackups[0],"conflicting backup");
                try { ProductProfilesStore.Save(migrated,encodedPath);throw new InvalidOperationException("Conflicting legacy backup was overwritten."); }
                catch(IOException) { }
                Check(File.ReadAllBytes(encodedPath).SequenceEqual(originalBytes)&&File.ReadAllText(encodedBackups[0])=="conflicting backup"&&Directory.GetFiles(encodedDir,"*.tmp").Length==0,"Backup conflict modified retained files or left a staged file.");
            }
        }
        finally{Directory.Delete(dir,true);}
        output.WriteLine("PASS  Unified normalized MAX, independent gains, absolute watts, protected raw heat, frozen draft and exact v1 backups including UTF-8 BOM/UTF-16/UTF-32 and conflict preservation");
    }
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
        TestUnifiedDemand(output);
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
            foreach(var invalid in new[]{"[]","null","{\"schemaVersion\":\"broken\"}","{\"schemaVersion\":1.5}","{\"schemaVersion\":999999999999999999999999}"})
            {
                File.WriteAllText(path,invalid);var safe=ProductProfilesStore.Load(path,out var warning);
                if(warning is null||File.ReadAllText(path)!=invalid)throw new InvalidOperationException("Malformed original was changed on load.");
                ProductProfilesStore.Save(safe,path);
                if(ProductProfilesStore.Load(path,out var savedWarning).SchemaVersion!=2||savedWarning is not null||Directory.GetFiles(dir,"*.tmp").Length!=0)
                    throw new InvalidOperationException("Explicit save could not recover malformed profile JSON.");
            }
        }
        finally { Directory.Delete(dir,true); }
        var lowProfile=original.Ac with { Fan=original.Ac.Fan with { UnifiedDemand=new(){Curve=[new(0,10),new(100,50)]}, Profile=AdaptiveCurveProfiles.Create("8349d1c765b948a4976ea9664ad578ba","Low",original.Ac.Fan.BuildPolicy() with {
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
        var hotSwap=new AdaptiveFanInertiaPolicy(original.Ac.Fan.BuildPolicy(),original.Ac.Fan.Tuning);
        AdaptiveFanPolicyInput Cold(int second)=>new(DateTimeOffset.UnixEpoch.AddSeconds(second),40,5,0,35,5,0);
        var initialSwap=hotSwap.Evaluate(Cold(1));
        hotSwap.UpdateUnifiedDemand(new(){Curve=[new(0,40),new(100,50)]});
        var afterSwap=hotSwap.Evaluate(Cold(2));
        if(initialSwap.EqualFanLevel!=12||afterSwap.EqualFanLevel!=12||afterSwap.SmoothedDemandLevel is not (>12 and <40))
            throw new InvalidOperationException("Live curve replacement reset the fan level or EMA history.");
        if(hotSwap.Evaluate(Cold(2)).Accepted)throw new InvalidOperationException("Live curve replacement lost telemetry continuity.");
        var confirmSwap=new AdaptiveFanInertiaPolicy(original.Ac.Fan.BuildPolicy(),original.Ac.Fan.Tuning);
        confirmSwap.Evaluate(Cold(1));confirmSwap.UpdateUnifiedDemand(new(){Curve=[new(0,40),new(100,50)]});
        for(var second=2;second<=4;second++)confirmSwap.Evaluate(Cold(second));
        confirmSwap.UpdateUnifiedDemand(new(){Curve=[new(0,35),new(100,50)]});
        if(confirmSwap.Evaluate(Cold(5)).EqualFanLevel!=12)throw new InvalidOperationException("Live curve replacement reused confirmation earned by the old curve.");
        output.WriteLine("PASS  Live curve replacement retains current level, EMA history and duplicate acquisition rejection");
        var before=simulator.ElapsedSeconds;Reject(()=>simulator.Advance(new(CpuTemperature:999),1));Reject(()=>simulator.Advance(new(),3601));
        if(simulator.ElapsedSeconds!=before)throw new InvalidOperationException("Invalid simulation advanced virtual time.");
        output.WriteLine("PASS  Offline simulation matches editable inertia across rise/load/cooling/thermal phases; bounded history and no configuration effects");
        output.WriteLine("PASS  Product AC/Battery isolation, strict/null/duplicate schema, explicit malformed-file recovery, atomic failure preservation, PL1/PL2 and bounded configurable GPU execution");
    }
}
