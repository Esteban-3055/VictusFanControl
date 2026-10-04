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
            var policy = new AdaptiveFanInertiaPolicy(configuration.BuildPolicy(), configuration.Tuning);
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
        Check("normal rise is faster, EMA retains precision, invalid input resets", () =>
        {
            var origin = DateTimeOffset.UtcNow;
            var tuned = new AdaptiveFinalDemandFilter(configuration.Tuning);
            var legacy = new AdaptiveFinalDemandFilter();
            double Read(AdaptiveFinalDemandFilter f, int sec, double raw) => f.Evaluate(origin.AddSeconds(sec),raw,26,50,TimeSpan.FromSeconds(3),false);
            Read(tuned,0,26); Read(legacy,0,26);
            var a = Read(tuned,1,40); var b = Read(legacy,1,40);
            Require(a > b && a != Math.Round(a,1));
            try { Read(tuned,2,double.NaN); throw new ArgumentException("Invalid accepted."); }
            catch (InvalidOperationException) { }
            Require(Read(tuned,3,40) == 40);
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
