namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestProductIcons(Action<bool,string> require)
    {
        using(var icons=new ProductIcons())
        {
            require(icons.Program.Size.Width==32&&icons.Default.Width>0&&icons.Automatic.Width>0&&icons.AutomaticMuted.Width>0&&icons.Error.Width>0,"Embedded artwork could not load.");
            require(!ReferenceEquals(icons.Default,icons.Automatic)&&!ReferenceEquals(icons.Automatic,icons.Error),"Tray icons share the same object.");
            require(!ReferenceEquals(icons.AutomaticMuted,icons.Automatic)&&!ReferenceEquals(icons.AutomaticMuted,icons.Error)&&ReferenceEquals(icons.For(ProductTrayState.AutomaticMuted),icons.AutomaticMuted),"Muted Automatic artwork was not selected.");
        }
        var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://icons",fixture:runtime,fixtureProfiles:new());form.Show();Application.DoEvents();
        require(form.Icon is not null&&form.TrayState==ProductTrayState.Default,"Program/default icon missing.");
        var active=new ProductRuntimeState{Runtime="Healthy",FanMode="Automatic",FanAuthority="Custom"};
        runtime.Publish(active with{AutomaticPreparing=true});require(form.TrayState==ProductTrayState.Default,"Preparing displayed successful Automatic.");
        runtime.Publish(active);require(form.TrayState==ProductTrayState.Automatic,"Active Automatic icon missing.");
        var muted=new HyperXSnapshot{ReceiverPresent=true,Powered=true,Muted=true};
        form.PresentHyperXTray(muted);require(form.TrayState==ProductTrayState.AutomaticMuted,"Mute did not update the tray without a runtime publication.");
        runtime.Publish(active);require(form.TrayState==ProductTrayState.AutomaticMuted,"Runtime publication erased confirmed mute.");
        form.PresentHyperXTray(muted with{Muted=false});require(form.TrayState==ProductTrayState.Automatic,"Unmute did not restore normal Automatic.");
        form.PresentHyperXTray(muted with{Muted=null});require(form.TrayState==ProductTrayState.Automatic,"Unknown mute invented a muted icon.");
        form.PresentHyperXTray(muted with{ReceiverPresent=false});require(form.TrayState==ProductTrayState.Automatic,"Disconnected receiver retained a muted icon.");
        form.PresentHyperXTray(muted with{Powered=false});require(form.TrayState==ProductTrayState.Automatic,"Powered-off headset retained a muted icon.");
        form.PresentHyperXTray(muted);
        runtime.Publish(active with{AutomaticPreparing=true});require(form.TrayState==ProductTrayState.Default,"Muted icon appeared while Automatic was preparing.");
        runtime.Publish(active with{FanAuthority="Firmware"});require(form.TrayState==ProductTrayState.Default,"Muted icon appeared without active Automatic authority.");
        runtime.Publish(new(){Runtime="Healthy"});require(form.TrayState==ProductTrayState.Default,"Manual Firmware selection displayed error.");
        runtime.Publish(active with{FanMode="Manual"});require(form.TrayState==ProductTrayState.Default,"Muted icon appeared in Manual.");
        runtime.Publish(active with{LifecycleBlocked=true});require(form.TrayState==ProductTrayState.Error,"Interrupted Automatic did not display error.");
        form.PresentHyperXTray(muted with{Muted=false});require(form.TrayState==ProductTrayState.Error,"Unmute hid the existing failure.");
        form.PresentHyperXTray(muted);require(form.TrayState==ProductTrayState.Error,"Mute overrode the failure icon.");
        runtime.Publish(new(){Runtime="Healthy"});require(form.TrayState==ProductTrayState.Error,"Error disappeared before successful reactivation.");
        runtime.Publish(active);require(form.TrayState==ProductTrayState.AutomaticMuted,"Successful muted reactivation retained error.");
        form.PresentHyperXTray(muted with{Muted=false});require(form.TrayState==ProductTrayState.Automatic,"Unmute after successful reactivation retained muted icon.");
        runtime.Publish(active with{Failure="guardian failure"});require(form.TrayState==ProductTrayState.Error,"Failure did not take priority over Automatic.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Icon fixture could not exit.");
        Console.WriteLine("Product icons: PASS (embedded artwork, mute/unmute while Automatic is active, disconnected/unknown/powered-off state, Manual/preparing, error priority/latch and successful retry reset; no hardware IO).");
    }
}
