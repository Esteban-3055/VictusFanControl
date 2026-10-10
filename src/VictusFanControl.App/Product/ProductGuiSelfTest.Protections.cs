using VictusFanControl.Product;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestProtections(Action<bool,string> require)
    {
        var preferences=new ProductProfiles{Protections=new(){CpuThermalHandoff=false,GpuThermalHandoff=false,PowerEnvelopeHandoff=false,ResumeAutomatic=false}};
        require(ProductProfilesStore.Copy(preferences).Protections==preferences.Protections,"Protection preferences did not persist.");
        using(var form=new ProductForm("fixture://protections",fixture:new RecordingRuntime(),fixtureProfiles:new()))
        {
            foreach(var id in new[]{"protections-cpu","protections-gpu","protections-power","protections-resume"})form.HandleCommand(id);
            require(form.Draft.Protections==preferences.Protections&&form.Dirty,"Protection switches did not edit independent preferences.");
            form.Canvas.Page=ProductPage.Protections;form.ClientSize=new(1040,660);form.PerformLayout();form.Canvas.Refresh();
            using var image=new Bitmap(form.Canvas.Width,form.Canvas.Height);form.Canvas.DrawToBitmap(image,new(0,0,image.Width,image.Height));
            var renderDirectory=Path.Combine("logs","product-gui-self-test");Directory.CreateDirectory(renderDirectory);
            require(image.Width==1040&&image.Height==660,"Protection fixture did not render the actual minimum resolution.");
            image.Save(Path.Combine(renderDirectory,"protections-minimum-layout.png"));
            require(form.Canvas.Hits.Any(h=>h.Id=="protections-cpu")&&form.Canvas.Hits.Any(h=>h.Id=="page-9"),"Protection page/navigation missing at minimum resolution.");
        }
        long clock=0;var review=new ProductAutomaticReview(()=>clock,ProductAutomaticReviewMode.Habitual);review.Configure(preferences.Protections);review.Start();
        var at=DateTimeOffset.UtcNow;
        TelemetrySnapshot Sample(int i,double cpu=100,double gpu=90)=>new(at.AddSeconds(i),"CPU",cpu,100,40,"GPU",gpu,90,30,2000,2000)
        {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",cpu)]};
        SafetyGateResult Safety(TelemetrySnapshot s)=>new(true,true,true,true,true,true,false,true,true,true,s.Timestamp,s.Timestamp,1,[]);
        for(int i=0;i<5;i++){clock=i*1000;var s=Sample(i);review.Observe(s,Safety(s));}
        require(review.RemainingCpuSpikeMilliseconds is null,"Disabled CPU handoff retained its deadline.");
        var invalid=Sample(5) with{CpuPackagePowerW=double.NaN};
        bool rejected=false;try{review.EnsureDispatchAllowed(invalid);}catch(InvalidOperationException){rejected=true;}
        require(rejected,"Disabling the review margin accepted invalid telemetry.");
        var bounded=new ProductAutomaticReview(()=>clock);bounded.Configure(preferences.Protections);bounded.Start();
        rejected=false;try{bounded.Observe(Sample(6),Safety(Sample(6)));}catch(InvalidOperationException){rejected=true;}
        require(rejected,"Product preferences bypassed an explicit physical review envelope.");

        var ready=new ProductRuntimeState{AutomaticAuthorized=true,Runtime="Healthy",FanAuthority="Firmware",LifecycleBlocked=true,
            Snapshot=Sample(0,55,45)};
        var resume=new ProductAutomaticResumption();resume.Observe(ready,0);
        require(!resume.TakeRequest(ready,new(),at,10000),"Never-active Firmware armed unattended Automatic.");
        resume.Observe(ready with{LifecycleBlocked=false,FanMode="Automatic"},0);resume.Observe(ready,1000);
        require(!resume.TakeRequest(ready,new(),at,10999),"Resumption bypassed its wait.");
        foreach(var blocked in new[]{ready with{FanAuthority="Unknown"},ready with{FanAuthority="Faulted"},ready with{Runtime="Degraded"},ready with{Snapshot=Sample(0,96,45)}})
            require(!resume.TakeRequest(blocked,new(),at,11000),"Resumption admitted uncertain/hot state.");
        require(!resume.TakeRequest(ready,new(){ResumeAutomatic=false},at,11000),"Disabled resumption armed control.");
        require(resume.TakeRequest(ready,new(),at,11000)&&!resume.TakeRequest(ready,new(),at,11001),"Resumption duplicated a request.");
        require(resume.TakeRequest(ready,new(),at,41000)&&resume.TakeRequest(ready,new(),at,101000)&&!resume.TakeRequest(ready,new(),at,201000),"Resumption exceeded three attempts/backoff.");
        resume.Cancel();resume.Observe(ready with{LifecycleBlocked=false,FanMode="Automatic"},202000);resume.Observe(ready,203000);
        require(!resume.TakeRequest(ready,new(),at,300000),"Late Automatic event overrode a user stop.");

        var root=Path.Combine(Path.GetTempPath(),"vfc-stop-contention-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Parallel.For(0,64,i=>WmiFanGuiGuardianHost.RequestStop(root,"STOP-"+i));
            using var held=new FileStream(Path.Combine(root,"stop.signal"),FileMode.Open,FileAccess.ReadWrite,FileShare.None);
            WmiFanGuiGuardianHost.RequestStop(root,"CLIENT_RELEASE");
            require(held.Length>0,"Stop contention removed or truncated the release fence.");
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("Protections: PASS (independent persisted handoffs, immutable review envelope, plausibility, bounded clean resumption, user stop and concurrent stop fence; zero hardware IO).");
    }

    private static void TestUnattendedAutomaticRetry(Action<bool,string> require)
    {
        var root=Path.Combine(Path.GetTempPath(),"vfc-unattended-retry-"+Guid.NewGuid().ToString("N"));
        var exitCode=Environment.ExitCode;
        try
        {
            foreach(var failure in new[]{false,true})
            {
                var old=new RecordingRuntime{DisposeGate=new(TaskCreationOptions.RunContinuationsAsynchronously),DisposeFailure=failure?"unresolved release":null};
                var next=new RecordingRuntime();int created=0;
                using var form=new ProductForm("fixture://resume",fixture:old,fixtureProfiles:new(),restartDirectory:root,
                    retryRuntimeFactory:p=>{created++;require(p.Ac.CpuPl1Watts==ProductProfiles.DefaultProfile(ProductPowerProfile.Ac).CpuPl1Watts,"Unattended retry applied an unsaved draft.");return Task.FromResult<IProductRuntime>(next);});
                form.Show();Application.DoEvents();
                var at=DateTimeOffset.UtcNow.AddSeconds(-2);
                TelemetrySnapshot Sample(int i)=>new(at.AddSeconds(i),"CPU",55,20,30,"GPU",45,15,20,2000,2000)
                {CpuExpectedPhysicalCoreCount=1,CpuCoreTemperatures=[new(0,0,"Performance",55)]};
                ProductRuntimeState Ready(int i)=>new(){Target="HP-8C40-9D0R1LA-F18",AutomaticAuthorized=true,Runtime="Healthy",Source="Ac",FanAuthority="Firmware",CanApplyPerformance=true,Snapshot=Sample(i)};
                old.Publish(Ready(2) with{FanMode="Automatic"});form.EditValue("pl1",38);
                old.Publish(Ready(2) with{LifecycleBlocked=true,LifecycleBlockReason="transient interruption"});
                form.TryAutomaticResumption(Environment.TickCount64+10001);
                PumpUntil(()=>old.Disposals==1,"Unattended retry did not release the old runtime.");require(created==0,"Unattended retry bypassed the old release.");
                old.DisposeGate.SetResult();PumpUntil(()=>!form.Canvas.Busy,"Unattended cleanup hung.");
                if(failure)require(created==0&&form.Canvas.State.LifecycleBlocked,"Failed release recreated an unattended runtime.");
                else
                {
                    require(created==1&&next.Commands==0&&form.Draft.Ac.CpuPl1Watts==38,"Unattended retry changed draft or activated before fresh observations.");
                    next.Publish(Ready(0));next.Publish(Ready(1));next.Publish(Ready(2));PumpUntil(()=>!form.Canvas.Busy,"Fresh unattended activation hung.");
                    require(next.Commands==1,"Clean resumption did not activate coupled Automatic exactly once.");
                }
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Unattended fixture could not exit.");
            }
            Console.WriteLine("Unattended Automatic resumption: PASS (old-runtime release fence, failed-release retention, frozen active preferences, fresh 3-sample gate and same GUI; zero hardware IO).");
        }
        finally{Environment.ExitCode=exitCode;if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
