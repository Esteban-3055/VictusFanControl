using System.Text.Json;

namespace VictusFanControl.Control.Adaptive;

public static class AdaptiveCurveProfilesSelfTest
{
    public static int Run(TextWriter output)
    {
        var failures=0;
        void Check(string name,Func<bool> test)
        {
            try { if(!test())throw new InvalidOperationException("assertion");output.WriteLine($"PASS: curve profiles — {name}"); }
            catch(Exception e){failures++;output.WriteLine($"FAIL: curve profiles — {name}: {e.Message}");}
        }
        bool Reject(Action a){try{a();return false;}catch(Exception e) when(e is InvalidDataException or ArgumentException or IOException or JsonException){return true;}}
        var presets=AdaptiveCurveProfiles.Presets();var balanced=presets[1];
        Check("Equilibrado exactly preserves Candidate V1",()=>
            AdaptiveCurveProfiles.Serialize(balanced)==AdaptiveCurveProfiles.Serialize(AdaptiveCurveProfiles.Create("equilibrado","Equilibrado",Hp8C40AdaptiveCandidateV1.Create())));
        Check("three independent presets and monotone envelopes",()=>
        {
            foreach(var p in presets)_=AdaptiveCurveProfiles.Validate(p);
            var silence=AdaptiveCurveProfiles.Validate(presets[0]);var performance=AdaptiveCurveProfiles.Validate(presets[2]);
            return presets.Select(p=>p.Name).SequenceEqual(new[]{"Silencio","Equilibrado","Performance"}) &&
                AdaptiveCurveProfiles.Interpolate(silence.CpuLoadCurve,75)==18 && AdaptiveCurveProfiles.Interpolate(performance.CpuLoadCurve,75)==26;
        });
        Check("linear editor and policy interpolation agree",()=>
        {
            var points=new[]{new AdaptiveFanCurvePoint(40,10),new AdaptiveFanCurvePoint(60,30)};
            return AdaptiveCurveProfiles.Interpolate(points,50)==20 && AdaptiveCurveProfiles.Interpolate(points,0)==10 && AdaptiveCurveProfiles.Interpolate(points,100)==30;
        });
        Check("draft edits cannot mutate preset or prior snapshots",()=>
        {
            var p=AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10),new(100,50)]);
            return AdaptiveCurveProfiles.Validate(balanced).CpuLoadCurve[^1].Level==28 && AdaptiveCurveProfiles.Validate(p).CpuLoadCurve[^1].Level==50;
        });
        Check("duplicates, descending, fractions and bounds rejected",()=>
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10),new(0,20)])) &&
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,20),new(100,10)])) &&
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10),new(101,20)])) &&
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10),new(90.5,20)])) &&
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10),new(100,51)])) &&
            Reject(()=>AdaptiveCurveProfiles.WithCurve(balanced,AdaptiveCurveAxis.CpuLoad,[new(0,10)])));
        Check("authorization injection and incorrect target rejected",()=>
        {
            var json=AdaptiveCurveProfiles.Serialize(balanced);
            return Reject(()=>AdaptiveCurveProfiles.Parse(json.Replace("\"authorizedForProduction\": false","\"authorizedForProduction\": true"))) &&
                Reject(()=>AdaptiveCurveProfiles.Parse(json.Replace("HP-8C40-9D0R1LA-F18","HP-88F8"))) &&
                Reject(()=>AdaptiveCurveProfiles.Parse(json.Insert(1,"\"automaticExecutionAuthorized\":true,"))) &&
                Reject(()=>AdaptiveCurveProfiles.Parse(json.Replace("shadow-only","production")));
        });
        Check("unsafe ID and smoothing changes rejected",()=>
            Reject(()=>AdaptiveCurveProfiles.Validate(balanced with{Id="../outside"})) &&
            Reject(()=>AdaptiveCurveProfiles.Validate(balanced with{Config=balanced.Config with{MaximumUpStepPerSample=50}})));
        var dir=Path.Combine(Path.GetTempPath(),"victus-profile-test-"+Guid.NewGuid().ToString("N"));
        try
        {
            var store=new AdaptiveCurveProfileStore(dir);var custom=AdaptiveCurveProfiles.Copy(balanced) with{Id=Guid.NewGuid().ToString("N"),Name="Mi curva"};
            Check("atomic save, roundtrip and overwrite",()=>
            {
                store.Save(custom);store.Save(custom with{Name="Mi curva 2"});var loaded=store.LoadCustom(out var rejected);
                return rejected==0 && loaded.Count==1 && loaded[0].Name=="Mi curva 2" && !Directory.EnumerateFiles(dir,"*.tmp").Any();
            });
            Check("corruption fallback preserves valid profiles and builtins",()=>
            {
                File.WriteAllText(Path.Combine(dir,Guid.NewGuid().ToString("N")+".json"),"{");
                var invalid=custom with{Id=Guid.NewGuid().ToString("N")};
                var invalidJson=AdaptiveCurveProfiles.Serialize(invalid).Replace("\"authorizedForProduction\": false","\"authorizedForProduction\": true");
                File.WriteAllText(Path.Combine(dir,invalid.Id+".json"),invalidJson);
                var loaded=store.LoadCustom(out var rejected);return rejected==2 && loaded.Count==1 && AdaptiveCurveProfiles.Presets().Count==3;
            });
            Check("custom delete, builtin and traversal protection",()=>
            {
                var guarded=Reject(()=>store.Save(balanced)) && Reject(()=>store.Delete("../outside"));store.Delete(custom.Id);
                return guarded && store.LoadCustom(out _).Count==0;
            });
        }
        finally { if(Directory.Exists(dir))Directory.Delete(dir,true); }
        output.WriteLine(failures==0 ? "Adaptive curve profiles self-test: PASS (10 groups)" : $"Adaptive curve profiles self-test: FAIL ({failures})");
        return failures==0 ? 0 : 36;
    }
}
