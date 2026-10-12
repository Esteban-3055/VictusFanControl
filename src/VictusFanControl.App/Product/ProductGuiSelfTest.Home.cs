using System.Drawing.Imaging;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestHomeDashboard(Action<bool,string> require)
    {
        ProductHomeTelemetrySelfTest.Run(require);
        ProductFanAnimationSelfTest.Run(require);
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
        require(names.SequenceEqual(new[]{"Inicio","Ventiladores","Rendimiento","Curvas","Perfiles","Monitorización","Protecciones","Configuración","Avanzado","Actualizaciones"}),
            "Requested navigation order was not rendered.");
        var phase=canvas.HomeFanPhases;var clock=Environment.TickCount64;
        canvas.AdvanceHomeAnimation(clock+100,DateTimeOffset.UtcNow);var moving=canvas.HomeFanPhases;
        require(canvas.HomeAnimationRunning&&moving.Cpu>phase.Cpu&&moving.Gpu>phase.Gpu&&
            Math.Abs((moving.Cpu-phase.Cpu)-(moving.Gpu-phase.Gpu))<1e-8,
            "Fan animation did not use shared controller demand despite different RPM.");
        // Reproduce UI publications while RPM is unavailable and the accepted decision remains current.
        var feedbackState=canvas.State;
        var heldNow=DateTimeOffset.UtcNow;
        var delayedFeedback=feedbackState.Snapshot! with{CpuFanRpm=null,GpuFanRpm=null};
        for(int i=2;i<=50;i++)
        {
            canvas.State=feedbackState with{Snapshot=delayedFeedback};
            canvas.AdvanceHomeAnimation(clock+i*100,heldNow);
        }
        require(canvas.HomeAnimationRunning&&canvas.HomeFanAnimationSpeeds.Cpu>13&&
            canvas.HomeFanAnimationSpeeds.Cpu==canvas.HomeFanAnimationSpeeds.Gpu,
            "RPM updates restarted demand inertia or independently changed blade speed.");
        canvas.State=feedbackState;
        canvas.Page=ProductPage.Fans;canvas.Refresh();var fanPlot=canvas.RenderedCurveBounds;
        require(!canvas.HomeAnimationRunning,"Fan animation consumed resources on another page.");
        var stopped=canvas.HomeFanPhases;canvas.AdvanceHomeAnimation(clock+200,DateTimeOffset.UtcNow);
        require(canvas.HomeFanPhases==stopped,"Invisible Home fans continued rotating.");
        Render("fans-expanded-curve");canvas.Page=ProductPage.Curves;canvas.SimulationVisible=false;canvas.Refresh();
        require(canvas.RenderedCurveBounds.Size==fanPlot.Size&&fanPlot.Height>=350,"Fan curve did not match the editor plot dimensions.");
        canvas.Page=ProductPage.Home;canvas.Size=new(1040,660);Render("home-dashboard-minimum");
        canvas.Size=new(3344,1882);Render("home-dashboard-4k-scale");canvas.Size=new(1672,941);
        form.Hide();canvas.AdvanceHomeAnimation(clock+300,DateTimeOffset.UtcNow);
        require(!canvas.HomeAnimationRunning,"Hidden Home retained an animation timer.");
        form.Show();canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        form.WindowState=FormWindowState.Minimized;canvas.AdvanceHomeAnimation(clock+400,DateTimeOffset.UtcNow);
        require(!canvas.HomeAnimationRunning,"Minimized Home retained an animation timer.");
        form.WindowState=FormWindowState.Normal;
        var stale=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow.AddMinutes(-1));canvas.State=stale;Render("home-dashboard-stale");
        require(!canvas.HomeAnimationRunning,"Stale feedback showed moving fans.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        var coastClock=Environment.TickCount64;
        canvas.AdvanceHomeAnimation(coastClock+100,DateTimeOffset.UtcNow);
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow,0);
        var coastNow=DateTimeOffset.UtcNow;var coastPhase=canvas.HomeFanPhases;
        require(canvas.HomeAnimationRunning,"Zero demand skipped fan inertia.");
        canvas.AdvanceHomeAnimation(coastClock+200,coastNow);
        require(canvas.HomeFanPhases.Cpu>coastPhase.Cpu,"Zero-demand fan did not coast down.");
        for(int i=3;i<160;i++)canvas.AdvanceHomeAnimation(coastClock+i*100,coastNow);
        require(!canvas.HomeAnimationRunning,"Settled zero-demand fans retained an animation timer.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow) with{FanMode="Firmware"};
        require(!canvas.HomeAnimationRunning,"Firmware RPM fabricated automatic demand animation.");
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow,55,"Battery");Render("home-dashboard-battery");
        require(canvas.HomeCurveSource==ProductPowerProfile.Battery,"Home did not follow the real Battery source.");
        require(runtime.Commands==0&&ProductProfilesStore.Serialize(form.Draft)==originalDraft,"Dashboard rendering, animation or history wrote hardware or preferences.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Dashboard fixture exit stalled.");
        Console.WriteLine("Home dashboard: PASS (navigation, applied curve, real demand drives both blades, uninterrupted inertia across RPM updates, hidden/minimized/stale pause, finite zero-demand coast-down, equal-size fan plot and Windows renders; zero hardware IO).");
    }
}
