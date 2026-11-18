using System.Drawing.Imaging;
using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunCpuPowerEditorChecks()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            CpuPowerProductExecutionSelfTest.Run(Console.Out);
            TestCpuPowerEditor(Require);TestLivePerformanceApply(Require);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }

    private static void TestCpuPowerEditor(Action<bool,string> require)
    {
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://cpu-power-editor",fixture:runtime,fixtureProfiles:new());
        form.ClientSize=new(1672,941);form.Show();Application.DoEvents();
        var canvas=form.Canvas;canvas.Dock=DockStyle.None;canvas.Size=new(1672,941);
        canvas.Page=ProductPage.Performance;canvas.PerformanceTab=0;
        foreach(var source in new[]{ProductPowerProfile.Ac,ProductPowerProfile.Battery})
        {
            canvas.Editing=source;
            var peer=ProductProfilesStore.Serialize(form.Draft.With(source,ProductProfiles.DefaultProfile(source)));
            require(form.TryEditNumericValue("pl2","20",out _),"PL2 could not be lowered independently.");
            form.EditValue("pl1",99);canvas.Refresh();
            var pl1=canvas.Hits.Single(h=>h.Id=="pl1");var pl2=canvas.Hits.Single(h=>h.Id=="pl2");
            require(form.Draft.Get(source) is{CpuPl1Watts:20,CpuPl2Watts:20}&&pl1.Min==8&&pl1.Max==20&&pl2.Min==8&&pl2.Max==114,
                "PL1 did not follow PL2 or rendering still constrains PL2 by PL1.");
            var before=ProductProfilesStore.Serialize(form.Draft);
            require(!form.TryEditNumericValue("pl1","21",out var error)&&error.Contains("20")&&before==ProductProfilesStore.Serialize(form.Draft),
                "Numeric PL1 accepted a value above the current PL2.");
            var child=canvas.AccessibilityObject.GetChild(canvas.Hits.ToList().FindIndex(h=>h.Id=="pl1"))!;
            child.Value="99";
            child.Select(AccessibleSelection.TakeFocus);canvas.HandleKey(Keys.Right);form.HandleCommand("pl1-plus");
            require(form.Draft.Get(source) is{CpuPl1Watts:20,CpuPl2Watts:20},"Keyboard/accessibility/buttons raised PL2 or exceeded the dependent range.");
            form.HandleCommand("pl2-minus");
            require(form.Draft.Get(source) is{CpuPl1Watts:19,CpuPl2Watts:19},"Lowering PL2 did not reduce an excessive PL1.");
            require(form.TryEditNumericValue("pl2","60",out _)&&form.Draft.Get(source).CpuPl1Watts==19,
                "Raising PL2 silently increased sustained power.");
            form.EditValue("pl1",99);canvas.Refresh();
            require(form.Draft.Get(source) is{CpuPl1Watts:60,CpuPl2Watts:60}&&canvas.Hits.Single(h=>h.Id=="pl1").Max==60,
                "PL1 retained the old 44 W ceiling instead of following PL2.");
            require(form.TryEditNumericValue("pl1","60",out _),"Exact PL1 above 44 W was rejected.");
            require(form.TryEditNumericValue("pl2","114",out _)&&form.TryEditNumericValue("pl1","114",out _),
                "Equal PL1/PL2 at the existing PL2 ceiling was rejected.");
            var maximum=ProductProfilesStore.Serialize(form.Draft);
            require(!form.TryEditNumericValue("pl1","115",out _)&&maximum==ProductProfilesStore.Serialize(form.Draft),
                "PL1 exceeded the configured PL2 ceiling.");
            require(form.TryEditNumericValue("pl2","8",out _),"Minimum PL2 was rejected because of the prior PL1.");
            canvas.Refresh();
            using(var bitmap=new Bitmap(canvas.Width,canvas.Height))
            {
                canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
                require(canvas.Hits.Single(h=>h.Id=="pl1") is{Min:8,Max:8},"Degenerate 8 W slider has the wrong range.");
            }
            form.HandleCommand("pl1-plus");form.EditValue("pl1",0);
            using(var exactMinimum=new ProductNumericDialog("CPU PL1 (W)",8,8,8,text=>form.TryEditNumericValue("pl1",text,out var error)?null:error))
            {
                exactMinimum.Show(form);Application.DoEvents();exactMinimum.Input.Text="9";
                require(!exactMinimum.TryCommit(),"Fixed 8 W numeric dialog accepted 9 W.");
                exactMinimum.Input.Text="8";require(exactMinimum.TryCommit(),"Fixed 8 W numeric dialog rejected its only value.");
            }
            require(form.Draft.Get(source) is{CpuPl1Watts:8,CpuPl2Watts:8}&&ProductProfilesStore.Serialize(form.Draft.With(source,ProductProfiles.DefaultProfile(source)))==peer,
                "Minimum range or independent source preset was lost.");
            form.Draft.Validate();
        }
        canvas.Editing=ProductPowerProfile.Ac;form.EditValue("pl2",20);form.EditValue("pl1",20);
        canvas.Editing=ProductPowerProfile.Battery;form.EditValue("pl2",10);
        require(form.TryEditNumericValueForProfile("pl1","18",ProductPowerProfile.Ac,out _)&&
            form.Draft.Ac.CpuPl1Watts==18&&form.Draft.Battery.CpuPl1Watts==8&&canvas.Editing==ProductPowerProfile.Battery,
            "A pending exact edit used the other profile's dependent range.");
        var serialized=ProductProfilesStore.Serialize(form.Draft);
        require(ProductProfilesStore.Serialize(ProductProfilesStore.Parse(serialized))==serialized,"Dependent drafts did not round-trip through persistence.");
        require(runtime.Commands==0,"CPU draft editing dispatched a hardware operation.");
        canvas.Editing=ProductPowerProfile.Ac;canvas.Refresh();
        Directory.CreateDirectory("logs/cpu-power-editor-self-test");
        using(var bitmap=new Bitmap(canvas.Width,canvas.Height))
        {canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));bitmap.Save("logs/cpu-power-editor-self-test/pl2-20-pl1-range.png",ImageFormat.Png);}
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"CPU editor fixture exit stalled.");
        Console.WriteLine("PASS CPU power editor: PL1 follows PL2 through 114 W without a 44 W cap; lowering PL2 clamps PL1, raising it preserves PL1; exact inputs, keyboard/buttons/accessibility, 8 W rendering, independent AC/Battery and persistence; hardware IO=0.");
    }
}
