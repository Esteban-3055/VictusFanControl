using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestSelectedCurveInsertion(Action<bool,string> require)
    {
        ProductFanControllerFactorySelfTest.Run(require);
        ProductFanRangePreview.Enabled=true;
        try
        {
            var previewRuntime=new RecordingRuntime();var preferences=new ProductProfiles{StartMinimized=true,ActivateAutomaticOnStart=true};
            using var trial=new ProductForm("fixture://preview",fixture:previewRuntime,fixtureProfiles:preferences,enableStartupAutomaticInFixture:true);
            trial.Show();Application.DoEvents();trial.PresentationTick();
            require(trial.Visible&&previewRuntime.Commands==0&&trial.Canvas.State.FanMode=="Firmware","Preview started minimized or activated saved Automatic.");
            trial.Canvas.State=trial.Canvas.State with{ManualMaximumLevel=54,FanUpperRangePreview=true,AutomaticAuthorized=true};
            trial.EditValue("manual",999);require(trial.Canvas.ManualLevel==54,"Preview slider lost logical maximum.");
            trial.HandleCommand("fan-mode-2");require(previewRuntime.Commands==0,"Preview accepted Automatic through a semantic command.");
            require(trial.Draft.StartMinimized&&trial.Draft.ActivateAutomaticOnStart,"Preview rewrote saved startup preferences.");
        }
        finally{ProductFanRangePreview.Enabled=false;}
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://insert",fixture:runtime,fixtureProfiles:new ProductProfiles());
        form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Page=ProductPage.Curves;canvas.ShowCurvePoints=true;
        canvas.SelectedNode=-1;canvas.Refresh();var original=form.Draft.Ac.Fan.UnifiedDemand!.Curve.ToArray();
        require(canvas.Hits.Single(h=>h.Id=="node-add").Enabled==false,"Add enabled without selection.");
        form.HandleCommand("node-add");require(form.Draft.Ac.Fan.UnifiedDemand!.Curve.SequenceEqual(original),"No-selection command inserted a point.");
        canvas.SelectedNode=2;canvas.Refresh();require(canvas.Hits.Single(h=>h.Id=="node-add").Enabled,"Selected valid interval denied insertion.");
        form.HandleCommand("node-add");var changed=form.Draft.Ac.Fan.UnifiedDemand!.Curve;
        require(changed.Count==original.Length+1&&canvas.SelectedNode==3&&changed[3].Input>original[2].Input&&changed[3].Input<original[3].Input,
            "Point did not enter selected interval.");
        require(changed.Where((_,i)=>i!=3).SequenceEqual(original),"Insertion moved existing points.");
        require(changed[3].Level==Math.Round(AdaptiveCurveProfiles.Interpolate(original,changed[3].Input)),"Inserted height changed local interpolation.");
        canvas.SelectedNode=changed.Count-1;canvas.Refresh();require(!canvas.Hits.Single(h=>h.Id=="node-add").Enabled,"Last endpoint allowed insertion.");
        require(!ProductCurveInsertion.CanInsert([new(0,10),new(1,20),new(100,50)],0),"One-percent gap allowed duplicate position.");
        require(!ProductCurveInsertion.CanInsert(Enumerable.Range(0,64).Select(i=>new AdaptiveFanCurvePoint(i,10)).ToArray(),1),"64-node limit escaped.");
        while(form.Draft.Ac.Fan.UnifiedDemand!.Curve.Count<64)
        {
            var points=form.Draft.Ac.Fan.UnifiedDemand!.Curve;
            canvas.SelectedNode=Enumerable.Range(0,points.Count-1).FirstOrDefault(n=>points[n+1].Input-points[n].Input>=2,-1);
            require(canvas.SelectedNode>=0,"Selected insertion could not reach the 64-node cap.");
            form.HandleCommand("node-add");
        }
        canvas.Refresh();require(!canvas.Hits.Single(h=>h.Id=="node-add").Enabled,"Add enabled at the 64-node cap.");
        var capped=form.Draft.Ac.Fan.UnifiedDemand!.Curve.ToArray();form.HandleCommand("node-add");
        require(form.Draft.Ac.Fan.UnifiedDemand!.Curve.SequenceEqual(capped),"Insertion changed a capped curve.");
        form.HandleCommand("profile-battery");require(canvas.SelectedNode==-1,"Source switch retained a stale selected point.");
        form.Draft.Validate();require(runtime.Commands==0,"Curve insertion issued hardware commands.");
        Console.WriteLine("Selected curve insertion: PASS (selected right interval, interpolation, old points preserved, no-selection/last/no-space/64-node guards, source switch; zero hardware IO).");
    }
}
