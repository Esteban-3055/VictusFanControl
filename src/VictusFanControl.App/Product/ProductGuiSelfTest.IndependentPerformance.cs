using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunIndependentPerformanceChecks()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            ProductAutomaticActivationSelfTest.Run(Require);TestIndependentPerformanceControls(Require);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void TestIndependentPerformanceControls(Action<bool,string> require)
    {
        var profiles=new ProductProfiles();var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://independent-performance",fixture:runtime,fixtureProfiles:profiles);
        form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Page=ProductPage.Performance;
        var ready=runtime.State with{Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",FanMode="Automatic",FanAuthority="Custom",
            AutomaticSessionId="retained-fan-episode",PerformanceSupported=true,CanApplyPerformance=true,PerformanceProcessPresent=true,PerformanceActive=true,
            CpuState="Active",GpuState="ActiveUnverified",AppliedPerformance=profiles.PerformanceConfiguration(),AppliedPerformanceSource="Ac",
            Snapshot=Snapshot(DateTimeOffset.UtcNow,45,40,10,5)};
        runtime.Publish(ready);int expectedCommands=0;
        foreach(var selection in new[]{(false,true),(true,false),(false,false),(true,true)})
        {
            if(form.Draft.CpuEnabled!=selection.Item1)form.HandleCommand("cpu-toggle");
            if(form.Draft.GpuEnabled!=selection.Item2)form.HandleCommand("gpu-toggle");
            canvas.Refresh();
            require(runtime.Commands==expectedCommands&&canvas.CanApplyPerformance,"Domain selection wrote hardware before Apply or could not be applied.");
            form.HandleCommand("performance-apply");PumpUntil(()=>!canvas.Busy,"Independent Apply did not finish.");expectedCommands++;
            var expected=form.Draft.PerformanceConfiguration();
            require(runtime.Commands==expectedCommands&&runtime.LastPerformance==expected&&expected.CpuEnabled==selection.Item1&&expected.GpuEnabled==selection.Item2,
                "Independent Apply lost the requested domain selection.");
            runtime.Publish(ready with{AppliedPerformance=expected,CpuState=selection.Item1?"Active":"Disabled",GpuState=selection.Item2?"ActiveUnverified":"Disabled",
                PerformanceActive=selection.Item1||selection.Item2,Snapshot=Snapshot(DateTimeOffset.UtcNow,45,40,10,5)});
            require(canvas.State.FanMode=="Automatic"&&canvas.State.AutomaticSessionId=="retained-fan-episode"&&runtime.Releases==0&&runtime.Fences==0&&!canvas.CanApplyPerformance,
                $"Domain Apply changed intent/acknowledgement: selected={selection}; mode={canvas.State.FanMode}; episode={canvas.State.AutomaticSessionId}; releases={runtime.Releases}; fences={runtime.Fences}; canApply={canvas.CanApplyPerformance}.");
            require(form.Draft.Ac.CpuPl1Watts==profiles.Ac.CpuPl1Watts&&form.Draft.Ac.CpuPl2Watts==profiles.Ac.CpuPl2Watts&&
                form.Draft.Ac.GpuMaximumMHz==profiles.Ac.GpuMaximumMHz,"Domain toggles changed stored wattage or clocks.");
        }
        form.HandleCommand("performance-release");PumpUntil(()=>!canvas.Busy,"Disable all limits did not finish.");
        require(runtime.Commands==++expectedCommands&&!form.Draft.CpuEnabled&&!form.Draft.GpuEnabled&&canvas.State.FanMode=="Automatic"&&runtime.Fences==0&&runtime.Releases==0,
            "Disable all limits changed fan authority or did not reflect the disabled selection.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Independent control fixture exit stalled.");
        Console.WriteLine("PASS independent performance controls: CPU-only/GPU-only/both/neither Apply, no writes while selecting, unchanged fan Automatic episode and watt/clock preferences, global limit release leaves fans running; hardware IO=0.");
    }
}
