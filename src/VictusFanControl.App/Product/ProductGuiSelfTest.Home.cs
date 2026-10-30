using System.Drawing.Imaging;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestHomeDashboard(Action<bool,string> require)
    {
        ProductHomeTelemetrySelfTest.Run(require);
        ProductFanAnimationSelfTest.Run(require);
        ProductFanReadoutsSelfTest.Run(require);
        var output=Path.GetFullPath("logs/product-gui-self-test");Directory.CreateDirectory(output);
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://home",fixture:runtime,fixtureProfiles:new ProductProfiles());
        form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Dock=DockStyle.None;canvas.Size=new(1672,941);
        var originalDraft=ProductProfilesStore.Serialize(form.Draft);
        var now=DateTimeOffset.UtcNow;
        for(int i=120;i>=0;i--)canvas.ObserveHomeDemand(ProductHomeTelemetrySelfTest.StateAt(now.AddSeconds(-i),50+Math.Sin(i*.09)*20),now.AddSeconds(-i));
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        canvas.Editing=ProductPowerProfile.Battery;
        var applied=canvas.State.AppliedAutomaticConfiguration;
        canvas.Profiles=canvas.Profiles with{Ac=canvas.Profiles.Ac with{Fan=canvas.Profiles.Ac.Fan with
        {UnifiedDemand=canvas.Profiles.Ac.Fan.UnifiedDemand! with{Curve=[new(0,10),new(90,10),new(100,50)]}}}};
        require(canvas.HomeCurveUsesApplied&&ReferenceEquals(canvas.HomeCurveConfiguration,applied)&&canvas.HomeCurveSource==ProductPowerProfile.Ac,
            "Home displayed an unsaved draft or the voluntarily edited other source as the applied curve.");
        void Render(string name)
        {
            canvas.Refresh();Application.DoEvents();using var bitmap=new Bitmap(canvas.Width,canvas.Height);
            canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
        }
        canvas.Page=ProductPage.Home;Render("home-dashboard-live");
        var names=canvas.Hits.Where(h=>h.Id.StartsWith("page-")).OrderBy(h=>h.Bounds.Y).Select(h=>h.Label).ToArray();
        require(names.SequenceEqual(new[]{"Inicio","Ventiladores","Rendimiento","Curvas","Perfiles","Monitorización","Protecciones","Configuración","Avanzado","Base externa","Curva Externa","HyperX Battery","Actualizaciones"}),
            "Requested navigation order was not rendered.");
        var phase=canvas.HomeFanPhases;var clock=ProductCanvas.AnimationClockMilliseconds;
        canvas.AdvanceHomeAnimation(clock+100,DateTimeOffset.UtcNow);var moving=canvas.HomeFanPhases;
        require(canvas.HomeAnimationRunning&&moving.Cpu>phase.Cpu&&moving.Gpu>phase.Gpu&&
            moving.Cpu-phase.Cpu>moving.Gpu-phase.Gpu,
            "Fan animation did not follow each independently measured RPM.");
        // Reproduce repeated UI publications with missing RPM, then expired/failed telemetry.
        var feedbackState=canvas.State;
        var heldNow=DateTimeOffset.UtcNow;
        var delayedFeedback=feedbackState.Snapshot! with{CpuFanRpm=null,GpuFanRpm=null};
        for(int i=2;i<=50;i++)
        {
            canvas.State=feedbackState with{Snapshot=delayedFeedback};
            canvas.AdvanceHomeAnimation(clock+i*100,heldNow);
        }
        require(canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds.Cpu>350&&
            Math.Abs(canvas.HomeFanAnimationSpeeds.Cpu-2*canvas.HomeFanAnimationSpeeds.Gpu)<1e-8,
            "RPM gaps restarted inertia or lost the independent measured speeds.");
        require(canvas.CpuFanReading(heldNow) is {Rpm:3000,Held:true}&&canvas.GpuFanReading(heldNow) is {Rpm:1500,Held:true},
            "Missing RPM replaced independent measured readouts with blanks or simulated RPM.");
        require(ProductCanvas.HomeFanRpmCaption(canvas.CpuFanReading(heldNow))=="Última RPM"&&
            ProductCanvas.HomeFanRpmCaption(canvas.GpuFanReading(heldNow))=="Última RPM",
            "Retained readings were presented as current RPM.");
        Render("home-dashboard-held-rpm");
        var beforeFailure=canvas.HomeFanAnimationSpeeds;
        canvas.State=feedbackState with{Runtime="Failed"};
        canvas.AdvanceHomeAnimation(clock+5100,heldNow.AddMinutes(20));
        require(canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds.Cpu>=beforeFailure.Cpu,
            "Expired/failed telemetry stopped retained RPM animation.");
        canvas.State=feedbackState;
        canvas.Page=ProductPage.Fans;canvas.Refresh();var fanPlot=canvas.RenderedCurveBounds;
        require(!canvas.HomeAnimationRunning,"Fan animation consumed resources on another page.");
        var stopped=canvas.HomeFanPhases;var pausedSpeeds=canvas.HomeFanAnimationSpeeds;
        canvas.AdvanceHomeAnimation(clock+20000,DateTimeOffset.UtcNow);
        require(canvas.HomeFanPhases==stopped&&canvas.HomeFanAnimationSpeeds==pausedSpeeds,
            "Invisible Home fans rotated or discarded inertia.");
        Render("fans-expanded-curve");canvas.Page=ProductPage.Curves;canvas.SimulationVisible=false;canvas.Refresh();
        require(canvas.RenderedCurveBounds.Size==fanPlot.Size&&fanPlot.Height>=350,"Fan curve did not match the editor plot dimensions.");
        canvas.Page=ProductPage.Home;
        require(canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds==pausedSpeeds&&canvas.HomeFanPhases==stopped,
            "Returning to Home reset position or momentum.");
        canvas.AdvanceHomeAnimation(ProductCanvas.AnimationClockMilliseconds,DateTimeOffset.UtcNow);
        require(Math.Abs(canvas.HomeFanAnimationSpeeds.Cpu-pausedSpeeds.Cpu)<1,
            "Returning to Home caught up hidden elapsed time.");
        canvas.Size=new(1040,660);Render("home-dashboard-minimum");
        canvas.Size=new(3344,1882);Render("home-dashboard-4k-scale");canvas.Size=new(1672,941);
        form.Hide();canvas.AdvanceHomeAnimation(clock+300,DateTimeOffset.UtcNow);
        require(!canvas.HomeAnimationRunning,"Hidden Home retained an animation timer.");
        form.Show();canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        form.WindowState=FormWindowState.Minimized;canvas.AdvanceHomeAnimation(clock+400,DateTimeOffset.UtcNow);
        require(!canvas.HomeAnimationRunning,"Minimized Home retained an animation timer.");
        var minimizedPhase=canvas.HomeFanPhases;var minimizedSpeed=canvas.HomeFanAnimationSpeeds;
        form.WindowState=FormWindowState.Normal;
        require(canvas.HomeAnimationRunning&&canvas.HomeFanPhases==minimizedPhase&&canvas.HomeFanAnimationSpeeds==minimizedSpeed,
            "Restore failed to restart the timer or reset inertia.");
        var stale=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow.AddMinutes(-1));canvas.State=stale;Render("home-dashboard-stale");
        require(canvas.HomeAnimationRunning,"Stale feedback stopped fans with retained measured RPM.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow,0);
        var rpmClock=ProductCanvas.AnimationClockMilliseconds;var rpmNow=DateTimeOffset.UtcNow;
        for(int i=1;i<=120;i++)canvas.AdvanceHomeAnimation(rpmClock+i*100,rpmNow);
        require(canvas.HomeAnimationRunning&&Math.Abs(canvas.HomeFanAnimationSpeeds.Cpu-360)<0.1&&
            Math.Abs(canvas.HomeFanAnimationSpeeds.Gpu-180)<0.1,
            "Zero demand overrode measured RPM or the one-second reference speed.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow,100,level:10) with{FanMode="Firmware"};
        require(canvas.HomeAnimationRunning,"Firmware fans stopped despite positive measured RPM.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow) with{FanMode="Manual"};
        require(canvas.HomeAnimationRunning,"Manual fans stopped despite positive measured RPM.");
        var stoppedAt=DateTimeOffset.UtcNow;
        var zero=ProductHomeTelemetrySelfTest.StateAt(stoppedAt);
        canvas.State=zero with{Snapshot=zero.Snapshot! with{CpuFanRpm=0,GpuFanRpm=1500}};
        var zeroClock=ProductCanvas.AnimationClockMilliseconds;var zeroNow=DateTimeOffset.UtcNow;
        for(int i=1;i<=200;i++)canvas.AdvanceHomeAnimation(zeroClock+i*100,zeroNow);
        require(canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds.Cpu==0&&canvas.HomeFanAnimationSpeeds.Gpu>179,
            "A measured CPU stop also stopped the running GPU fan.");
        var bothZero=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        canvas.State=bothZero with{Snapshot=bothZero.Snapshot! with{CpuFanRpm=0,GpuFanRpm=0}};
        for(int i=1;i<=200;i++)canvas.AdvanceHomeAnimation(zeroClock+20000+i*100,DateTimeOffset.UtcNow);
        require(!canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds==(0d,0d),
            "Measured zeros did not stop the timer after coast-down.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow,55,"Battery");Render("home-dashboard-battery");
        require(canvas.HomeCurveSource==ProductPowerProfile.Battery,"Home did not follow the real Battery source.");
        require(runtime.Commands==0&&ProductProfilesStore.Serialize(form.Draft)==originalDraft,"Dashboard rendering, animation or history wrote hardware or preferences.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Dashboard fixture exit stalled.");
        Console.WriteLine("Home dashboard: PASS (navigation, applied curve, independent measured RPM drives blades, gaps retain RPM and inertia, measured zero coast-down, firmware/manual operation, hidden/minimized freeze and resume, equal-size fan plot and Windows renders; zero hardware IO).");
    }
    private static void TestLiveAnimationSettings(Action<bool,string> require)
    {
        ProductFanAnimationSettingsSelfTest.Run(require);
        var root=Path.Combine(Path.GetTempPath(),"vfc-animation-gui-"+Guid.NewGuid().ToString("N"));
        var path=Path.Combine(root,"fan-animation.json");
        Directory.CreateDirectory(root);
        try
        {
            var runtime=new RecordingRuntime();
            using var form=new ProductForm("fixture://animation",fixture:runtime,fixtureProfiles:new ProductProfiles(),fanAnimationPath:path);
            form.Show();Application.DoEvents();var canvas=form.Canvas;
            var draft=ProductProfilesStore.Serialize(form.Draft);
            canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
            var clock=ProductCanvas.AnimationClockMilliseconds;var now=DateTimeOffset.UtcNow;
            for(int i=1;i<=120;i++)canvas.AdvanceHomeAnimation(clock+i*100,now);
            var speeds=canvas.HomeFanAnimationSpeeds;var phases=canvas.HomeFanPhases;
            void Save(string value)=>File.WriteAllText(path,"{\"segundosPorVueltaA3000Rpm\":"+value+"}");
            void WaitSetting(double value)=>require(SpinWait.SpinUntil(()=>{form.PresentationTick();return canvas.HomeAnimationSecondsPerRevolution==value;},5000),"Background visual setting did not arrive.");
            Save("2");WaitSetting(2);
            require(canvas.HomeAnimationSecondsPerRevolution==2&&canvas.HomeFanAnimationSpeeds==speeds&&canvas.HomeFanPhases==phases,
                "Live file reload did not reach both animations without resetting inertia.");
            for(int i=1;i<=200;i++)canvas.AdvanceHomeAnimation(clock+12000+i*100,now);
            require(Math.Abs(canvas.HomeFanAnimationSpeeds.Cpu-180)<0.1&&Math.Abs(canvas.HomeFanAnimationSpeeds.Gpu-90)<0.1,
                "Live setting did not scale each fan's own measured RPM.");
            File.WriteAllText(path,"{");form.PresentationTick();
            require(canvas.HomeAnimationSecondsPerRevolution==2,"Partial save interrupted the active setting.");
            canvas.Page=ProductPage.Fans;Save("0.5");form.PresentationTick();
            require(canvas.HomeAnimationSecondsPerRevolution==2&&!canvas.HomeAnimationRunning,"Another page reloaded/animated hidden Home.");
            canvas.Page=ProductPage.Home;WaitSetting(0.5);
            require(canvas.HomeAnimationSecondsPerRevolution==0.5&&canvas.HomeAnimationRunning,"Returning to Home did not pick up the pending edit.");
            form.WindowState=FormWindowState.Minimized;Save("3");form.PresentationTick();
            require(canvas.HomeAnimationSecondsPerRevolution==0.5&&!canvas.HomeAnimationRunning,"Minimized Home kept animation/settings work alive.");
            form.WindowState=FormWindowState.Normal;WaitSetting(3);
            require(canvas.HomeAnimationSecondsPerRevolution==3&&canvas.HomeAnimationRunning,"Restore failed to apply the pending edit.");
            form.Hide();Save("1");form.PresentationTick();
            require(canvas.HomeAnimationSecondsPerRevolution==3&&!canvas.HomeAnimationRunning,"Tray-hidden Home kept animation/settings work alive.");
            form.Show();WaitSetting(1);
            require(canvas.HomeAnimationSecondsPerRevolution==1&&canvas.CpuFanReading(now)?.Rpm==3000&&canvas.GpuFanReading(now)?.Rpm==1500&&
                runtime.Commands==0&&ProductProfilesStore.Serialize(form.Draft)==draft,"Visual edits changed RPM, hardware or profiles.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Live animation fixture exit stalled.");
        }
        finally {Directory.Delete(root,true);}
        Console.WriteLine("Live animation GUI: PASS (file reload, independent RPM scale, unchanged inertia/readouts, partial-save recovery, hidden/minimized/page pause and return; zero hardware/profile writes).");
    }

}
