namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestProductIcons(Action<bool,string> require)
    {
        using(var icons=new ProductIcons())
        {
            require(icons.Program.Size.Width==32&&icons.Default.Width>0&&icons.Automatic.Width>0&&icons.Error.Width>0,"Embedded artwork could not load.");
            require(!ReferenceEquals(icons.Default,icons.Automatic)&&!ReferenceEquals(icons.Automatic,icons.Error),"Tray icons share the same object.");
        }
        var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://icons",fixture:runtime,fixtureProfiles:new());form.Show();Application.DoEvents();
        require(form.Icon is not null&&form.TrayState==ProductTrayState.Default,"Program/default icon missing.");
        var active=new ProductRuntimeState{Runtime="Healthy",FanMode="Automatic",FanAuthority="Custom"};
        runtime.Publish(active with{AutomaticPreparing=true});require(form.TrayState==ProductTrayState.Default,"Preparing displayed successful Automatic.");
        runtime.Publish(active);require(form.TrayState==ProductTrayState.Automatic,"Active Automatic icon missing.");
        runtime.Publish(new(){Runtime="Healthy"});require(form.TrayState==ProductTrayState.Default,"Manual Firmware selection displayed error.");
        runtime.Publish(active with{LifecycleBlocked=true});require(form.TrayState==ProductTrayState.Error,"Interrupted Automatic did not display error.");
        runtime.Publish(new(){Runtime="Healthy"});require(form.TrayState==ProductTrayState.Error,"Error disappeared before successful reactivation.");
        runtime.Publish(active);require(form.TrayState==ProductTrayState.Automatic,"Successful reactivation retained error.");
        runtime.Publish(active with{Failure="guardian failure"});require(form.TrayState==ProductTrayState.Error,"Failure did not take priority over Automatic.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Icon fixture could not exit.");
        Console.WriteLine("Product icons: PASS (embedded artwork, normal/preparing/Automatic/error latch and successful retry reset; no hardware IO).");
    }
}
