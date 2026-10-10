using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestSourceTheme(Action<bool,string> require)
    {
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://source",fixture:runtime,fixtureProfiles:new ProductProfiles());
        form.Show();Application.DoEvents();
        runtime.Publish(new(){Source="Ac",Runtime="Healthy"});
        form.HandleCommand("page-2");form.EditValue("pl1",31);
        
        runtime.Publish(new(){Source="Battery",Runtime="Healthy"});
        require(form.Canvas.Editing==ProductPowerProfile.Battery&&form.Canvas.Accent==ProductCanvas.Yellow,"Battery source did not select its performance draft and accent.");
        form.EditValue("pl1",9);var both=ProductProfilesStore.Serialize(form.Draft);
        form.HandleCommand("page-4");
        runtime.Publish(new(){Source="Ac",Runtime="Healthy"});
        require(form.Canvas.Editing==ProductPowerProfile.Ac&&form.Canvas.Accent==ProductCanvas.Blue,"AC source did not select its curve draft and accent.");
        require(ProductProfilesStore.Serialize(form.Draft)==both,"Source switching discarded pending edits.");
        form.HandleCommand("profile-battery");runtime.Publish(new(){Source="Ac",Runtime="Healthy"});
        require(form.Canvas.Editing==ProductPowerProfile.Battery&&form.Canvas.Accent==ProductCanvas.Blue,"Repeated source publications overrode a voluntary draft selection or changed the active-source color.");
        require(form.TryEditNumericValueForProfile("pl1","32",ProductPowerProfile.Ac,out _)&&form.Draft.Ac.CpuPl1Watts==32&&form.Draft.Battery.CpuPl1Watts==9&&form.Canvas.Editing==ProductPowerProfile.Battery,"A source change redirected a pending numeric dialog into another preset.");
        runtime.Publish(new(){Source="Battery",Runtime="Failed",Failure="fixture"});
        require(form.Canvas.Accent==ProductCanvas.Red,"Error did not override battery accent.");
        runtime.Publish(new(){Source="Battery",Runtime="Healthy"});
        require(form.Canvas.Accent==ProductCanvas.Yellow,"Resolved error did not restore battery accent.");
        runtime.Publish(new(){Source="Unknown",Runtime="Initializing"});
        require(form.Canvas.Editing==ProductPowerProfile.Battery,"Unknown source replaced the draft selection.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Source/theme fixture exit stalled.");
        Console.WriteLine("PASS product source/theme: automatic AC/Battery selection in performance and curves, preserved drafts, voluntary editing, error priority; no hardware IO.");
    }
}
