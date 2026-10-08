using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.OemShadow;
using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

if (args is ["--self-test"]) { PlatformTests.Run(); PhysicalExperimentSelfTest.Run(); return; }
if (args is ["--physical-fixture", var fixtureOutput]) { PhysicalExperimentSelfTest.Run(fixtureOutput); return; }
if (args.Length != 4 || args[0] is not ("--replay" or "--baseline-only"))
    throw new ArgumentException("--replay|--baseline-only OEM-fixtures platform-fixtures new-output-directory");
bool baselineOnly = args[0] == "--baseline-only";
var json = new JsonSerializerOptions { PropertyNameCaseInsensitive=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
string root=args[1], configRoot=args[2], output=args[3];
if (Directory.Exists(output)) throw new IOException("Output must be a new directory.");
Directory.CreateDirectory(output);
var settings = new PlatformSettings();
using var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(configRoot,"recorded-fan-settings.json")));
var profiles = new (string Name, FanConfiguration Fan)[]
{
    ("default-ac",ProductProfiles.DefaultProfile(ProductPowerProfile.Ac).Fan),
    ("default-battery",ProductProfiles.DefaultProfile(ProductPowerProfile.Battery).Fan),
    ("recorded-ac",recorded.RootElement.GetProperty("ac").Deserialize<FanConfiguration>(json)!),
    ("recorded-battery",recorded.RootElement.GetProperty("battery").Deserialize<FanConfiguration>(json)!)
};
var summaries = new List<object>();
var sourceHashes = new Dictionary<string,string>();
foreach (var session in new[] {"v10","v11","live-20261008"})
{
    string fixture=Path.Combine(root,session+".jsonl.gz");
    sourceHashes[session]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture))).ToLowerInvariant();
    var frames=ReadLines(fixture).Select(s=>JsonSerializer.Deserialize<Frame>(s,json)!).ToArray();
    var corePath=Path.Combine(configRoot,session+".cores.jsonl.gz");
    var cores=File.Exists(corePath)?ReadLines(corePath).Select(s=>JsonSerializer.Deserialize<CoreRow>(s,json)!)
        .ToDictionary(r=>r.TimestampUtc,r=>r.Cores):new Dictionary<DateTimeOffset,CpuCoreTemperatureSample[]>();
    foreach (var (profileName,fan) in profiles)
    {
        var config=fan.BuildPolicy();
        string[] variants=baselineOnly?["baseline"]:["baseline","tz01","dtt3","both","both-retention","both-warmer-thresholds","both-colder-thresholds"];
        foreach (var variant in variants)
        {
            var parameters=settings;
            double tzShift=variant=="both-warmer-thresholds"?5:variant=="both-colder-thresholds"?-5:0;
            double dttShift=variant=="both-warmer-thresholds"?3:variant=="both-colder-thresholds"?-3:0;
            if (tzShift!=0)
                parameters=settings with {Tz01Curve=settings.Tz01Curve.Select(p=>p with{TemperatureC=p.TemperatureC+tzShift}).ToArray(),
                    Dtt3Curve=settings.Dtt3Curve.Select(p=>p with{TemperatureC=p.TemperatureC+dttShift}).ToArray()};
            bool useTz=variant!="baseline"&&variant!="dtt3", useDtt=variant!="baseline"&&variant!="tz01";
            var platform=new PlatformThermalDemand(parameters,useTz,useDtt);
            var baseline=new AdaptiveFanInertiaPolicy(config,fan.Tuning);
            var augmented=variant=="baseline"?null:new ResearchFanInertiaPolicy(config,fan.Tuning);
            double? rememberedSupplemental=null;
            int? lastShadowLevel=null;
            int? previousAugmented=null, previousBaseline=null;
            DateTimeOffset? previousAcceptedTime=null;
            int baseAccepted=0, accepted=0, unavailable=0, raises=0, rawLower=0, targetLower=0, changes=0, baseChanges=0, overrideMismatch=0;
            double extraSeconds=0, pairedSeconds=0, extraLevelSeconds=0, lowPowerExtraSeconds=0, highPowerExtraSeconds=0;
            int maximumExtra=0;
            var histogram=new SortedDictionary<int,int>();
            var trace=new StringBuilder();
            DateTimeOffset? last=null;
            foreach (var f in frames)
            {
                if (last.HasValue&&f.TimestampUtc<=last) throw new InvalidDataException("Non-monotonic fixture.");
                last=f.TimestampUtc;
                var observation=platform.Evaluate(f);
                var input=BuildInput(f,fan.Tuning,cores,json);
                AdaptiveFanInertiaDecision? b=null,a=null,shadow=null;
                double? applied=null;
                if (input is not null)
                {
                    b=baseline.Evaluate(input);
                    if (b.Accepted) baseAccepted++;
                    if (b.Accepted)
                    {
                        if (observation.Available)
                        {
                            applied=observation.DemandLevel;
                            if (variant=="both-retention"&&applied.HasValue)
                                applied=Math.Min(applied.Value,lastShadowLevel??b.EqualFanLevel!.Value);
                            rememberedSupplemental=applied;
                        }
                        // Keep the parallel observer's history across source outages.
                        // The last qualified demand is explicit MEMORY, not a fresh
                        // sensor value; no target is proposed while sources are unavailable.
                        shadow=variant=="baseline"?b:EvaluateAugmented(augmented!,input,rememberedSupplemental);
                        lastShadowLevel=shadow.EqualFanLevel;
                        if (observation.Available)a=shadow;
                    }
                    else { augmented?.Reset();rememberedSupplemental=null;lastShadowLevel=null; }
                }
                else { augmented?.Reset();rememberedSupplemental=null;lastShadowLevel=null; }
                if (a is {Accepted:true} && b is {Accepted:true})
                {
                    accepted++;
                    int delta=a.EqualFanLevel!.Value-b.EqualFanLevel!.Value;
                    if (delta>0) raises++;
                    if (delta<0) targetLower++;
                    if (a.RawDemandLevel<b.RawDemandLevel) rawLower++;
                    if (a.ThermalOverride!=b.ThermalOverride) overrideMismatch++;
                    maximumExtra=Math.Max(maximumExtra,delta);
                    histogram[a.EqualFanLevel.Value]=histogram.GetValueOrDefault(a.EqualFanLevel.Value)+1;
                    double interval=previousAcceptedTime.HasValue?(f.TimestampUtc-previousAcceptedTime.Value).TotalSeconds:0;
                    if (interval>0&&interval<=config.MaximumSampleGap.TotalSeconds)
                    {
                        pairedSeconds+=interval;
                        if (delta>0)
                        {
                            extraSeconds+=interval;extraLevelSeconds+=delta*interval;
                            // Recorded watts are row context, not independently timestamped sources.
                            if (f.CpuPowerW<15&&f.GpuPowerW<10) lowPowerExtraSeconds+=interval;
                            else highPowerExtraSeconds+=interval;
                        }
                        if (previousAugmented!=a.EqualFanLevel) changes++;
                        if (previousBaseline!=b.EqualFanLevel) baseChanges++;
                    }
                    previousAcceptedTime=f.TimestampUtc;
                    previousAugmented=a.EqualFanLevel;previousBaseline=b.EqualFanLevel;
                }
                else
                {
                    unavailable++;previousAugmented=null;previousBaseline=null;previousAcceptedTime=null;
                }
                trace.AppendLine(JsonSerializer.Serialize(new {timestamp=f.TimestampUtc, platform=observation, appliedSupplementalLevel=applied,
                    baseline=b,candidate=a, inputAvailable=input is not null,
                    internalShadowDecision=shadow,rememberedSupplementalLevel=rememberedSupplemental,
                    proposedDisposition=a is {Accepted:true}?"SimulatedTarget":"NoTargetHandoffRequired",
                    lowPowerContext=f.CpuPowerW<15&&f.GpuPowerW<10},json));
            }
            string path=Path.Combine(output,$"{session}-{profileName}-{variant}.jsonl");
            File.WriteAllText(path+".tmp",trace.ToString());
            File.Move(path+".tmp",path);
            summaries.Add(new {session,profile=profileName,variant,frames=frames.Length,baseAccepted,accepted,unavailable,
                raisedTargets=raises,rawDemandBelowBaseline=rawLower,targetsBelowBaseline=targetLower,overrideMismatch,
                maximumExtraLevels=maximumExtra,pairedSeconds,extraSeconds,extraLevelSeconds,lowPowerExtraSeconds,highPowerExtraSeconds,
                candidateChanges=changes,baselineChanges=baseChanges,levelHistogram=histogram,
                missingCpuLoad=frames.Count(f=>!f.CpuLoadPercent.HasValue),missingCoreRows=frames.Count(f=>!cores.ContainsKey(f.TimestampUtc))});
        }
    }
}
File.WriteAllText(Path.Combine(output,"summary.json"),JsonSerializer.Serialize(new
{
    schemaVersion=1, purpose="offline-product-policy-experiment",hardwareWrites=false, productionEnabled=false,
    disclosure="Recorded OEM temperatures are held fixed; this is not a thermal plant simulation or a replay of hardware admission/ACK scheduling. No RPM/noise/safety outcome is inferred.",
    parameters=settings,profiles=profiles.Select(p=>new {name=p.Name,fan=p.Fan}),sourceHashes,
    archivedCoreHashes=new[]{"v10","v11"}.ToDictionary(s=>s,s=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(configRoot,s+".cores.jsonl.gz")))).ToLowerInvariant()),
    recordedSettingsHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(configRoot,"recorded-fan-settings.json")))).ToLowerInvariant(),runs=summaries
},new JsonSerializerOptions(json){WriteIndented=true})+Environment.NewLine);
Console.WriteLine($"Platform policy replay: {summaries.Count} variants; no hardware IO.");

