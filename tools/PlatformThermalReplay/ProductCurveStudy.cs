using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.PlatformThermalReplay;

internal static class ProductCurveStudy
{
    public static void Run(string tracePath,string output)
    {
        if(Directory.Exists(output))throw new IOException("Output must be a new directory.");
        var json=new JsonSerializerOptions{PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
        json.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var records=File.ReadLines(tracePath).Select(s=>JsonDocument.Parse(s)).ToArray();
        try
        {
            var fan=records.Single(r=>r.RootElement.GetProperty("kind").GetString()=="session").RootElement.GetProperty("data").GetProperty("fan").Deserialize<FanConfiguration>(json)!;
            var profiles=new ProductProfiles{Ac=new ProductProfiles().Ac with{Fan=fan}};
            var candidate=ProductQuietCandidate.Stage(profiles).Ac.Fan;
            var baseline=new AdaptiveFanInertiaPolicy(fan.BuildPolicy(),fan.Tuning);
            var quiet=new AdaptiveFanInertiaPolicy(candidate.BuildPolicy(),candidate.Tuning);
            var retained=new AdaptiveFanInertiaPolicy(candidate.BuildPolicy(),candidate.Tuning);
            var retention=new ProductPlatformRetention();retention.Configure(true);
            Source tz=new(),dtt=new();
            int? previousRetained=null,previousBaseline=null,previousQuiet=null;
            int rows=0,baselineChanges=0,quietChanges=0,retainedChanges=0,lowerQuiet=0,lowerRetained=0,raisedByRetention=0;
            double seconds=0,baselineLevelSeconds=0,quietLevelSeconds=0,retainedLevelSeconds=0;
            DateTimeOffset? previous=null;
            var csv=new StringBuilder("timestamp,baseline,quiet,quiet_retention,baseline_raw,quiet_raw,retained_raw,retention_floor,thermal\n");
            static string N(double? value)=>value?.ToString("R",CultureInfo.InvariantCulture)??"";
            foreach(var record in records)
            {
                var root=record.RootElement;var kind=root.GetProperty("kind").GetString();var data=root.GetProperty("data");
                if(kind=="telemetry")
                {
                    var snapshot=data.GetProperty("snapshot").Deserialize<TelemetrySnapshot>(json)!;
                    var history=data.GetProperty("sourceHistory");
                    var updates=history.GetProperty("tz01").Deserialize<Source[]>(json)!.Select(s=>(Tz:true,Sample:s))
                        .Concat(history.GetProperty("dtt3").Deserialize<Source[]>(json)!.Select(s=>(Tz:false,Sample:s)))
                        .Where(s=>s.Sample.SampledAtUtc is not null).OrderBy(s=>s.Sample.SampledAtUtc);
                    foreach(var update in updates)
                    {
                        var last=update.Tz?tz:dtt;
                        if(last.SampledAtUtc is {} at && update.Sample.SampledAtUtc<=at)continue;
                        if(update.Tz)tz=update.Sample;else dtt=update.Sample;
                        retention.SetSources(tz,dtt);
                    }
                    retention.ObserveTelemetry(snapshot,data.GetProperty("source").GetString()!,data.GetProperty("stage").GetProperty("custom").GetBoolean());
                }
                else if(kind=="actuation-observation")
                {
                    var input=data.Deserialize<AdaptiveFanPolicyInput>(json)!;
                    if(!baseline.ObserveDuringActuation(input,out _) || !quiet.ObserveDuringActuation(input,out _) || !retained.ObserveDuringActuation(input,out _))
                        throw new InvalidDataException("Actuation observation refused.");
                }
                else if(kind=="decision")
                {
                    var input=data.GetProperty("input").Deserialize<AdaptiveFanPolicyInput>(json)!;
                    var a=baseline.Evaluate(input); var b=quiet.Evaluate(input);
                    var c=retained.EvaluateWithSupplement(input,raw=>retention.GetSupplement(input,raw,previousRetained),retentionOnly:true);
                    var archived=data.GetProperty("baseline").Deserialize<AdaptiveFanInertiaDecision>(json)!;
                    if(a with{Detail=archived.Detail}!=archived)throw new InvalidDataException("Replayed baseline differs at "+input.Timestamp.ToString("O")+"; new="+JsonSerializer.Serialize(a,json)+"; archived="+JsonSerializer.Serialize(archived,json));
                    if(!a.Accepted||!b.Accepted||!c.Accepted || a.ThermalOverride!=b.ThermalOverride || a.ThermalOverride!=c.ThermalOverride)
                        throw new InvalidDataException("Candidate changed admission or raw thermal response.");
                    if(previous is {} last)
                    {
                        var dt=(input.Timestamp-last).TotalSeconds;
                        if(dt<=0||dt>3)throw new InvalidDataException("Decision gap cannot be integrated as continuous evidence.");
                        seconds+=dt; baselineLevelSeconds+=previousBaseline!.Value*dt;quietLevelSeconds+=previousQuiet!.Value*dt;retainedLevelSeconds+=previousRetained!.Value*dt;
                        baselineChanges+=a.EqualFanLevel!=previousBaseline?1:0;quietChanges+=b.EqualFanLevel!=previousQuiet?1:0;retainedChanges+=c.EqualFanLevel!=previousRetained?1:0;
                    }
                    lowerQuiet+=b.EqualFanLevel<a.EqualFanLevel?1:0;lowerRetained+=c.EqualFanLevel<a.EqualFanLevel?1:0;raisedByRetention+=c.EqualFanLevel>b.EqualFanLevel?1:0;
                    previous=input.Timestamp;previousBaseline=a.EqualFanLevel;previousQuiet=b.EqualFanLevel;previousRetained=c.EqualFanLevel;rows++;
                    csv.AppendLine($"{input.Timestamp:O},{a.EqualFanLevel},{b.EqualFanLevel},{c.EqualFanLevel},{N(a.RawDemandLevel)},{N(b.RawDemandLevel)},{N(c.RawDemandLevel)},{N(retention.State.SupplementalDemand)},{a.ThermalOverride}");
                }
            }
            Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"decisions.csv"),csv.ToString());
            var summary=new {kind="VictusFanControl.ProductQuietCurveReplay",sourceSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tracePath))).ToLowerInvariant(),
                rows,seconds,baselineUnchanged=true,baselineMeanLevel=baselineLevelSeconds/seconds,quietMeanLevel=quietLevelSeconds/seconds,retainedMeanLevel=retainedLevelSeconds/seconds,
                baselineChanges,quietChanges,retainedChanges,lowerQuiet,lowerRetained,raisedByRetention,candidate,
                disclosure="Same recorded CPU/GPU temperatures and powers for all candidates. Targets are simulated with immediate acknowledgements; levels*100 are nominal RPM, not noise/dBA. No counterfactual cooling, acoustic improvement or physical qualification is inferred."};
            File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(summary,json));
            Console.WriteLine($"Product curve replay: PASS ({rows} decisions; archived baseline exact; {seconds:0.0} s). Output: {output}");
        }
        finally {foreach(var record in records)record.Dispose();}
    }
}
