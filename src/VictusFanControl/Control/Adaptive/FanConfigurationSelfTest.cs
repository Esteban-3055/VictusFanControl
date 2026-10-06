using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

internal static class FanConfigurationSelfTest
{
    internal static int Run(TextWriter output)
    {
        var failures = 0;
        void Check(string name, Action test)
        {
            try { test(); output.WriteLine("PASS: fan settings — " + name); }
            catch (Exception ex) { failures++; output.WriteLine("FAIL: fan settings — " + name + ": " + ex); }
        }
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
        static void Reject(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or ArgumentException) { return; }
            throw new InvalidOperationException("Unsafe configuration was accepted.");
        }
        var configuration = new FanConfiguration();
        Check("strict persistence, six curves, defensive copy and protected limits", () =>
        {
            var text = FanConfigurationStore.Serialize(configuration);
            var copy = FanConfigurationStore.Parse(text);
            Require(copy.Tuning == configuration.Tuning && copy.Profile.Name == "Firmware suave");
            Require(!ReferenceEquals(copy.Profile.Config.CpuTemperatureCurve, configuration.Profile.Config.CpuTemperatureCurve));
            Reject(() => FanConfigurationStore.Parse(text.Replace("\"schemaVersion\": 1", "\"automaticExecutionAuthorized\": true, \"schemaVersion\": 1")));
            foreach (var tuning in new[] { configuration.Tuning with { MinimumLevel = 51 },
                configuration.Tuning with { MaximumLevel = 25 }, configuration.Tuning with { ThermalMaximumUpStepLevels = 5 },
                configuration.Tuning with { CpuThermalOverrideC = 86 }, configuration.Tuning with { NormalPollingDelayMilliseconds = 1600 },
                configuration.Tuning with { FallTimeConstantSeconds = double.NaN } })
                Reject(() => (configuration with { Tuning = tuning }).BuildPolicy());
            Reject(() => (configuration with { SchemaVersion = 2 }).BuildPolicy());
            Require(copy.BuildPolicy().MinimumLevel == 26 && copy.BuildPolicy().MaximumUpStepPerSample == 4);
        });
        Check("average ignores hottest demand; invalid/missing cores fail closed; archived source stays original", () =>
        {
            var sample = new TelemetrySnapshot(DateTimeOffset.UtcNow, "CPU", 90, 5, 10, "GPU", 35, 0, 0, 3000, 3000)
            {
                CpuExpectedPhysicalCoreCount = 2,
                CpuCoreTemperatures = [new(0,0,"Performance",90), new(1,1,"Efficiency",40)]
            };
            Require(configuration.Tuning.CpuTemperatureSource == CpuDemandTemperatureSource.CoreAverage);
            Require(CpuDemandTemperature.Select(sample,configuration.Tuning.CpuTemperatureSource) == 65);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.PackageOrHottestCore) == 90);
            Require(CpuDemandTemperature.Select(sample with { CpuExpectedPhysicalCoreCount=3 },CpuDemandTemperatureSource.CoreAverage) is null);
            Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=[new(0,0,"P",double.NaN),new(1,1,"E",40)] },CpuDemandTemperatureSource.CoreAverage) is null);
            Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=[new(0,0,"P",90),new(0,1,"E",40)] },CpuDemandTemperatureSource.CoreAverage) is null);
            var text = FanConfigurationStore.Serialize(configuration);
            var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            Require(node["tuning"]!.AsObject().Remove("cpuTemperatureSource"));
            var archived = node.ToJsonString();
            Require(archived != text && FanConfigurationStore.Parse(archived).Tuning.CpuTemperatureSource == CpuDemandTemperatureSource.PackageOrHottestCore);
            Reject(() => (configuration with { Tuning=configuration.Tuning with { CpuTemperatureSource=(CpuDemandTemperatureSource)99 } }).BuildPolicy());
        });
        Check("P-core aggregation selects physical cores, top N is configurable, safety stays raw", () =>
        {
            var sample = new TelemetrySnapshot(DateTimeOffset.UtcNow,"CPU",80,5,10,"GPU",35,0,0,3000,3000)
            {
                CpuExpectedPhysicalCoreCount=8,
                // Deliberately interleave types; do not rely on array indices.
                CpuCoreTemperatures=[new(9,18,"Efficiency",99), new(5,10,"Performance",90),
                    new(3,6,"Performance",58), new(8,16,"Efficiency",40), new(1,2,"Performance",56),
                    new(4,8,"Performance",80), new(0,0,"Performance",55), new(2,4,"Performance",57)]
            };
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.PerformanceCoreAverage)==66);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage)==76);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage,1)==90);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage,2)==85);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage,6)==66);
            Require(sample.CpuControlTemperatureC==99); // Even an excluded E-Core still protects safety.
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage,7) is null);
            Require(CpuDemandTemperature.Select(sample,CpuDemandTemperatureSource.HottestPerformanceCoresAverage,0) is null);
            foreach(var source in new[]{CpuDemandTemperatureSource.PerformanceCoreAverage,CpuDemandTemperatureSource.HottestPerformanceCoresAverage})
            {
                Require(CpuDemandTemperature.Select(sample with { CpuExpectedPhysicalCoreCount=9 },source) is null);
                Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=sample.CpuCoreTemperatures.Select(c=>c with {CoreType="Unknown"}).ToArray() },source) is null);
                Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=sample.CpuCoreTemperatures.Select(c=>c with {CoreType="Efficiency"}).ToArray() },source) is null);
                Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=sample.CpuCoreTemperatures.Select(c=>c with {CoreIndex=0}).ToArray() },source) is null);
                Require(CpuDemandTemperature.Select(sample with { CpuCoreTemperatures=sample.CpuCoreTemperatures.Select(c=>c.CoreIndex==9? c with {TemperatureC=double.NaN}:c).ToArray() },source) is null);
                var copy=FanConfigurationStore.Copy(configuration with {Tuning=configuration.Tuning with {CpuTemperatureSource=source,HottestPerformanceCoreCount=2}});
                Require(copy.Tuning.CpuTemperatureSource==source && copy.Tuning.HottestPerformanceCoreCount==2);
            }
            Reject(()=>(configuration with {Tuning=configuration.Tuning with {HottestPerformanceCoreCount=0}}).BuildPolicy());
            Reject(()=>(configuration with {Tuning=configuration.Tuning with {HottestPerformanceCoreCount=65}}).BuildPolicy());
            var archived=System.Text.Json.Nodes.JsonNode.Parse(FanConfigurationStore.Serialize(configuration))!;
            archived["tuning"]!.AsObject().Remove("hottestPerformanceCoreCount");
            Require(FanConfigurationStore.Parse(archived.ToJsonString()).Tuning.HottestPerformanceCoreCount==3);
        });
        Check("raw heat acts immediately without phantom normal rises; descent waits 16 seconds", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            AdaptiveFanPolicyInput Input(int second, double cpu) => new(origin.AddSeconds(second), cpu, 0, 0, 35, 0, 0);
            var policy = new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(), new AdaptiveFanTuning());
            Require(policy.Evaluate(Input(0, 45)).EqualFanLevel == 26);
            var hot = policy.Evaluate(Input(1, 89));
            Require(hot.ThermalOverride && hot.EqualFanLevel == 30 && hot.SmoothedDemandLevel == hot.RawDemandLevel);
            for (var second = 2; second < 18; second++)
            {
                var cold = policy.Evaluate(Input(second, 45));
                Require(cold.EqualFanLevel == 30 && cold.SmoothedDemandLevel == 26);
            }
            Require(policy.Evaluate(Input(18, 45)).EqualFanLevel == 29);
            policy.Reset();
            Require(policy.Evaluate(Input(0, 89)).EqualFanLevel >= 48); // No assumed cold startup.
            Require(AdaptiveFanInertiaPolicy.RoundNormalDemandToTenth(3.05) == 3.0 &&
                AdaptiveFanInertiaPolicy.RoundNormalDemandToTenth(3.06) == 3.1);
        });
        Check("raw CPU heat bypasses cool averages and low edited demand, with protected rise", () =>
        {
            var origin=DateTimeOffset.UtcNow;
            var policy=new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(),configuration.Tuning);
            AdaptiveFanPolicyInput Input(int sec,double raw)=>new(origin.AddSeconds(sec),60,5,5,35,0,0)
                {CpuRawControlTemperatureC=raw};
            Require(policy.Evaluate(Input(0,63)).EqualFanLevel==26);
            var high=policy.Evaluate(Input(1,96));
            Require(high.Accepted&&high.ThermalOverride&&high.EqualFanLevel==30&&high.ActuationDemandLevel>=44);
            var cool=policy.Evaluate(Input(2,62));Require(cool.Accepted&&!cool.ThermalOverride&&cool.EqualFanLevel==30);
            Require(!policy.Evaluate(Input(3,double.NaN)).Accepted);
            policy.Reset();Require(policy.Evaluate(Input(0,85)).ThermalOverride);
            policy.Reset();var gpu=policy.Evaluate(Input(0,60) with{GpuTemperatureC=78});
            Require(gpu.ThermalOverride&&gpu.ActuationDemandLevel>=44);
        });
        Check("normal rise is faster, EMA retains precision, invalid input resets", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            var tuned = new AdaptiveFinalDemandFilter(new AdaptiveFanTuning());
            var legacy = new AdaptiveFinalDemandFilter();
            double Read(AdaptiveFinalDemandFilter f, int sec, double raw) => f.Evaluate(origin.AddSeconds(sec),raw,26,50,TimeSpan.FromSeconds(3),false);
            Read(tuned,0,26); Read(legacy,0,26);
            var a = Read(tuned,1,40); var b = Read(legacy,1,40);
            Require(a > b && a != Math.Round(a,1));
            try { Read(tuned,2,double.NaN); throw new ArgumentException("Invalid accepted."); }
            catch (InvalidOperationException) { }
            Require(Read(tuned,3,40) == 40);
        });
        Check("adaptive response survives storage; archived settings retain fixed descent", () =>
        {
            var copy = FanConfigurationStore.Copy(configuration);
            Require(copy.Tuning.AdaptiveDescentEnabled && copy.Tuning.RiseTimeConstantSeconds == 8 &&
                copy.Tuning.IncreaseConfirmationSeconds == 3);
            var archived = System.Text.Json.Nodes.JsonNode.Parse(FanConfigurationStore.Serialize(configuration))!;
            archived["tuning"]!.AsObject().Remove("adaptiveDescentEnabled");
            Require(!FanConfigurationStore.Parse(archived.ToJsonString()).Tuning.AdaptiveDescentEnabled);
            Reject(() => (configuration with { Tuning = configuration.Tuning with { SustainedLoadSeconds = double.NaN } }).BuildPolicy());
            Reject(() => (configuration with { Tuning = configuration.Tuning with { ShortLoadDecreaseConfirmationSeconds = 20 } }).BuildPolicy());
        });
        Check("brief heat descends quickly; normal rise has inertia; sustained heat bypasses it", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            AdaptiveFanPolicyInput Input(int s, double cpu) => new(origin.AddSeconds(s),cpu,0,0,35,0,0);
            var p = new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(),configuration.Tuning);
            Require(p.Evaluate(Input(0,45)).EqualFanLevel == 26);
            Require(p.Evaluate(Input(1,89)).EqualFanLevel == 30);
            for(var s=2;s<6;s++) Require(p.Evaluate(Input(s,45)).EqualFanLevel == 30);
            Require(p.Evaluate(Input(6,45)).EqualFanLevel == 29);
            p.Reset();
            p.Evaluate(Input(0,45));
            for(var s=1;s<4;s++) Require(p.Evaluate(Input(s,78)).EqualFanLevel == 26);
            Require(p.Evaluate(Input(4,78)).EqualFanLevel == 27);
            Require(p.Evaluate(Input(5,89)).ThermalOverride);
        });
        Check("20 minutes counts loaded intervals; idle gaps do not fabricate thermal history", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            var t = configuration.Tuning;
            AdaptiveFanPolicyInput Input(int s, bool loaded) => new(origin.AddSeconds(s),60,loaded?30:0,0,45,0,0);
            var h = new AdaptiveLoadHistory();
            for(var s=0;s<1200;s++) h.Observe(Input(s,true),t);
            Require(!h.SustainedLoadCooling && h.ObservedLoadSeconds == 1199);
            h.Observe(Input(1200,true),t);
            Require(h.SustainedLoadCooling);
            h.BreakContinuity();
            for(var s=1201;s<1321;s++) h.Observe(Input(s,false),t);
            Require(h.SustainedLoadCooling); // First fresh sample starts, not completes, the idle interval.
            h.Observe(Input(1321,false),t);
            Require(!h.SustainedLoadCooling);
            h.Reset();
            for(var s=0;s<=50;s++) h.Observe(Input(s,true),t);
            for(var s=51;s<=60;s++) h.Observe(Input(s,false),t);
            h.Observe(Input(61,true),t);h.Observe(Input(62,true),t);
            Require(h.ObservedLoadSeconds == 51);
            for(var s=63;s<=94;s++) h.Observe(Input(s,false),t);
            Require(h.ObservedLoadSeconds == 0);
            h.Observe(Input(95,true) with { CpuPackagePowerW=0,GpuLoadPercent=60 },t);
            h.Observe(Input(96,true) with { CpuPackagePowerW=0,GpuLoadPercent=60 },t);
            Require(h.ObservedLoadSeconds == 1);
        });
        Check("qualified load selects slow descent; invalid samples cannot clear its history", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            AdaptiveFanPolicyInput Input(int s,bool loaded) => new(origin.AddSeconds(s),45,loaded?30:0,0,35,0,0);
            var p = new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(),configuration.Tuning);
            for(var s=0;s<=1200;s++) p.Evaluate(Input(s,true));
            Require(p.Evaluate(Input(1201,true) with {CpuEffectiveTemperatureC=89}).SustainedLoadCooling);
            for(var s=1202;s<1218;s++) Require(p.Evaluate(Input(s,false)).EqualFanLevel == 30);
            Require(p.Evaluate(Input(1218,false)).EqualFanLevel == 29);
            Require(!p.Evaluate(Input(1219,false) with {CpuEffectiveTemperatureC=double.NaN}).Accepted);
            Require(p.Evaluate(Input(1220,false)).SustainedLoadCooling);
            p.Reset();
            Require(!p.Evaluate(Input(1221,false)).SustainedLoadCooling);
        });
        var directory = Path.Combine(Path.GetTempPath(),"vfc-settings-"+Guid.NewGuid().ToString("N"));
        try
        {
            Check("save failure preserves prior file; corrupt load falls back; experiment clamps only explicitly", () =>
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory,"settings.json");
                FanConfigurationStore.Save(configuration,path);
                var original = File.ReadAllText(path);
                Reject(() => FanConfigurationStore.Save(configuration with { Tuning = configuration.Tuning with { MinimumLevel = 51 } },path));
                Require(File.ReadAllText(path) == original && Directory.GetFiles(directory,"*.tmp").Length == 0);
                Require(new WmiFanExperimentOptions(directory,directory,30,false,null,path).ReadConfiguration()!.Tuning.MinimumLevel == 26);
                Reject(() => new WmiFanExperimentOptions(directory,directory,30,true,null,path).ReadConfiguration());
                FanConfigurationStore.Save(configuration with { Tuning = configuration.Tuning with { MinimumLevel = 30 } },path);
                Require(new WmiFanExperimentOptions(directory,directory,30,true,null,path).ReadConfiguration()!.Tuning.MinimumLevel == 30);
                File.WriteAllText(path,"invalid");
                Require(FanConfigurationStore.Load(path,out var notice).Tuning.MinimumLevel == 26 && notice is not null);
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }
        return failures;
    }
}
