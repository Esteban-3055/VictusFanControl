using System.Collections;
using System.Text.Json;
using VictusFanControl.Product;

static class PureProbe
{
    static readonly List<object> Results=[];
    static void Set(object obj,string name,object? value)=>obj.GetType().GetProperty(name,R.Flags)!.SetValue(obj,value);
    static void Case(string name,Func<object> action)
    {try{var details=action();Results.Add(new{name,completed=true,details});Console.WriteLine(name+": "+JsonSerializer.Serialize(details));}
     catch(Exception e){Results.Add(new{name,completed=false,error=e.ToString()});Console.WriteLine(name+": HARNESS ERROR "+e);}}
    static void Main()
    {
        foreach(var scenario in new[]{"fresh","stale-thermal","unknown-source"})
        Case("I01-resumption-admission-"+scenario,()=>
        {
            var now=DateTimeOffset.UtcNow;var sample=Fixture.Sample();
            if(scenario=="stale-thermal")sample=sample with{CpuThermalSampledAtUtc=now.AddMinutes(-1),GpuThermalSampledAtUtc=now.AddMinutes(-1)};
            var state=R.New("ProductRuntimeState");
            Set(state,"Runtime","Healthy");Set(state,"Source",scenario=="unknown-source"?"Unknown":"Ac");
            Set(state,"FanMode","Firmware");Set(state,"FanAuthority","Firmware");Set(state,"LifecycleBlocked",true);Set(state,"AutomaticAuthorized",true);Set(state,"Snapshot",sample);
            Set(state,"Target","HP-8C40-9D0R1LA-F18");
            var episode=R.New("ProductRecoveryEpisode");var resume=R.New("ProductAutomaticResumption",episode);
            R.Call(resume,"ConfirmActivation");R.Call(resume,"Observe",state,0L);
            var requested=(bool)R.Call(resume,"TakeRequest",state,new ProductProtectionSettings(),DateTimeOffset.UtcNow,10001L)!;
            return new{Scenario=scenario,Source=R.Get(state,"Source"),SampleComplete=sample.IsComplete,
                AllControlSensorsFresh=sample.HasFreshControlSensorsAt(DateTimeOffset.UtcNow),FanTelemetryFresh=sample.IsFanTelemetryFreshAt(DateTimeOffset.UtcNow,3000),
                RequestedRuntimeReplacement=requested,BudgetAttempts=R.Get(episode,"Attempts"),
                Scope="Actual pure resumption policy; replacement and fresh-start validation were not executed. No HWND, device, native controller or network."};
        });
        foreach(var lowPoint in new[]{94,95})
        Case("I02-full-alert-rearm-at-"+lowPoint,()=>
        {
            var policy=R.New("HyperXNotificationPolicy");var cfg=R.New("HyperXSettings");var now=DateTimeOffset.UtcNow;
            object Snapshot(int percent){var snapshot=R.New("HyperXSnapshot");Set(snapshot,"ReceiverPresent",true);Set(snapshot,"SampledAt",now);Set(snapshot,"Percent",percent);return snapshot;}
            int Evaluate(object snapshot,object settings)=>((IEnumerable)R.Call(policy,"Evaluate",snapshot,settings,now)!).Cast<object>().Count();
            var initial=Evaluate(Snapshot(100),cfg);Evaluate(Snapshot(lowPoint),cfg);var repeat=Evaluate(Snapshot(100),cfg);
            return new{InitialFullNotices=initial,RecoveryPoint=lowPoint,ReturnToFullNotices=repeat,Scope="Recorded percentage inputs only; no HID reader or notices shown."};
        });
        Case("I03-cosmetic-setting-repeats-battery-notice",()=>
        {
            var policy=R.New("HyperXNotificationPolicy");var cfg=R.New("HyperXSettings");var changed=R.New("HyperXSettings");Set(changed,"LcdLowSeconds",7);
            var now=DateTimeOffset.UtcNow;var snapshot=R.New("HyperXSnapshot");Set(snapshot,"ReceiverPresent",true);Set(snapshot,"SampledAt",now);Set(snapshot,"Percent",100);
            int Evaluate(object settings)=>((IEnumerable)R.Call(policy,"Evaluate",snapshot,settings,now)!).Cast<object>().Count();
            var first=Evaluate(cfg);var same=Evaluate(cfg);var cosmetic=Evaluate(changed);
            return new{InitialNotices=first,IdenticalTickNotices=same,LcdDurationChangeNotices=cosmetic,AcquisitionUnchanged=true,ThresholdsUnchanged=true};
        });
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory,"pure-results"));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"pure-results","results.json"),JsonSerializer.Serialize(Results,new JsonSerializerOptions{WriteIndented=true}));
        Environment.ExitCode=Results.Any(x=>x.GetType().GetProperty("completed")!.GetValue(x) is false)?1:0;
    }
}
