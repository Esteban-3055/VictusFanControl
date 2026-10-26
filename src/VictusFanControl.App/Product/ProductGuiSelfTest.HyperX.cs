using System.Drawing.Imaging;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunHyperXChecks()
    {
        try
        {
            HyperXSelfTest.Run();
            var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://hyperx",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();
            form.HandleCommand("page-"+(int)ProductPage.HyperXBattery);Application.DoEvents();
            var page=form.Canvas.Controls.OfType<HyperXBatteryPage>().Single();
            var now=DateTimeOffset.UtcNow;page.UpdateReading(new(){ReceiverPresent=true,Status="Lectura de prueba",Percent=55,Millivolts=3795,SampledAt=now,Muted=true},now);
            var dir=Path.GetFullPath("logs/hyperx-self-test");Directory.CreateDirectory(dir);
            foreach(var size in new[]{new Size(1344,756),new Size(1040,660)})
            {
                form.ClientSize=size;Application.DoEvents();
                page.UpdateReading(new(){ReceiverPresent=true,Status="Lectura de prueba",Percent=55,Millivolts=3795,SampledAt=now,Muted=true},now);
                using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new(0,0,form.Width,form.Height));bitmap.Save(Path.Combine(dir,$"hyperx-{size.Width}.png"),ImageFormat.Png);
            }
            page.LcdLowSeconds.Value=8;page.LcdLowCycles.Value=4;
            page.LcdCriticalSeconds.Value=12;page.LcdCriticalCycles.Value=6;
            page.SaveAsync().GetAwaiter().GetResult();
            if(page.Draft.DisplayConfiguration() is not{LowDurationMs:8000,LowBlinkCycles:4,CriticalDurationMs:12000,CriticalBlinkCycles:6})
                throw new InvalidOperationException("LCD alert editing did not preserve timing/cycles.");
            page.Critical.Value=30;page.Low.Value=20;page.SaveAsync().GetAwaiter().GetResult();
            form.HandleCommand("page-"+(int)ProductPage.ExternalCurve);Application.DoEvents();
            if(page.Visible||runtime.Commands!=0)throw new InvalidOperationException("HyperX page leaked visibility or hardware authority.");
            form.Hide();Console.WriteLine("PASS HyperX GUI: navigation below external curve, native settings, invalid thresholds, two window sizes, hidden-page lifecycle; zero hardware commands.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