static AdaptiveFanInertiaDecision EvaluateAugmented(ResearchFanInertiaPolicy policy,AdaptiveFanPolicyInput input,double? extra)
    => policy.Evaluate(input,extra);
static IEnumerable<string> ReadLines(string path)
{
    using var file=File.OpenRead(path);using var zipped=new GZipStream(file,CompressionMode.Decompress);using var reader=new StreamReader(zipped);
    while(reader.ReadLine() is { } line)yield return line;
}
static AdaptiveFanPolicyInput? BuildInput(Frame f,AdaptiveFanTuning tuning,
    Dictionary<DateTimeOffset,CpuCoreTemperatureSample[]> cores,JsonSerializerOptions _)
{
    if (!new[]{f.CpuPackage,f.CpuCoreMax,f.Gpu}.All(s=>s.Fresh(f.TimestampUtc,3000)) ||
        f.CpuPowerW is null || f.GpuPowerW is null || f.CpuLoadPercent is null || f.GpuLoadPercent is null)
        return null;
    cores.TryGetValue(f.TimestampUtc,out var c);
    var snapshot=new TelemetrySnapshot(f.TimestampUtc,null,f.CpuPackage.Value,f.CpuPowerW,f.CpuLoadPercent,
        null,f.Gpu.Value,f.GpuPowerW,f.GpuLoadPercent,null,null)
    {CpuCoreTemperatures=c??[],CpuExpectedPhysicalCoreCount=14};
    var cpu=CpuDemandTemperature.Select(snapshot,tuning.CpuTemperatureSource,tuning.HottestPerformanceCoreCount);
    if (!cpu.HasValue)return null;
    return new(f.TimestampUtc,cpu.Value,f.CpuPowerW.Value,f.CpuLoadPercent.Value,f.Gpu.Value!.Value,f.GpuPowerW.Value,f.GpuLoadPercent.Value)
    {CpuRawControlTemperatureC=Math.Max(f.CpuPackage.Value!.Value,f.CpuCoreMax.Value!.Value)};
}
public sealed record CoreRow(DateTimeOffset TimestampUtc,CpuCoreTemperatureSample[] Cores);
