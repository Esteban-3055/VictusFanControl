using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;
namespace VictusFanControl.App;
internal static class PlatformPhysicalSelfTest
{
    internal static int Run()
    {
        try
        {
            PhysicalExperimentSelfTest.Run();
            // Only constructor/render/dispose: no hardware entry or reader is invoked.
            var original=new ProductProfiles{CpuEnabled=false,GpuEnabled=false};
            var originalText=ProductProfilesStore.Serialize(original);
            using var form=new PlatformThermalExperimentForm("synthetic-unused-modules",Path.Combine(Path.GetTempPath(),"vfc-unused-"+Guid.NewGuid().ToString("N")),original);
            form.Show();Application.DoEvents();
            if(form.Controls.Count!=3||form.ClientSize.Width<700)throw new InvalidOperationException("Experimental UI fixture is incomplete");
            if(form.CpuPl1.Value!=25||form.CpuPl2.Value!=30||form.GpuMaximum.Value!=original.Ac.GpuMaximumMHz)
                throw new InvalidOperationException("Trial initial limits are wrong.");
            Directory.CreateDirectory("logs/platform-physical-self-test");
            using(var initial=new Bitmap(form.Width,form.Height))
            {form.DrawToBitmap(initial,new Rectangle(0,0,initial.Width,initial.Height));initial.Save("logs/platform-physical-self-test/experiment-limits-initial.png");}
            form.CpuPl1.Value=35;form.CpuPl2.Value=30;
            bool refused=false;try{form.FreezeSelectedProfiles();}catch(InvalidDataException){refused=true;}
            if(!refused||!form.CpuPl1.Enabled)throw new InvalidOperationException("Invalid PL ordering must refuse before freezing.");
            form.CpuPl1.Value=25;form.CpuPl2.Value=30;form.GpuMaximum.Value=1950;
            var frozen=form.FreezeSelectedProfiles();
            if(frozen.Ac.CpuPl1Watts!=25||frozen.Ac.CpuPl2Watts!=30||frozen.Ac.GpuMaximumMHz!=1950||!frozen.CpuEnabled||!frozen.GpuEnabled||
                form.CpuPl1.Enabled||form.CpuPl2.Enabled||form.GpuMaximum.Enabled)
                throw new InvalidOperationException("Selected limits must be frozen with both controls enabled.");
            if(ProductProfilesStore.Serialize(original)!=originalText||System.Text.Json.JsonSerializer.Serialize(frozen.Battery)!=System.Text.Json.JsonSerializer.Serialize(original.Battery)||
                !System.Text.Json.JsonSerializer.Serialize(frozen.Ac.Fan).Equals(System.Text.Json.JsonSerializer.Serialize(original.Ac.Fan)))
                throw new InvalidOperationException("Trial must preserve input profiles, Battery and AC fan curve.");
            using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new Rectangle(0,0,bitmap.Width,bitmap.Height));
            Directory.CreateDirectory("logs/platform-physical-self-test");bitmap.Save("logs/platform-physical-self-test/experiment.png");
            Console.WriteLine("Physical experiment Windows UI self-test: PASS; no hardware IO.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 175;}
    }
}
