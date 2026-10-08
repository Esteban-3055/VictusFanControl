using VictusFanControl.PlatformThermalReplay;
namespace VictusFanControl.App;
internal static class PlatformPhysicalSelfTest
{
    internal static int Run()
    {
        try
        {
            PhysicalExperimentSelfTest.Run();
            // Only constructor/render/dispose: no hardware entry or reader is invoked.
            using var form=new PlatformThermalExperimentForm("synthetic-unused-modules",Path.Combine(Path.GetTempPath(),"vfc-unused-"+Guid.NewGuid().ToString("N")));
            form.Show();Application.DoEvents();
            if(form.Controls.Count!=2||form.ClientSize.Width<700)throw new InvalidOperationException("Experimental UI fixture is incomplete");
            using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
            Directory.CreateDirectory("logs/platform-physical-self-test");bitmap.Save("logs/platform-physical-self-test/experiment.png");
            Console.WriteLine("Physical experiment Windows UI self-test: PASS; no hardware IO.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 175;}
    }
}
