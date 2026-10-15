using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunSensorChecks()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            ProductSensorPolicySelfTest.Run(Require);
            ProductAutomaticActivationSelfTest.Run(Require);
            var runtime=new RecordingRuntime();
            using var form=new ProductForm("fixture://modules",fixture:runtime,
                fixtureProfiles:new ProductProfiles{ExperimentalPlatformRetention=true});
            form.Show();Application.DoEvents();
            var before=ProductProfilesStore.Serialize(form.Draft);
            form.HandleCommand("retention-toggle");form.HandleCommand("retention-quiet-candidate");
            Require(!form.Draft.ExperimentalPlatformRetention&&ProductProfilesStore.Serialize(form.Draft)==before,
                "Retired controls changed the imported configuration.");
            form.Canvas.Page=ProductPage.Advanced;form.Canvas.AdvancedTab=5;
            foreach(var size in new[]{new Size(1040,660),new Size(1672,941)})
            {
                form.ClientSize=size;form.PerformLayout();
                using var image=new Bitmap(form.Canvas.Width,form.Canvas.Height);
                form.Canvas.DrawToBitmap(image,new(0,0,image.Width,image.Height));
                Require(!form.Canvas.Hits.Any(h=>h.Id.StartsWith("retention-")),"A retired control remains actionable.");
                Directory.CreateDirectory("logs/product-gui-self-test");
                image.Save($"logs/product-gui-self-test/sensors-{size.Width}.png");
            }
            Require(runtime.Commands==0,"Sensor page dispatched a hardware command.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Sensor fixture exit hung.");
            Console.WriteLine("Retired sensor UI: PASS (legacy enabled profile, ignored old commands, no auxiliary controls, minimum/large layouts, no hardware commands).");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
