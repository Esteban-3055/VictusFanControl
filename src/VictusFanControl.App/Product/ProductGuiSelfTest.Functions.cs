using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestFunctions(Action<bool,string> require)
    {
        var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://functions",fixture:runtime,fixtureProfiles:new(),
            displayMetricsInFixture:()=>new("fixture",new(0,0,1920,1080),new(0,0,1920,1040),96));
        form.Show();Application.DoEvents();form.Canvas.Page=ProductPage.Functions;
        form.Size=new(1040,660);Application.DoEvents();
        using(var bitmap=new Bitmap(form.Canvas.Width,form.Canvas.Height))
        {
            form.Canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
            Directory.CreateDirectory("logs/product-gui-self-test");bitmap.Save("logs/product-gui-self-test/functions-minimum-layout.png");
        }
        var navigation=ProductCanvas.Navigation.Select(n=>n.Page).ToList();
        require(navigation.IndexOf(ProductPage.Functions)==navigation.IndexOf(ProductPage.Performance)+1,"Functions is not immediately below Performance.");
        require(form.Canvas.Hits.Any(h=>h.Id=="function-0-rule")&&form.Canvas.Hits.Any(h=>h.Id=="function-3-select")&&
            form.Canvas.Hits.All(h=>h.Id!="function-0-linked"),"Function summary/detail or advanced separation is missing.");
        form.HandleCommand("functions-independent");
        require(runtime.Commands==0&&form.Draft.Functions is{StopAutomaticOnPerformanceFailure:false,StopAutomaticOnCtgpFailure:false,
            ExternalOutputMode:ProductExternalOutputMode.TemperatureCurve},"Independent preset dispatched hardware or retained failure coupling.");
        form.HandleCommand("function-0-startup");require(form.Draft.Functions!.CpuLimits.StartWithApplication&&runtime.Commands==0,"Startup preference activated a controller.");
        form.HandleCommand("functions-apply");PumpUntil(()=>!form.Canvas.Busy&&runtime.LastFunctions is not null,"Function policy apply did not finish.");
        require(runtime.Commands==1&&runtime.LastFunctions==form.Draft.Functions,"Applying linkage changed or lost a draft preference.");
        form.HandleCommand("function-2-on");PumpUntil(()=>!form.Canvas.Busy&&runtime.LastFunction==ProductFunction.Ctgp,"Independent cTGP action not routed.");
        form.HandleCommand("function-3-off");PumpUntil(()=>!form.Canvas.Busy&&runtime.LastFunction==ProductFunction.ExternalCooling,"External withdrawal action not routed.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Functions fixture did not close.");exit.GetAwaiter().GetResult();
    }
}
