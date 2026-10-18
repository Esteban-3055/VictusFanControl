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
        TestExternalMarkers();
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
        curve.EditNode(0,0,0);Require(curve.Draft.Points[0].Output==0,"Editor denied zero output");
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
        Require(curve.EditNumeric("manual","0") is null&&curve.Draft.ManualOutput==0,"Manual zero denied");
        Require(curve.EditNumeric("start-output","0") is null&&curve.Draft.StartOutput==0,"Start zero denied");
        foreach(var (id,value) in new[]{("rise",17),("fall",23),("filter",2345),("hysteresis",7),("hold",4567),("start",1234),("start-output",201),("manual",87)})
            form.EditValue("external-curve-value-"+id,value);
        var expected=curve.Draft with{Rise=17,Fall=23,FilterMs=2345,Hysteresis=7,DownHoldMs=4567,StartMs=1234,StartOutput=201,ManualOutput=87};
        Require(curve.Draft==expected,"Response/startup control dispatch changed a field or its units");
        form.HandleCommand("external-curve-save");PumpUntil(()=>!curve.Busy,"Curve save blocked");
        var saved=new ExternalCoolingSettingsStore(path).Load(out _);
        Require(saved.Curve==expected with{Revision=curve.Draft.Revision}&&!curve.Dirty,"Response/startup settings did not persist");
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
    private static void TestExternalMarkers()
    {
        static void Require(bool ok,string message)=>ExternalCoolingSelfTest.Require(ok,message);
        var now=DateTimeOffset.UtcNow;var curve=new ExternalCurveConfiguration();
        var telemetry=new ExportTelemetry{CapturedAtUtc=now,DecisionAtUtc=now,SampledAtUtc=now,DemandaPct=50,
            ModoSolicitado="Automatic",Solicitud=27,SolicitudValida=true,ExternalConfiguration=curve};
        var status=new DeviceStatus{AckSequence=3,ExternalCurveVersion=1,ExternalMinimumOutput=0,AppliedConfigRevision=curve.Revision,
            AppliedDuty=64,ExternalRequest=130,AmbientTempC=24,AppliedPwmPct=25,Mode="Remote",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}};
        var diagnostics=new ExternalCoolingDiagnostics{State=ExternalCoolingConnectionState.Connected,
            LastValidReceivedUtc=now,Device=status,AcknowledgedTelemetry=telemetry};
        IReadOnlyList<ExternalCurveMarker> Build(ExternalCoolingDiagnostics? d)=>ExternalCurveMarkers.Build(curve,curve,d,now);
        var points=Build(diagnostics);
        Require(points.Count==2&&points[0]==new ExternalCurveMarker(50,130,false)&&points[1]==new ExternalCurveMarker(50,64,true),"Live target/applied markers mixed");
        Require(Build(diagnostics with{LastSent="a newer unacknowledged frame"}).SequenceEqual(points),"Marker used unacknowledged source");
        Require(Build(diagnostics with{LastValidReceivedUtc=now.AddMilliseconds(-2000)}).Count==0,"Stale device marker retained");
        Require(Build(diagnostics with{LastValidReceivedUtc=now.AddSeconds(1)}).Count==0,"Future marker retained");
        Require(Build(diagnostics with{AcknowledgedTelemetry=telemetry with{CapturedAtUtc=now.AddMilliseconds(-2000)}}).Count==0,"Stale frame marker retained");
        Require(Build(diagnostics with{AcknowledgedTelemetry=telemetry with{DecisionAtUtc=now.AddMilliseconds(-5000)}}).Count==0,"Stale demand marker retained");
        Require(Build(diagnostics with{AcknowledgedTelemetry=telemetry with{DemandaPct=null}}).Count==0,"Null demand became zero");
        Require(Build(diagnostics with{Device=status with{Mode="Local"}}).Count==1,"Local fallback claimed applied curve");
        Require(Build(diagnostics with{Device=status with{AppliedConfigRevision=null}}).Count==1,"Unconfirmed revision claimed applied curve");
        Require(Build(diagnostics with{State=ExternalCoolingConnectionState.Disconnected}).Count==0,"Disconnected marker retained");
        var zero=curve with{Points=[new(0,0),new(100,255)]};
        var zeroDiagnostics=diagnostics with{AcknowledgedTelemetry=telemetry with{DemandaPct=0,ExternalConfiguration=zero},Device=status with{AppliedDuty=0}};
        var zeroPoints=ExternalCurveMarkers.Build(zero,zero,zeroDiagnostics,now);
        Require(zeroPoints.Count==2&&zeroPoints.All(p=>p.Input==0&&p.Output==0),"Real zero marker missing");
        using(var canvas=new ProductCanvas{Size=new(1672,941),Page=ProductPage.ExternalCurve}) {
            canvas.ExternalCurve=new(canvas,()=>new ExternalCoolingSettings{Curve=zero},_=>Task.CompletedTask,()=>zeroDiagnostics);
            using var image=new Bitmap(canvas.Width,canvas.Height);
            canvas.DrawToBitmap(image,new(Point.Empty,canvas.Size));
            Directory.CreateDirectory("logs/external-cooling-self-test");
            image.Save("logs/external-cooling-self-test/external-curve-live-zero.png");
        }
        var requestCurve=curve with{Input=1};
        Require(ExternalCurveMarkers.Build(requestCurve,requestCurve,diagnostics,now)[0].Input==50,"Accepted request marker not normalized");
        Require(ExternalCurveMarkers.Build(requestCurve,requestCurve,diagnostics with{AcknowledgedTelemetry=telemetry with{SolicitudValida=false}},now).Count==0,"Unaccepted request plotted");
    }

}
