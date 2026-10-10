using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestStartupAutomatic(Action<bool,string> require)
    {
        var at=DateTimeOffset.UtcNow.AddSeconds(-2);
        ProductRuntimeState State(int i,string source="Ac")=>new(){Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source=source,
            AutomaticAuthorized=true,CanApplyPerformance=true,
            Snapshot=new TelemetrySnapshot(at.AddSeconds(i),"CPU",50,20,50,"GPU",45,10,30,1200,1200){CpuExpectedPhysicalCoreCount=3,CpuCoreTemperatures=new[]{new CpuCoreTemperatureSample(0,0,"Performance",50),new CpuCoreTemperatureSample(1,2,"Performance",49),new CpuCoreTemperatureSample(2,4,"Performance",48)}}};
        var gate=new ProductStartupAutomatic(0);
        require(!gate.Observe(State(0),at,0)&&!gate.Observe(State(0),at,1000),"Startup counted a repeated sample.");
        require(!gate.Observe(State(1),at.AddSeconds(1),1000)&&gate.Observe(State(2),at.AddSeconds(2),2000),"Three fresh startup acquisitions did not admit.");
        require(!gate.Observe(State(3),at.AddSeconds(3),3000),"Startup activation repeated.");
        foreach(var blocked in new[]{State(0) with{LifecycleBlocked=true},State(0) with{Target="Unsupported"},
            State(0) with{PerformanceRecovery=new(true,"fixture pending")},State(0) with{PerformanceProcessPresent=true},State(0) with{FanMode="Manual"}})
        {gate=new(0);require(!gate.Observe(blocked,at,0)&&gate.Finished&&!gate.Observe(State(2),at.AddSeconds(2),2000),"Startup retried a blocking failure.");}
        gate=new(0);require(!gate.Observe(State(0),at.AddSeconds(30),30000)&&!gate.Finished,"Startup request expired instead of waiting for sensors.");
        require(!gate.Observe(State(0),at,31000)&&!gate.Observe(State(1),at.AddSeconds(1),32000)&&gate.Observe(State(2),at.AddSeconds(2),33000),"Startup did not admit sensors after its former timeout.");
        gate=new(0);gate.Cancel("fixture");require(!gate.Observe(State(0),at,0),"User cancellation rearmed startup.");
        gate=new(0);require(!gate.Observe(State(0),at.AddSeconds(4),0)&&!gate.Observe(State(1),at,1000),"Stale/future startup data admitted.");
        gate=new(0);gate.Observe(State(0),at,0);gate.Observe(State(1,"Battery"),at.AddSeconds(1),1000);
        require(!gate.Observe(State(2,"Battery"),at.AddSeconds(2),2000)&&gate.Observe(State(3,"Battery"),at.AddSeconds(3),3000),"Startup failed to requalify a source change.");
        var runtime=new RecordingRuntime();
        using(var form=new ProductForm("fixture://startup",fixture:runtime,fixtureProfiles:new ProductProfiles{ActivateAutomaticOnStart=true},enableStartupAutomaticInFixture:true))
        {
            form.Show();Application.DoEvents();at=DateTimeOffset.UtcNow.AddSeconds(-2);
            runtime.Publish(State(0));runtime.Publish(State(1));runtime.Publish(State(2));Application.DoEvents();
            require(runtime.Commands==1,"Startup did not use the normal runtime command exactly once.");
            runtime.Publish(State(2));form.PresentationTick();require(runtime.Commands==1,"UI ticks repeated startup hardware activation.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Startup fixture exit stalled.");
        }
        var preferences=new ProductProfiles{ActivateAutomaticOnStart=true};
        foreach(var trayIndex in new[]{1,2})
        {
            var stopped=new RecordingRuntime();
            using var form=new ProductForm("fixture://tray-stop",fixture:stopped,fixtureProfiles:preferences,enableStartupAutomaticInFixture:true);
            form.Show();Application.DoEvents();at=DateTimeOffset.UtcNow.AddSeconds(-2);stopped.Publish(State(0));
            var tray=(NotifyIcon)typeof(ProductForm).GetField("_tray",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
            var menu=tray.ContextMenuStrip!;menu.Show(form,new Point(20,20));Application.DoEvents();
            ((ToolStripMenuItem)menu.Items[trayIndex]).PerformClick();menu.Close();
            PumpUntil(()=>!form.Canvas.Busy,"Tray release did not drain.");
            require(stopped.Commands==1,$"Tray action {trayIndex} did not dispatch its explicit release: commands={stopped.Commands}.");
            stopped.Publish(State(1));stopped.Publish(State(2));form.PresentationTick();
            require(stopped.Commands==1,$"Tray action {trayIndex} left startup Automatic armed: commands={stopped.Commands}.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Tray-stop fixture exit stalled.");
        }
        var receipt=ProductSessionRestart.Capture(preferences,preferences,true,false,ProductPage.Settings,ProductPowerProfile.Ac) with{ReleaseCompleted=true};
        var restarted=new RecordingRuntime();
        using(var form=new ProductForm("fixture://restart",fixture:restarted,fixtureProfiles:preferences,restartState:receipt,enableStartupAutomaticInFixture:true))
        {
            form.Show();Application.DoEvents();at=DateTimeOffset.UtcNow.AddSeconds(-2);
            restarted.Publish(State(0));restarted.Publish(State(1));restarted.Publish(State(2));form.PresentationTick();
            require(restarted.Commands==0,"A session restart automatically reacquired control from a persisted preference.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Restart startup fixture exit stalled.");
        }
        var suspended=new RecordingRuntime();
        using(var form=new ProductForm("fixture://suspend",fixture:suspended,fixtureProfiles:preferences,enableStartupAutomaticInFixture:true))
        {
            form.Show();Application.DoEvents();form.HandlePowerEventAsync(4).GetAwaiter().GetResult();
            at=DateTimeOffset.UtcNow.AddSeconds(-2);suspended.Publish(State(0));suspended.Publish(State(1));suspended.Publish(State(2));
            require(suspended.Commands==0,"Suspension did not cancel a pending startup request.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Suspended startup fixture exit stalled.");
        }
        var legacy=System.Text.Json.Nodes.JsonNode.Parse(ProductProfilesStore.Serialize(new ProductProfiles{Ac=ProductProfiles.LegacyDefaultProfile(ProductPowerProfile.Ac)}))!;
        legacy.AsObject().Remove("defaultCurveRevision");legacy.AsObject().Remove("activateAutomaticOnStart");
        var migrated=ProductProfilesStore.Parse(legacy.ToJsonString());
        require(!migrated.ActivateAutomaticOnStart&&FanConfigurationStore.Serialize(migrated.Ac.Fan)==FanConfigurationStore.Serialize(new ProductProfiles().Ac.Fan),"Default upgrade lost the new curve or enabled startup implicitly.");
        var fan=migrated.Ac.Fan;var custom=migrated with{DefaultCurveRevision=1,Ac=migrated.Ac with{Fan=fan with{Tuning=fan.Tuning with{FallTimeConstantSeconds=29}}},ActivateAutomaticOnStart=true};
        require(ProductProfilesStore.Serialize(ProductProfilesStore.Parse(ProductProfilesStore.Serialize(custom)))==ProductProfilesStore.Serialize(custom with{DefaultCurveRevision=2}),"Upgrade changed custom fan tuning or persisted startup preference.");
        var directory=Path.Combine(Path.GetTempPath(),"vfc-v1-migrate-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var path=Path.Combine(directory,"profiles.json");var bytes=System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString());File.WriteAllBytes(path,bytes);
            ProductProfilesStore.Save(migrated,path);
            var backup=Directory.GetFiles(directory,"*.pre-v1-backup-*.json");
            require(backup.Length==1&&File.ReadAllBytes(backup[0]).SequenceEqual(bytes),"The default curve migration lost the exact previous file.");
            ProductProfilesStore.Save(migrated,path);require(Directory.GetFiles(directory,"*.pre-v1-backup-*.json").Length==1,"Repeated save duplicated the previous-default backup.");
        } finally {Directory.Delete(directory,true);}
        var clock=0L;var guard=new ProductAutomaticReview(()=>clock,ProductAutomaticReviewMode.Habitual);guard.Start();clock=2700001;
        require(!guard.Expired&&guard.RemainingSeconds is null,"Habitual Automatic retained a review duration limit.");
        Console.WriteLine("PASS Product v1 startup: one attempt, distinct/fresh acquisitions, source change, cancellation, timeout, custom migration; no hardware IO.");
    }
}
