using VictusFanControl.ExternalCooling;
using VictusFanControl.Product;
namespace VictusFanControl.App;
internal static partial class ProductGuiSelfTest
{
    // Own process/context: rendering and closing this fixture must not change the message pump
    // used by the separate network suspend/resume fixtures.
    internal static int RunExternalCurveChecks()
    {
        try{TestExternalCurve();return 0;}
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void TestExternalCurve()
    {
        static void Require(bool ok,string message)=>ExternalCoolingSelfTest.Require(ok,message);
        var root=Path.GetFullPath("logs/external-cooling-self-test");Directory.CreateDirectory(root);
        var path=Path.Combine(root,"curve-"+Guid.NewGuid().ToString("N")+".json");
        var store=new ExternalCoolingSettingsStore(path);store.Save(new());
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://external-curve",fixture:runtime,fixtureProfiles:new ProductProfiles(),externalCoolingPath:path);
        form.Show();PumpUntil(()=>form.ExternalCoolingStatus is not null,"External manager startup");
        form.HandleCommand("page-"+(int)ProductPage.ExternalCooling);Application.DoEvents();
        var basePage=form.ExternalPage!;basePage.Host.Text="base.example.local";
        form.HandleCommand("page-"+(int)ProductPage.ExternalCurve);Application.DoEvents();
        var curve=form.Canvas.ExternalCurve??throw new InvalidOperationException("Curve page missing");
        var original=ProductProfilesStore.Serialize(form.Draft);var notebookDirty=form.Canvas.Dirty;
        curve.EditNode(2,55,145);Require(curve.Dirty&&curve.Draft.Points[2]==new ExternalCurvePoint(55,145),"Point edit failed");
        form.HandleCommand("external-curve-add");Require(curve.Draft.Points.Length==6,"Point insertion failed");
        form.HandleCommand("external-curve-remove");Require(curve.Draft.Points.Length==5,"Point removal failed");
        form.HandleCommand("external-curve-tuning");form.EditValue("external-curve-value-rise",18);Require(curve.Draft.Rise==18,"External tuning dispatched incorrectly");
        form.Canvas.Refresh();
        void Click(string id)
        {
            var hit=form.Canvas.Hits.Single(h=>h.Id==id);var scale=Math.Min(form.Canvas.Width/1672f,form.Canvas.Height/941f);
            var point=new Point((int)((form.Canvas.Width-1672*scale)/2+(hit.Bounds.X+hit.Bounds.Width/2)*scale),(int)((form.Canvas.Height-941*scale)/2+(hit.Bounds.Y+hit.Bounds.Height/2)*scale));
            form.Canvas.PointerDown(point);form.Canvas.PointerUp(point);
        }
        Click("external-curve-value-rise-plus");Require(curve.Draft.Rise==19,"Visible + did not edit external setting");
        Click("external-curve-value-rise-minus");Require(curve.Draft.Rise==18,"Visible - did not edit external setting");
        Require(curve.EditNumeric("filter","1650") is null&&curve.Draft.FilterMs==1650,"Exact external numeric edit failed");
        Require(curve.EditNumeric("filter","NaN") is not null&&curve.Draft.FilterMs==1650,"Invalid numeric edit changed curve");
        form.HandleCommand("external-curve-save");PumpUntil(()=>!curve.Busy,"Curve save blocked");
        var saved=new ExternalCoolingSettingsStore(path).Load(out _);Require(saved.Curve==curve.Draft&&saved.Curve.Rise==18&&!curve.Dirty,"External curve did not persist");
        form.HandleCommand("page-"+(int)ProductPage.ExternalCooling);form.HandleCommand("external-save");PumpUntil(()=>!basePage.Busy,"Base save blocked");
        saved=new ExternalCoolingSettingsStore(path).Load(out _);Require(saved.Curve==curve.Draft&&saved.Host=="base.example.local","Base draft overwrote saved curve");
        Require(runtime.Commands==0&&original==ProductProfilesStore.Serialize(form.Draft)&&form.Canvas.Dirty==notebookDirty,"External editor affected notebook authority/settings");
        form.HandleCommand("page-"+(int)ProductPage.ExternalCurve);form.Canvas.Dock=DockStyle.None;
        foreach(var tuning in new[]{false,true})
        {
            form.HandleCommand(tuning?"external-curve-tuning":"external-curve-points");
            foreach(var size in new[]{new Size(1672,941),new Size(1040,660),new Size(3344,1882)})
            {
                form.Canvas.Size=size;form.Canvas.Refresh();Application.DoEvents();
                Require(form.Canvas.Hits.Where(h=>h.Id.StartsWith("page-")).All(h=>h.Bounds.Bottom<882),"Sidebar overflow");
                // DrawToBitmap includes invisible native children; omit the Base page editors for this capture.
                var editors=new[]{basePage.Host,basePage.Port,basePage.Key};foreach(var editor in editors)form.Canvas.Controls.Remove(editor);
                try
                {
                    using var image=new Bitmap(size.Width,size.Height);form.Canvas.DrawToBitmap(image,new(Point.Empty,size));
                    image.Save(Path.Combine(root,$"external-curve-{tuning}-{size.Width}.png"));
                }
                finally{foreach(var editor in editors)form.Canvas.Controls.Add(editor);basePage.Layout(false);}
            }
        }
        curve.EditNode(2,99,999);curve.Draft.Validate();
        form.HandleCommand("external-curve-discard");Require(curve.Draft==saved.Curve&&!curve.Dirty,"Discard curve failed");
        var close=form.RequestExitAsync();PumpUntil(()=>close.IsCompleted,"Curve fixture did not close");close.GetAwaiter().GetResult();
        Console.WriteLine("PASS external curve UI: points, tuning, independent saves, preferences, DPI/sidebar, no notebook writes.");
    }
}
