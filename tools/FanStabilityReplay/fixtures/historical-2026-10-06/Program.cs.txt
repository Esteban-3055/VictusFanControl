using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

// Pure offline policy comparison; no backend, hardware commands, admission or thermal plant model.
if(args.Length!=2)throw new ArgumentException("Expected dataset.json and output.json paths.");
var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true};
using var document=JsonDocument.Parse(File.ReadAllText(args[0]));
var output=new List<object>();
foreach(var session in document.RootElement.EnumerateArray())
{
    var samples=session.GetProperty("snapshots").EnumerateArray().Select(e=>e.Deserialize<TelemetrySnapshot>(json)!).OrderBy(s=>s.Timestamp).ToArray();
    var events=session.GetProperty("configEvents").EnumerateArray().ToArray();
    foreach(var run in session.GetProperty("decisions").EnumerateArray().GroupBy(d=>d.GetProperty("automaticSessionId").GetString()))
    {
        var decisions=run.ToDictionary(d=>d.GetProperty("snapshotTimestamp").GetDateTimeOffset());
        var first=decisions.Keys.Min();var last=decisions.Keys.Max();
        var start=events.Last(e=>e.GetProperty("kind").GetString()=="start"&&e.GetProperty("data").GetProperty("automaticSessionId").GetString()==run.Key);
        var original=start.GetProperty("data").GetProperty("fan").Deserialize<FanConfiguration>(json)!;
        var changes=events.Where(e=>e.GetProperty("kind").GetString()!="start"&&e.GetProperty("data").GetProperty("automaticSessionId").GetString()==run.Key).OrderBy(e=>e.GetProperty("time").GetDateTimeOffset()).ToArray();
        foreach(var mode in new[]{"recorded-settings","previous-default","stable-inertia-only","stable-preset"})
        {
            var battery=original.Profile.Name.Contains("Batería",StringComparison.OrdinalIgnoreCase);
            var fan=mode=="recorded-settings"?original:ProductProfiles.DefaultProfile(battery?ProductPowerProfile.Battery:ProductPowerProfile.Ac).Fan;
            var oldCurve=battery?new UnifiedFanDemand{CpuPowerInfluence=20,GpuPowerInfluence=35,CpuLoadInfluence=10,GpuLoadInfluence=10,
                Curve=[new(0,10),new(25,10),new(40,12),new(60,24),new(76,35),new(90,44),new(100,50)]}:
                new UnifiedFanDemand{Curve=[new(0,12),new(20,12),new(40,21),new(60,28),new(76,35),new(90,44),new(100,50)]};
            if(mode is "previous-default" or "stable-inertia-only")fan=fan with{UnifiedDemand=oldCurve};
            if(mode=="previous-default")fan=fan with{Tuning=AdaptiveFanTuning.WithSmoothAdaptiveResponse(fan.Tuning) with{NormalDecreaseHysteresisLevels=0,ThermalDecreaseHoldSeconds=0}};
            var policy=new AdaptiveFanInertiaPolicy(fan.BuildPolicy(),fan.Tuning);
            var rows=new List<object>();int index=0,rejected=0,observed=0;
            foreach(var s in samples.Where(s=>s.Timestamp>=first&&s.Timestamp<=last))
            {
                while(index<changes.Length&&changes[index].GetProperty("time").GetDateTimeOffset()<=s.Timestamp)
                {
                    var e=changes[index++];if(mode!="recorded-settings")continue;
                    var d=e.GetProperty("data");var kind=e.GetProperty("kind").GetString();
                    if(kind=="curve"){var v=d.GetProperty("demand").Deserialize<UnifiedFanDemand>(json)!;policy.UpdateUnifiedDemand(v);fan=fan with{UnifiedDemand=v};}
                    if(kind=="tuning"){var v=d.GetProperty("tuning").Deserialize<AdaptiveFanTuning>(json)!;policy.UpdateTuning(v);fan=fan with{Tuning=v};}
                    if(kind=="source"){fan=d.GetProperty("fan").Deserialize<FanConfiguration>(json)!;policy.UpdateUnifiedDemand(fan.UnifiedDemand!);policy.UpdateTuning(fan.Tuning);}
                }
                var cpu=CpuDemandTemperature.Select(s,fan.Tuning.CpuTemperatureSource,fan.Tuning.HottestPerformanceCoreCount);
                if(!s.IsComplete||!cpu.HasValue){rejected++;continue;}
                var input=new AdaptiveFanPolicyInput(s.Timestamp,cpu.Value,s.CpuPackagePowerW!.Value,s.CpuLoadPercent!.Value,s.GpuTemperatureC!.Value,s.GpuPowerW!.Value,s.GpuLoadPercent!.Value){CpuRawControlTemperatureC=s.CpuControlTemperatureC};
                if(decisions.TryGetValue(s.Timestamp,out var recorded))
                {
                    var result=policy.Evaluate(input);if(!result.Accepted){rejected++;continue;}
                    rows.Add(new{timestamp=s.Timestamp,level=result.EqualFanLevel,raw=result.RawDemandLevel,smoothed=result.SmoothedDemandLevel,
                        thermal=result.ThermalOverride,detail=result.Detail,recordedLevel=recorded.GetProperty("decision").GetProperty("EqualFanLevel").GetInt32(),
                        cpuRaw=s.CpuControlTemperatureC,gpu=s.GpuTemperatureC,sustained=result.SustainedLoadCooling});
                }
                else {if(!policy.ObserveDuringActuation(input,out _))rejected++;else observed++;}
            }
            output.Add(new{session=session.GetProperty("session").GetString(),run=run.Key,mode,rejected,observed,rows});
        }
    }
}
File.WriteAllText(args[1],JsonSerializer.Serialize(output));
Console.WriteLine($"Replayed {output.Count} policy variants; no hardware operations.");
