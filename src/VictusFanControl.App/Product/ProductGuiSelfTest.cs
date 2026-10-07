using System.Drawing.Imaging;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Performance;
using VictusFanControl.Telemetry;
using VictusFanControl.Safety;
using VictusFanControl.Runtime;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
namespace VictusFanControl.App;

internal static class ProductGuiSelfTest
{
    internal static int Run()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            TestAutomaticReview(Require);
            TestLiveCurveApply(Require);
            TestAdvancedSettings(Require);
            TestLivePerformanceApply(Require);
            TestSessionLogs(Require);
            ProductAutomaticActivationSelfTest.Run(Require);
            var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles());
            form.ClientSize=new(1672,941);form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Dock=DockStyle.None;canvas.Size=new(1672,941);
            Require(runtime.Commands==0,"Startup acquired authority.");
            form.HandleCommand("profile-battery");form.EditValue("pl1",30);form.EditValue("pl2",15);
            Require(form.Draft.Battery.CpuPl1Watts==30&&form.Draft.Battery.CpuPl2Watts==30&&form.Draft.Ac.CpuPl1Watts==35,"PL1/PL2 or independent AC failed.");
            form.HandleCommand("profile-ac");canvas.ShowCurvePoints=true;form.EditNode(3,70,31);
            Require(form.Draft.Ac.Fan.UnifiedDemand!.Curve[3].Level==31&&form.Draft.Battery.Fan.UnifiedDemand!.Curve[3].Level==ProductProfiles.DefaultProfile(ProductPowerProfile.Battery).Fan.UnifiedDemand!.Curve[3].Level,"Curve edit leaked across profiles.");
            for(int i=0;i<7;i++)form.HandleCommand("page-"+i);
            form.EditValue("manual",0);Require(canvas.ManualLevel==10,"Manual lower endpoint inaccessible.");
            form.EditValue("manual",99);Require(canvas.ManualLevel==50,"Manual upper endpoint escaped.");
            form.EditValue("manual",10);
            Require(runtime.Commands==0,"Editing/navigation wrote hardware.");
            form.HandleCommand("fan-mode-2");Require(runtime.Commands==0,"Closed Automatic gate dispatched.");
            form.EditValue("gpu",99999);Require(form.Draft.Ac.GpuMaximumMHz==2500,"GPU slider escaped upper bound.");
            form.EditValue("gpu",0);Require(form.Draft.Ac.GpuMaximumMHz==210,"GPU slider escaped lower bound.");
            Require(form.TryEditNumericValue("pl1","30",out _)&&form.TryEditNumericValue("pl2","35",out _)&&form.TryEditNumericValue("gpu","1800",out _),"Exact numeric values rejected.");
            Require(form.Draft.Ac.CpuPl1Watts==30&&form.Draft.Ac.CpuPl2Watts==35&&form.Draft.Ac.GpuMaximumMHz==1800&&form.Draft.Battery.GpuMaximumMHz==1200,"Exact numeric edits rounded, clamped or leaked across sources.");
            var typedDraft=ProductProfilesStore.Serialize(form.Draft);
            foreach(var entry in new[]{("pl1","45"),("pl2","29"),("pl2","115"),("gpu","209"),("gpu","2501"),("gpu","1800.5"),("gpu",""),("gpu","NaN"),("gpu","99999999999")})
                Require(!form.TryEditNumericValue(entry.Item1,entry.Item2,out var error)&&error.Length>0&&typedDraft==ProductProfilesStore.Serialize(form.Draft),"Invalid text silently changed a performance draft.");
            canvas.Busy=true;Require(!form.TryEditNumericValue("gpu","1700",out _)&&typedDraft==ProductProfilesStore.Serialize(form.Draft),"Busy numeric edit changed draft.");canvas.Busy=false;
            using(var numeric=new ProductNumericDialog("GPU máximo (MHz) · AC",1800,210,2500,text=>form.TryEditNumericValue("gpu",text,out var error)?null:error))
            {
                numeric.Show(form);Application.DoEvents();numeric.Input.Text="1800.5";
                Require(!numeric.TryCommit()&&numeric.DialogResult!=DialogResult.OK,"Dialog accepted fractional MHz.");
                numeric.Input.Text="1750";Require(numeric.TryCommit()&&numeric.DialogResult==DialogResult.OK&&form.Draft.Ac.GpuMaximumMHz==1750,"Dialog lost exact MHz.");
            }
            using(var cancelled=new ProductNumericDialog("CPU PL1 (W) · AC",30,8,44,text=>form.TryEditNumericValue("pl1",text,out var error)?null:error))
            {cancelled.Show(form);Application.DoEvents();cancelled.Input.Text="40";cancelled.DialogResult=DialogResult.Cancel;Require(form.Draft.Ac.CpuPl1Watts==30,"Cancelled dialog edited draft.");}
            Require(runtime.Commands==0,"Typed values wrote hardware.");
            var oldCaps=form.Draft.PerformanceConfiguration();
            for(int i=0;i<6;i++)
            {
                var batteryPeer=ProductProfilesStore.Serialize(form.Draft.With(ProductPowerProfile.Ac,ProductProfiles.DefaultProfile(ProductPowerProfile.Ac)));
                form.EditValue("influence-"+i,-999);Require(form.Draft.Ac.Fan.UnifiedDemand!.Influence(i)==(i<2?100:0),"Influence lower bound escaped.");
                form.EditValue("influence-"+i,999);Require(form.Draft.Ac.Fan.UnifiedDemand!.Influence(i)==200,"Influence upper bound escaped.");
                Require(batteryPeer==ProductProfilesStore.Serialize(form.Draft.With(ProductPowerProfile.Ac,ProductProfiles.DefaultProfile(ProductPowerProfile.Ac))),"Influence leaked into Battery.");
            }
            form.HandleCommand("curve-defaults");Require(form.Draft.PerformanceConfiguration()==oldCaps,"Reset of curve/influences changed CPU/GPU caps.");
            form.EditNode(0,90,10);form.EditNode(form.Draft.Ac.Fan.UnifiedDemand!.Curve.Count-1,20,10);
            Require(form.Draft.Ac.Fan.UnifiedDemand!.Curve[0].Input==0&&form.Draft.Ac.Fan.UnifiedDemand!.Curve[^1]==new AdaptiveFanCurvePoint(100,50),"Curve endpoints escaped protection.");
            form.EditNode(3,999,999);form.Draft.Validate();
            canvas.SelectedNode=3;form.HandleCommand("node-add");form.HandleCommand("node-remove");form.Draft.Validate();
            // Keyboard and accessibility gestures use the same draft-only path as the mouse.
            canvas.Page=ProductPage.Curves;canvas.Refresh();canvas.SelectedNode=3;
            var beforeTyping=ProductProfilesStore.Serialize(form.Draft);canvas.HandleKey(Keys.A);
            Require(beforeTyping==ProductProfilesStore.Serialize(form.Draft),"A non-arrow key edited the selected curve.");
            canvas.Page=ProductPage.Performance;canvas.PerformanceTab=0;canvas.Refresh();
            AccessibleObject Child(string id)=>canvas.AccessibilityObject.GetChild(canvas.Hits.ToList().FindIndex(h=>h.Id==id))!;
            var pl1=Child("pl1");pl1.Select(AccessibleSelection.TakeFocus);var prior=form.Draft.Ac.CpuPl1Watts;canvas.HandleKey(Keys.Right);
            Require(form.Draft.Ac.CpuPl1Watts==prior+1&&pl1.Value==(prior+1).ToString(),"Focused slider arrows/accessibility value disagree.");
            pl1.Value="999";Require(form.Draft.Ac.CpuPl1Watts==44&&form.Draft.Ac.CpuPl2Watts>=44,"Accessible slider escaped CPU bounds.");
            bool invalidValueRejected=false;try{pl1.Value="bad";}catch(ArgumentException){invalidValueRejected=true;}Require(invalidValueRejected,"Accessible slider accepted non-numeric input.");
            canvas.Busy=true;var beforeBusy=ProductProfilesStore.Serialize(form.Draft);canvas.HandleKey(Keys.Left);
            bool busyRejected=false;try{pl1.Value="8";}catch(InvalidOperationException){busyRejected=true;}Require(busyRejected&&beforeBusy==ProductProfilesStore.Serialize(form.Draft),"Busy accessibility gesture changed a draft.");canvas.Busy=false;
            canvas.Page=ProductPage.Home;canvas.Refresh();Require(pl1.Bounds==Rectangle.Empty,"Stale accessible child retained an invisible hit area.");
            canvas.Page=ProductPage.Curves;canvas.Refresh();var priorAxis=canvas.ShowCurvePoints;canvas.HandleKey(Keys.Enter);
            Require(canvas.ShowCurvePoints==priorAxis,"A retained focus activated an unrelated control on another page.");
            Require(runtime.Commands==0,"Keyboard or accessibility edited hardware.");
            var now=DateTime.UtcNow.Ticks;Require(PerformanceGuardianClient.IsStatusFresh(now,now-TimeSpan.FromSeconds(5).Ticks,true),"Fresh Guardian response refused.");
            Require(!PerformanceGuardianClient.IsStatusFresh(now,now-TimeSpan.FromSeconds(6).Ticks,true)&&!PerformanceGuardianClient.IsStatusFresh(now,now+1,true)&&!PerformanceGuardianClient.IsStatusFresh(now,now,false),"Stale/future/exited Guardian response remained active.");
            Require(ProductCanvas.DomainColor("Failed",ProductCanvas.Blue)==ProductCanvas.Red&&ProductCanvas.DomainColor("Recovering",ProductCanvas.Green)==ProductCanvas.Yellow,"Failure/recovery colors misrepresent status.");
            var output=Path.GetFullPath("logs/product-gui-self-test");Directory.CreateDirectory(output);
            // Fixture telemetry is explicit and used only by this rendering entry. No hardware reader is constructed.
            var baseline=new ProductProfiles();canvas.Profiles=baseline;canvas.Notice="";canvas.State=runtime.State with
            {
                Hardware="HP Victus 15-fa1xxx (8C40)",Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",
                ManualAuthorized=true,AutomaticAuthorized=false,PerformanceSupported=true,CanApplyPerformance=false,
                FanMode="Manual",FanAuthority="Custom",FanLevel=30,CpuState="Active",GpuState="ActiveUnverified",
                PerformanceActive=true,PerformanceProcessPresent=true,GuardianState="SessionEnabled",
                AppliedPerformanceSource="Ac",AppliedPerformance=new PerformanceGuiSessionConfiguration(),Snapshot=Snapshot(DateTimeOffset.UtcNow,36,48,12,28)
            };
            var confirmed=canvas.State;canvas.State=confirmed with{CpuState="Recovering",GpuState="Failed"};
            Require(canvas.AppliedCpu()=="Sin confirmación actual"&&canvas.AppliedGpu()=="Sin confirmación actual","Loss of confirmation was presented as a completed reset.");
            canvas.State=confirmed with{CpuState="Disabled",GpuState="Disabled"};Require(canvas.AppliedCpu()=="Sin límite aplicado"&&canvas.AppliedGpu()=="Sin límite aplicado","Disabled domains were presented as active.");
            canvas.State=confirmed with{Source="Battery",AppliedPerformanceSource="Ac"};Require(canvas.AppliedCpu()=="35 / 60 W"&&canvas.AppliedGpu()=="210–1850 MHz","A Windows source change fabricated an applied profile transition.");
            canvas.State=confirmed;
            canvas.Editing=ProductPowerProfile.Ac;canvas.SelectedNode=-1;canvas.StartupKnown=true;
            for(int i=300;i>=0;i--){var wave=Math.Sin(i*.13);canvas.AddSnapshot(Snapshot(DateTimeOffset.UtcNow.AddSeconds(-i),60-i*.06+wave*2,50-i*.025+wave,38+wave*12,24+wave*8));}
            void Render(string name,bool renewSnapshot=true)
            {
                if(renewSnapshot)canvas.State=canvas.State with{Snapshot=canvas.State.Snapshot is { } s?s with{Timestamp=DateTimeOffset.UtcNow}:null};
                canvas.Refresh();Application.DoEvents();using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
                bitmap.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
                Require(canvas.Hits.All(h=>h.Bounds.Left>=0&&h.Bounds.Top>=0&&h.Bounds.Right<=1673&&h.Bounds.Bottom<=942),"Hit area outside reference surface.");
                var scale=Math.Min(canvas.Width/1672f,canvas.Height/941f);var ox=(canvas.Width-1672*scale)/2;var oy=(canvas.Height-941*scale)/2;
                Require(canvas.IsHeaderDrag(new((int)(ox+100*scale),(int)(oy+30*scale))),"Scaled header lost window dragging.");
                Require(!canvas.IsHeaderDrag(new((int)(ox+1540*scale),(int)(oy+30*scale))),"Window action was mistaken for dragging.");
                Require(bitmap.Size==canvas.Size,"Render dimensions changed.");
                Require(bitmap.GetPixel(bitmap.Width/2,bitmap.Height/2).A==255,"Render is transparent.");
            }
            foreach(var page in Enum.GetValues<ProductPage>()){canvas.Page=page;canvas.FanTab=0;canvas.PerformanceTab=0;Render("page-"+page);}
            canvas.Page=ProductPage.Advanced;for(int i=0;i<5;i++){canvas.AdvancedTab=i;Render("advanced-tab-"+i);}
            Require(canvas.Hits.All(h=>!h.Id.Contains("MaximumDownStep")&&!h.Id.Contains("MinimumLevel")),"Advanced settings expose controls ignored by the protected physical envelope.");
            canvas.Size=new(1040,660);for(int i=0;i<5;i++){canvas.AdvancedTab=i;Render("advanced-minimum-tab-"+i);}
            canvas.Size=new(1672,941);
            canvas.Page=ProductPage.Fans;for(int i=1;i<=2;i++){canvas.FanTab=i;Render("fans-tab-"+i);}
            Require(canvas.Hits.All(h=>h.Id!="fan-tab-3")&&canvas.Hits.Count(h=>h.Id.StartsWith("fan-tab-"))==3,"Duplicate Curves tab remains in Fans.");
            canvas.Page=ProductPage.Performance;canvas.PerformanceTab=0;Render("performance-exact-CPU");Require(canvas.Hits.Any(h=>h.Id=="pl1-text")&&canvas.Hits.Any(h=>h.Id=="pl2-text"),"CPU numeric inputs inaccessible.");
            canvas.PerformanceTab=1;Render("performance-exact-GPU");Require(canvas.Hits.Any(h=>h.Id=="gpu-text"),"GPU numeric input inaccessible.");
            canvas.PerformanceTab=0;canvas.Profiles=baseline with{Ac=baseline.Ac with{CpuPl1Watts=25,CpuPl2Watts=38}};
            canvas.State=confirmed with{FanMode="Automatic",CanApplyPerformance=true};
            Render("performance-live-pending");Require(canvas.CanApplyPerformance,"Pending live caps did not enable Apply in the rendered surface.");
            canvas.Size=new(1040,660);Render("performance-live-pending-minimum");
            canvas.Size=new(1672,941);canvas.State=canvas.State with{AppliedPerformance=canvas.Profiles.PerformanceConfiguration()};
            Render("performance-live-confirmed");Require(!canvas.CanApplyPerformance,"Confirmed live caps left Apply enabled in the rendered surface.");
            canvas.Profiles=baseline;canvas.State=confirmed;
            using(var numeric=new ProductNumericDialog("GPU máximo (MHz) · AC",1800,210,2500,_=>null))
            {numeric.Show(form);Application.DoEvents();using var bitmap=new Bitmap(numeric.Width,numeric.Height);numeric.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));bitmap.Save(Path.Combine(output,"performance-numeric-dialog.png"),ImageFormat.Png);numeric.DialogResult=DialogResult.Cancel;}
            canvas.Page=ProductPage.Performance;for(int i=1;i<=3;i++){canvas.PerformanceTab=i;Render("performance-tab-"+i);}
            canvas.Page=ProductPage.Monitoring;for(int i=1;i<=3;i++){canvas.MonitorTab=i;Render("monitor-tab-"+i);}
            canvas.Page=ProductPage.Curves;foreach(var size in new[]{new Size(1040,660),new Size(1254,706),new Size(1920,1080),new Size(2508,1412),new Size(3344,1882)})
            {canvas.Size=size;Require(canvas.Size==size,"Requested layout was clamped.");Render("layout-"+size.Width+"x"+size.Height);}
            Require(runtime.Commands==0,"Rendering wrote hardware.");
            canvas.Size=new(1672,941);canvas.Page=ProductPage.Home;
            foreach(var state in new[]{"Unsupported","Recovering","Failed"}){canvas.State=canvas.State with{Runtime=state,CpuState=state,GpuState=state,PerformanceActive=false,CanApplyPerformance=false};Render("state-"+state);}
            canvas.Page=ProductPage.Performance;canvas.State=canvas.State with{Runtime="Healthy",CpuState="Active",GpuState="Failed",PerformanceActive=false};Render("state-partial-CPU-active-GPU-failed");
            form.HandleCommand("fan-mode-1");form.HandleCommand("manual-apply");form.HandleCommand("firmware");form.HandleCommand("performance-release");
            Require(runtime.Commands==4&&runtime.LastManualLevel==10,"Explicit Manual Apply did not dispatch level 10 through its separate contract.");
            form.HandleCommand("curve-simulator");var unedited=ProductProfilesStore.Serialize(form.Draft);var commands=runtime.Commands;
            form.EditValue("sim-input-2",40);form.EditValue("sim-input-3",110);form.EditValue("sim-input-4",100);form.HandleCommand("sim-1200");
            Require(canvas.Simulation.ElapsedSeconds==1200&&unedited==ProductProfilesStore.Serialize(form.Draft)&&runtime.Commands==commands,"Simulation acquired authority or edited profiles.");
            form.HandleCommand("profile-battery");Require(canvas.Simulation.ElapsedSeconds==0,"Simulation mixed profile histories.");
            canvas.Page=ProductPage.Curves;canvas.Refresh();canvas.HandleKey(Keys.End);canvas.HandleKey(Keys.Up);Require(unedited==ProductProfilesStore.Serialize(form.Draft),"Simulation keys edited hidden curve nodes.");
            form.HandleCommand("sim-60");form.HandleCommand("sim-reset");Require(canvas.Simulation.ElapsedSeconds==0,"Simulation reset failed.");
            RunLifecycleFixtures(Require);
            RunFailureAndPersistenceFixtures(Require);
            canvas.State=confirmed with{Snapshot=confirmed.Snapshot! with{Timestamp=DateTimeOffset.UtcNow.AddMinutes(-1)}};
            canvas.Page=ProductPage.Monitoring;Render("state-stale-telemetry",false);Require(canvas.CurrentSnapshot is null,"Expired telemetry appeared as current.");
            canvas.State=confirmed with{Snapshot=confirmed.Snapshot! with{CpuPackagePowerW=null,GpuPowerW=null,CpuFanRpm=null,GpuFanRpm=null}};Render("state-missing-metrics");
            canvas.Page=ProductPage.Curves;canvas.SimulationVisible=false;canvas.ShowCurvePoints=false;Render("curve-influences");Require(canvas.Hits.Count(h=>h.Id.StartsWith("influence-")&&h.Slider)==6&&canvas.Hits.All(h=>!h.Id.StartsWith("axis-")),"Influence controls missing or obsolete axis buttons remain.");canvas.ShowCurvePoints=true;canvas.SelectedNode=5;Render("curve-selected-node");
            var expandedInfluences=baseline.Ac.Fan.UnifiedDemand!;
            for(int i=0;i<6;i++)expandedInfluences=expandedInfluences.WithInfluence(i,200);
            canvas.Profiles=baseline with{Ac=baseline.Ac with{Fan=baseline.Ac.Fan with{UnifiedDemand=expandedInfluences}}};canvas.Editing=ProductPowerProfile.Ac;canvas.ShowCurvePoints=false;
            Render("curve-influences-200");Require(canvas.Hits.Count(h=>h.Id.StartsWith("influence-")&&h.Slider&&h.Max==200)==6,"Rendered influence sliders did not expose 200 percent.");
            canvas.Size=new(1040,660);Render("curve-influences-200-minimum");canvas.Size=new(1672,941);canvas.Profiles=baseline;
            canvas.Editing=ProductPowerProfile.Battery;canvas.ShowCurvePoints=true;Render("curve-battery-unified");
            canvas.SimulationVisible=true;canvas.Editing=ProductPowerProfile.Ac;canvas.Simulation=new(baseline.Ac.Fan);canvas.SimulationInputs=new(80,70,40,110,100,100);canvas.Simulation.Advance(new(),1);canvas.Simulation.Advance(canvas.SimulationInputs,1201);Render("curve-simulator-sustained-load");
            canvas.Size=new(1040,660);Render("curve-simulator-minimum-layout");
            canvas.Size=new(1672,941);canvas.SimulationVisible=false;canvas.Editing=ProductPowerProfile.Ac;canvas.ShowCurvePoints=true;
            var markerSample=Snapshot(DateTimeOffset.UtcNow,75,68,25,100);
            var markerConfig=baseline.Ac.Fan with{Tuning=baseline.Ac.Fan.Tuning with{CpuTemperatureSource=CpuDemandTemperatureSource.PackageOrHottestCore}};
            canvas.Profiles=baseline with{Ac=baseline.Ac with{Fan=markerConfig}};
            canvas.State=confirmed with{FanMode="Automatic",FanAuthority="Custom",FanLevel=31,AppliedFanProfile="Ac",AppliedAutomaticConfiguration=markerConfig,
                Snapshot=markerSample,AutomaticDecisionSnapshot=markerSample,AutomaticDecision=new(AdaptiveFanProductionMode.Automatic,AdaptiveFanProductionActionKind.HoldCustom,true,31,35,FanAuthority.Custom,"fixture")};
            var curveMarkers=canvas.CurrentDemandMarkers();
            Require(curveMarkers.Count==2&&curveMarkers.Single(m=>m.IsApplied).Level==31,"Accepted request marker was replaced by draft interpolation.");
            canvas.Page=ProductPage.Fans;canvas.FanTab=0;Render("fans-live-demand-marker");
            var markerState=canvas.State;
            canvas.State=markerState with{AutomaticReview=true,AutomaticReviewMaximumSeconds=2700,AutomaticReviewRemainingSeconds=2633,
                AutomaticDecision=markerState.AutomaticDecision! with{ObservedLoadSeconds=0,SustainedLoadCooling=false}};
            Render("fans-review-brief-layout");canvas.Size=new(1040,660);Render("fans-review-brief-minimum-layout");
            canvas.State=canvas.State with{AutomaticDecision=canvas.State.AutomaticDecision! with{ObservedLoadSeconds=1815.785,SustainedLoadCooling=true}};
            Render("fans-review-sustained-minimum-layout");canvas.Size=new(1672,941);Render("fans-review-sustained-layout");
            canvas.State=markerState;
            var interruption=markerSample with{CpuTemperatureC=96,CpuCoreTemperatures=[new(0,0,"Performance",98)]};
            canvas.State=markerState with{FanMode="Firmware",FanAuthority="Firmware",LifecycleBlocked=true,
                LifecycleBlockReason=ProductAutomaticReview.CpuSpikeDeadlineFailure,AutomaticInterruptionSnapshot=interruption,
                Failure=null,Message="Telemetría recuperada."};
            Require(canvas.State.InterruptionDetails!.Contains(ProductAutomaticReview.CpuSpikeDeadlineFailure)&&
                canvas.State.InterruptionDetails.Contains("CPU 98 °C")&&canvas.State.InterruptionDetails.Contains("Salir desde la bandeja"),
                "Later healthy telemetry hid the interruption or its clean-exit guidance.");
            canvas.Page=ProductPage.Fans;Render("fans-interrupted-layout");
            Require(canvas.Hits.Single(h=>h.Id=="interruption-details").Enabled&&canvas.Hits.Where(h=>h.Id is "fan-mode-1" or "fan-mode-2").All(h=>!h.Enabled),
                "Interruption details reopened hardware control or were inaccessible.");
            canvas.Size=new(1040,660);Render("fans-interrupted-minimum-layout");
            canvas.Page=ProductPage.Settings;Render("settings-interrupted-minimum-layout");canvas.Size=new(1672,941);Render("settings-interrupted-layout");
            canvas.State=markerState;
            canvas.Page=ProductPage.Curves;Render("editor-live-demand-marker");
            canvas.Profiles=canvas.Profiles with{Ac=canvas.Profiles.Ac with{Fan=markerConfig with{UnifiedDemand=markerConfig.UnifiedDemand! with{Curve=[new(0,10),new(90,10),new(100,50)]}}}};
            var changedMarkers=canvas.CurrentDemandMarkers();
            Require(changedMarkers.Single(m=>m.IsApplied)==curveMarkers.Single(m=>m.IsApplied)&&changedMarkers.Single(m=>!m.IsApplied).Level!=curveMarkers.Single(m=>!m.IsApplied).Level,"Editing changed the accepted request or left preview frozen.");
            Render("editor-draft-vs-applied-marker");
            canvas.Editing=ProductPowerProfile.Battery;Require(canvas.CurrentDemandMarkers().All(m=>!m.IsApplied),"Other profile displayed a fabricated applied request.");
            canvas.Editing=ProductPowerProfile.Ac;canvas.State=canvas.State with{Snapshot=markerSample with{Timestamp=DateTimeOffset.UtcNow.AddMinutes(-1)}};
            Require(canvas.CurrentDemandMarkers().Count==0,"Stale telemetry left moving markers visible.");
            canvas.State=canvas.State with{Snapshot=markerSample,AutomaticDecisionSnapshot=markerSample with{Timestamp=DateTimeOffset.UtcNow.AddMinutes(-1)}};
            Require(canvas.CurrentDemandMarkers().All(m=>!m.IsApplied),"Stale decision appeared as an accepted current request.");
            canvas.State=canvas.State with{FanMode="Firmware",FanAuthority="Firmware"};
            Require(canvas.CurrentDemandMarkers().All(m=>!m.IsApplied),"Firmware displayed a custom applied request.");

            Console.WriteLine("PASS: product GUI draft isolation, editing/navigation without authority, sliders, nodes, closed gates and real Windows renders.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void TestSessionLogs(Action<bool,string> require)
    {
        require(!AppLog.QualificationCompatibilityLogEnabled,"Product GUI opted into legacy daily logs.");
        AppLog.Initialize();var id=AppLog.SessionId;var path=AppLog.CurrentLogPath;AppLog.Initialize();
        require(id==AppLog.SessionId&&path==AppLog.CurrentLogPath&&Path.GetDirectoryName(path)==AppLog.SessionDirectory,"Log identity changed within an application session.");
        var directory=Path.Combine(Path.GetTempPath(),"vfc-log-tail-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var log=Path.Combine(directory,"events.log");
            File.WriteAllText(log+".1","old row\nolder retained row\n");File.WriteAllText(log,"current ñ row\nlast row\n");
            var tail=AppLog.ReadTail(log,36);
            require(tail=="current ñ row\nlast row\n","Rotated tail retained a partial line or lost current UTF-8 rows.");
            File.WriteAllText(Path.Combine(directory,"other-session.log"),"unrelated session\n");
            require(!AppLog.ReadTail(log).Contains("unrelated session"),"Log export mixed other sessions.");
            File.WriteAllText(log,"complete\npartial");File.Delete(log+".1");
            require(AppLog.ReadTail(log)=="complete\n","Concurrent partial JSON row entered diagnostic export.");
            var sample=Snapshot(DateTimeOffset.UtcNow,40,35,5,0) with{GpuPowerW=double.NaN};AppLog.WriteTelemetry(sample);
            var rows=AppLog.ReadTail(AppLog.TelemetryLogPath).Split('\n',StringSplitOptions.RemoveEmptyEntries);
            using var row=System.Text.Json.JsonDocument.Parse(rows[^1]);
            require(row.RootElement.GetProperty("sessionId").GetString()==id&&row.RootElement.GetProperty("snapshot").GetProperty("GpuPowerW").GetString()=="NaN","Telemetry lost session identity or invalid values.");
        }
        finally{Directory.Delete(directory,true);}
    }
    private static void TestAutomaticReview(Action<bool,string> require)
    {
        require(ProductRuntime.PerformanceAdmissionPermitted(false,false,false,SystemState.Healthy,FanAuthority.Custom)&&ProductRuntime.PerformanceAdmissionPermitted(false,false,false,SystemState.Healthy,FanAuthority.Firmware),"Independent CPU/GPU session requires an unrelated fan mode.");
        require(!ProductRuntime.PerformanceAdmissionPermitted(false,false,false,SystemState.Healthy,FanAuthority.Restoring)&&!ProductRuntime.PerformanceAdmissionPermitted(false,false,false,SystemState.Healthy,FanAuthority.Faulted)&&!ProductRuntime.PerformanceAdmissionPermitted(false,true,false,SystemState.Healthy,FanAuthority.Custom)&&!ProductRuntime.PerformanceAdmissionPermitted(false,false,true,SystemState.Healthy,FanAuthority.Custom)&&!ProductRuntime.PerformanceAdmissionPermitted(false,false,false,SystemState.Degraded,FanAuthority.Custom),"CPU/GPU session escaped a failure/lifecycle/existing-owner fence.");
        require(TelemetryWorker.IsPlannedFanReleaseRead(new WmiFanReadAdmissionPausedException(true)),"Planned native read denial was treated as sensor failure.");
        require(!TelemetryWorker.IsPlannedFanReleaseRead(new WmiFanReadAdmissionPausedException(false))&&!TelemetryWorker.IsPlannedFanReleaseRead(new IOException("native failed")),"Unexpected native fault was hidden by planned release handling.");
        var target=Hp8C40TargetProfile.Instance.Id;
        require(!Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized,"Review promoted the normal Automatic gate.");
        require(!ProductAutomaticReview.IsAuthorized(false,target)&&!ProductAutomaticReview.IsAuthorized(true,"Unsupported")&&ProductAutomaticReview.IsAuthorized(true,target),"Review target/explicit admission failed.");
        long clock=1000;var review=new ProductAutomaticReview(()=>clock);
        var snapshot=Snapshot(DateTimeOffset.UtcNow,40,35,5,5) with{CpuExpectedPhysicalCoreCount=1};
        SafetyGateResult Safety(TelemetrySnapshot s)=>new(true,true,true,true,true,true,false,true,true,true,s.Timestamp,s.Timestamp,1,Array.Empty<string>());
        void Rejected(Action action,string message){bool failed=false;try{action();}catch(InvalidOperationException){failed=true;}require(failed,message);}
        Rejected(()=>review.EnsureDispatchAllowed(snapshot),"Inactive review admitted a dispatch.");
        review.Start();
        Rejected(()=>review.Observe(snapshot,Safety(snapshot) with{EvaluationSequence=0}),"Presentation-only safety admitted review.");
        require(review.RemainingSeconds==300&&!review.Expired,"Review did not use its activation clock.");
        require(!review.Observe(snapshot,Safety(snapshot)),"Review wrote after only one healthy sample.");
        snapshot=snapshot with{Timestamp=snapshot.Timestamp.AddSeconds(1)};require(!review.Observe(snapshot,Safety(snapshot)),"Review wrote after only two healthy samples.");
        snapshot=snapshot with{Timestamp=snapshot.Timestamp.AddSeconds(1)};require(review.Observe(snapshot,Safety(snapshot)),"Three unique healthy samples did not admit review.");
        Rejected(()=>review.Observe(snapshot,Safety(snapshot)),"Duplicate acquisition admitted.");
        Rejected(()=>review.EnsureDispatchAllowed(snapshot with{CpuPackagePowerW=61}),"CPU power envelope escaped.");
        Rejected(()=>review.EnsureDispatchAllowed(snapshot with{GpuTemperatureC=83}),"GPU thermal envelope escaped.");
        Rejected(()=>review.EnsureDispatchAllowed(snapshot with{CpuCoreTemperatures=[new(0,0,"Performance",91)]}),"Hottest-core envelope escaped.");
        Rejected(()=>review.EnsureDispatchAllowed(snapshot with{GpuPowerW=double.NaN}),"Nonfinite power admitted.");
        require(ProductAutomaticReview.EnvelopeFailure(snapshot with{CpuPackagePowerW=61}).Contains("CPU potencia 61 W > 60 W") &&
            !ProductAutomaticReview.EnvelopeFailure(snapshot with{CpuPackagePowerW=61}).Contains("GPU temperatura"),"Interruption reason did not identify the actual failed metric.");
        require(ProductAutomaticReview.EnvelopeFailure(snapshot with{GpuPowerW=double.NaN}).Contains("GPU potencia no disponible"),"Invalid telemetry lost its interruption cause.");
        clock+=299999;require(!review.Expired,"Review expired before deadline.");clock++;
        require(review.Expired&&review.RemainingSeconds==0,"Review deadline escaped.");
        Rejected(()=>review.EnsureDispatchAllowed(snapshot),"Expired review dispatched.");
        review.Stop();require(review.RemainingSeconds is null,"Stopped review retained a deadline.");
        review.Start();clock--;require(review.Expired,"Backwards clock admitted review.");
        // The product review consumes the controller's effective safety, not a
        // raw thermal-only veto. Startup still requires three cool acquisitions.
        clock=0; review=new ProductAutomaticReview(()=>clock); review.Start();
        var origin=snapshot.Timestamp;
        TelemetrySnapshot Sample(int ms,double cpu=63)=>snapshot with{Timestamp=origin.AddMilliseconds(ms),CpuTemperatureC=cpu,
            CpuCoreTemperatures=[new(0,0,"Performance",cpu)]};
        var hotStart=Sample(0,96);
        Rejected(()=>review.Observe(hotStart,Safety(hotStart)),"High CPU startup admitted review.");
        for(var i=0;i<3;i++){clock=i*100;var cool=Sample(i*100);review.Observe(cool,Safety(cool));}
        clock=300; var spike=Sample(300,96);
        require(review.Observe(spike,Safety(spike))&&review.RemainingCpuSpikeMilliseconds==2000,"Isolated CPU spike cancelled established review.");
        clock=800;review.EnsureDispatchAllowed(spike);review.EnsureDispatchAllowed(spike);
        require(review.RemainingCpuSpikeMilliseconds==1500,"Dispatch previews renewed the CPU spike deadline.");
        Rejected(()=>review.Observe(spike,Safety(spike)),"Repeated high acquisition counted as new.");
        clock=1400;var recovered=Sample(1400,94.9);require(review.Observe(recovered,Safety(recovered))&&review.RemainingCpuSpikeMilliseconds is null,"Timely acquisition below the core emergency threshold failed to clear spike.");
        clock=1500;var coreSpike=Sample(1500,60) with{CpuCoreTemperatures=[new(0,0,"Performance",97)]};
        require(review.Observe(coreSpike,Safety(coreSpike)),"Hottest-core spike was not admitted for confirmation.");
        clock=2500;var sustained=Sample(2500,95);require(review.Observe(sustained,Safety(sustained)),"Exact 95 C sample escaped the bounded confirmation path.");
        require(review.RemainingCpuSpikeMilliseconds==1000,"Another high sample reset the deadline.");
        clock=3500;
        Rejected(()=>review.EnsureDispatchAllowed(sustained),"Sustained CPU heat admitted dispatch at deadline.");
        var lateCool=Sample(3500,62);Rejected(()=>review.Observe(lateCool,Safety(lateCool)),"Late cool acquisition rescued expired spike admission.");
        // Regression from bf445702: 92, 93, 93 C caused a GUI-only 90 C deadline
        // while core admission remained open. Fresh readings below 95 C must not
        // create an acquisition budget or erase the activation's duration limit.
        review.Start();clock=4000;
        for(var i=0;i<3;i++){var cool=Sample(4000+i*100);review.Observe(cool,Safety(cool));}
        for(var i=0;i<8;i++)
        {
            clock=4300+i*700;var warm=Sample((int)clock,i==0?92:i==7?94.9:93);
            require(review.Observe(warm,Safety(warm))&&review.RemainingCpuSpikeMilliseconds is null,
                "Fresh established CPU below 95 C recreated the obsolete 90 C deadline.");
        }
        require(review.RemainingSeconds<300,"Warm CPU samples renewed the activation deadline.");
        var independentGpuFault=Sample((int)clock+100,93) with{GpuTemperatureC=83};
        Rejected(()=>review.Observe(independentGpuFault,Safety(independentGpuFault)),"Warm CPU concealed an independent GPU envelope fault.");
        review.Start();for(var i=0;i<3;i++){var cool=Sample(4000+i*100);review.Observe(cool,Safety(cool));}
        foreach(var critical in new[]{Sample(4300,99),Sample(4300,60) with{CpuCoreTemperatures=[new(0,0,"Efficiency",99)]}})
            Rejected(()=>review.Observe(critical,Safety(critical)),"CPU >=99 package/hottest-core did not hand off immediately.");
        var badSafety=Sample(4300,96);
        Rejected(()=>review.Observe(badSafety,Safety(badSafety) with{SnapshotFresh=false,CustomControlPermitted=false}),"Stale high acquisition entered spike admission.");
        require(ProductAutomaticReview.AcquisitionBudget(1200,700)==700&&ProductAutomaticReview.AcquisitionBudget(null,700)==700&&
            ProductAutomaticReview.AcquisitionBudget(1200,null)==1200&&ProductAutomaticReview.AcquisitionBudget(null,null) is null,"Acquisition budget extended a thermal deadline.");
        // Regression from session e618184c: package 98 C is below 99 C,
        // but the hottest core is 100 C and effective admission has already closed.
        var actualCritical=Sample(4300,98) with{CpuCoreTemperatures=[new(0,0,"Performance",100)]};
        string Failure(Action action){try{action();throw new Exception("Expected review rejection.");}catch(InvalidOperationException ex){return ex.Message;}}
        var criticalReason=Failure(()=>review.Observe(actualCritical,Safety(actualCritical) with{CustomControlPermitted=false,ThermalEmergency=true}));
        require(criticalReason.Contains("control 100 °C >= 99 °C")&&criticalReason.Contains("paquete 98 °C")&&
            criticalReason.Contains("núcleo más caliente 100 °C")&&criticalReason.Contains("Firmware")&&!criticalReason.Contains("Healthy"),
            "Effective critical admission hid the actual package/hottest-core cause.");
        var epochReason=Failure(()=>review.Observe(actualCritical,Safety(actualCritical) with{SnapshotTimestamp=actualCritical.Timestamp.AddSeconds(-1),CustomControlPermitted=false,ThermalEmergency=true}));
        require(epochReason.Contains("misma adquisición")&&!epochReason.Contains("control 100"),"Mismatched safety reported an unrelated thermal cause.");
        var staleReason=Failure(()=>review.Observe(badSafety,Safety(badSafety) with{SnapshotFresh=false,CustomControlPermitted=false,Reasons=["Telemetry is stale."]}));
        require(staleReason.Contains("Telemetry is stale."),"SafetyGate rejection discarded its underlying reasons.");
        var gpuWhileCpuPending=ProductAutomaticReview.EnvelopeFailure(Sample(4400,96) with{GpuTemperatureC=83},cpuSpikeAdmitted:true);
        require(gpuWhileCpuPending.Contains("GPU temperatura 83 °C > 82 °C")&&!gpuWhileCpuPending.Contains("CPU temperatura"),
            "Admitted CPU spike was falsely blamed for an independent GPU rejection.");
        // Explicit review still starts in Firmware, and editing/saving never dispatches a curve.
        var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://review",fixture:runtime,fixtureProfiles:new ProductProfiles(),automaticReview:ProductAutomaticReviewMode.Short);
        form.Show();Application.DoEvents();require(runtime.Commands==0&&form.Canvas.State.FanMode=="Firmware","Review auto-started control.");
        runtime.Publish(runtime.State with{AutomaticAuthorized=true,AutomaticReview=true,Runtime="Healthy"});
        form.HandleCommand("fan-mode-2");PumpUntil(()=>!form.Canvas.Busy,"Review mode command did not finish.");require(runtime.Commands==1,"Explicit Automatic click was not dispatched once.");
        runtime.Publish(runtime.State with{AutomaticAuthorized=true,AutomaticReview=true,Runtime="Healthy",FanMode="Automatic"});
        form.HandleCommand("fan-mode-2");require(runtime.Commands==1&&form.Canvas.Notice.Contains("no se renueva"),"Repeated Automatic click reapplied control or lost its bounded-review notice.");
        form.EditNode(3,70,35);require(runtime.Commands==1,"Curve editing dispatched hardware in review.");
        runtime.Publish(runtime.State with{AutomaticAuthorized=true,LifecycleBlocked=true});form.HandleCommand("fan-mode-2");require(runtime.Commands==1,"Interrupted review rearmed from GUI.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Review fixture shutdown failed.");exit.GetAwaiter().GetResult();
        var pendingPerformance=new RecordingRuntime { PerformanceGate=new(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var coexist=new ProductForm("fixture://coexist",fixture:pendingPerformance,fixtureProfiles:new ProductProfiles());
        coexist.Show();Application.DoEvents();pendingPerformance.Publish(pendingPerformance.State with{Runtime="Healthy",FanMode="Automatic",FanAuthority="Custom",PerformanceSupported=true,CanApplyPerformance=true});
        coexist.HandleCommand("performance-apply");require(coexist.Canvas.Busy&&pendingPerformance.Commands==1,"Slow Performance Apply fixture did not start.");
        coexist.HandleCommand("firmware");require(pendingPerformance.Commands==2&&coexist.Canvas.Busy,"Pending Performance Apply blocked the Firmware escape.");
        pendingPerformance.PerformanceGate.SetResult();PumpUntil(()=>!coexist.Canvas.Busy,"Independent operation did not drain.");
        var coexistExit=coexist.RequestExitAsync();PumpUntil(()=>coexistExit.IsCompleted,"Coexistence fixture failed to close.");coexistExit.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> completed,string failure)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!completed()){if(timer.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException(failure);Application.DoEvents();Thread.Sleep(1);}
        Application.DoEvents();
    }
    private static void RunLifecycleFixtures(Action<bool,string> require)
    {
        // Exercise the production P/Invoke bindings against a real HWND, with no hardware runtime.
        for(int i=0;i<3;i++)
        {
            var native=new RecordingRuntime();using var form=new ProductForm("fixture://modules",fixture:native,fixtureProfiles:new ProductProfiles(),registerPowerNotificationsInFixture:true);
            form.Show();PumpUntil(()=>native.Starts==1||form.Canvas.State.Failure is not null,"Native power-notification startup did not complete.");
            require(native.Starts==1&&form.Canvas.State.Failure is null,"Native power notifications prevented startup: "+form.Canvas.State.Failure);
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Native power-notification shutdown failed.");exit.GetAwaiter().GetResult();
            require(native.Disposals==1&&native.Commands==0,"Native power-notification fixture leaked its runtime or applied hardware.");
        }
        var broken=new RecordingRuntime{StartFailure="fixture startup failure"};
        using(var form=new ProductForm("fixture://modules",fixture:broken,fixtureProfiles:new ProductProfiles()))
        {
            form.Show();PumpUntil(()=>broken.Releases==1,"Incomplete startup did not release its lifecycle boundary.");
            require(broken.Fences==1&&form.Canvas.State.Failure?.Contains("fixture startup failure")==true,"Startup failure was not preserved in the diagnostic state.");
            broken.Publish(broken.State);require(form.Canvas.State.Failure?.Contains("fixture startup failure")==true,"A later presentation update erased startup failure.");
            var dir=Path.Combine(Path.GetTempPath(),"vfc-startup-diagnostic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try
            {
                var path=Path.Combine(dir,"diagnostic.zip");var export=form.ExportDiagnosticsAsync(path);PumpUntil(()=>export.IsCompleted,"Startup failure export did not finish.");export.GetAwaiter().GetResult();
                using var zip=System.IO.Compression.ZipFile.OpenRead(path);using var reader=new StreamReader(zip.GetEntry("gui-state.json")!.Open());
                using var state=System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());require(state.RootElement.GetProperty("Failure").GetString()?.Contains("fixture startup failure")==true,"Export omitted startup failure.");
            }
            finally{Directory.Delete(dir,true);}
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed-startup fixture shutdown failed.");require(broken.Disposals==1&&broken.Commands==0,"Failed startup acquired authority or leaked its runtime.");
        }
        Console.WriteLine("PASS: real Windows suspend/display registration and unregister, three cycles, startup failure retained in diagnostic ZIP, zero hardware commands.");
        using(var reverse=new ProductForm("fixture://modules",fixture:new RecordingRuntime(),fixtureProfiles:new ProductProfiles()))
        {
            reverse.Show();Application.DoEvents();reverse.Canvas.Focus();reverse.Canvas.Refresh();reverse.Canvas.HandleKey(Keys.Shift|Keys.Tab);
            var last=reverse.Canvas.Hits.ToList().FindLastIndex(h=>h.Enabled);
            require((reverse.Canvas.AccessibilityObject.GetChild(last)!.State&AccessibleStates.Focused)!=0,"Initial Shift+Tab skipped the last control.");
            var exit=reverse.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Reverse-tab fixture shutdown failed.");
        }
        var ready=new TaskCompletionSource<IProductRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);var late=new RecordingRuntime();
        using(var pending=new ProductForm("fixture://modules",fixtureProfiles:new ProductProfiles(),runtimeFactory:()=>ready.Task))
        {
            pending.Show();Application.DoEvents();var exit=pending.RequestExitAsync();require(!exit.IsCompleted,"Exit abandoned pending service construction.");
            ready.SetResult(late);PumpUntil(()=>exit.IsCompleted,"Delayed startup/exit did not complete.");exit.GetAwaiter().GetResult();
            require(late.Starts==0&&late.Disposals==1&&late.Commands==0,"Delayed startup acquired authority or leaked the service.");
        }
        var runtime=new RecordingRuntime{ReleaseGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using(var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles()))
        {
            form.Show();Application.DoEvents();var off=form.HandlePowerEventAsync(0x8013,0);
            require(runtime.Fences==1,"Display Off did not fence synchronously.");PumpUntil(()=>runtime.Releases==1,"First lifecycle release did not start.");
            var suspend=form.HandlePowerEventAsync(4);require(runtime.Fences==2&&!suspend.IsCompleted&&runtime.Releases==1,"Suspend failed to serialize pending releases.");
            form.HandlePowerEventAsync(18).GetAwaiter().GetResult();require(runtime.Resumes==0,"Maintenance resume reopened telemetry with display Off.");
            var on=form.HandlePowerEventAsync(0x8013,1);require(!on.IsCompleted&&runtime.Resumes==0,"Display On resumed before release.");
            runtime.ReleaseGate.SetResult();PumpUntil(()=>on.IsCompleted&&suspend.IsCompleted,"Lifecycle queue did not finish.");on.GetAwaiter().GetResult();off.GetAwaiter().GetResult();suspend.GetAwaiter().GetResult();
            require(runtime.Releases==2&&runtime.Resumes==1,"Lifecycle boundaries were dropped or resumed more than once.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Fixture shutdown did not complete.");require(runtime.Disposals==1,"Exit did not dispose its runtime exactly once.");
        }
        var busy=new RecordingRuntime{ManualGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using(var form=new ProductForm("fixture://modules",fixture:busy,fixtureProfiles:new ProductProfiles()))
        {
            form.Show();Application.DoEvents();form.Canvas.State=busy.State with{ManualAuthorized=true,Runtime="Healthy",FanMode="Manual",PerformanceSupported=true,CanApplyPerformance=true};
            form.HandleCommand("fan-mode-1");require(form.Canvas.Busy,"Pending Manual contract did not mark UI busy.");
            form.HandleCommand("manual-apply");form.HandleCommand("performance-apply");require(busy.Commands==1,"Busy surface dispatched another Apply.");
            form.HandleCommand("firmware");require(busy.Commands==2&&form.Canvas.Busy,"Firmware became unavailable or cleared a pending operation.");
            busy.ManualGate.SetResult();PumpUntil(()=>!form.Canvas.Busy,"Busy counter did not drain.");
            form.Canvas.State=busy.State with{ManualAuthorized=false,Runtime="Failed"};form.HandleCommand("fan-mode-1");form.HandleCommand("manual-apply");
            require(busy.Commands==2,"Disabled/Failed stale action dispatched a fan command.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Busy fixture shutdown failed.");
        }
        Console.WriteLine("PASS: product startup/exit race, serialized lifecycle releases, display-Off resume fence, busy Apply admission and stale Guardian status.");
    }
    private static void RunFailureAndPersistenceFixtures(Action<bool,string> require)
    {
        var dir=Path.Combine(Path.GetTempPath(),"vfc-product-gui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var runtime=new RecordingRuntime();var path=Path.Combine(dir,"profiles.json");
            using(var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles(),profilesPath:path))
            {
                form.Show();Application.DoEvents();var canvas=form.Canvas;
                var original=ProductProfilesStore.Serialize(form.Draft);
                foreach(var source in new[]{"profile-ac","profile-battery"})
                foreach(var axis in Enum.GetValues<AdaptiveCurveAxis>())
                {
                    form.HandleCommand(source);canvas.ShowCurvePoints=true;form.EditValue("influence-"+(int)axis,axis<AdaptiveCurveAxis.CpuPower?130:35);var slot=source=="profile-ac"?ProductPowerProfile.Ac:ProductPowerProfile.Battery;
                    var peer=ProductProfilesStore.Serialize(form.Draft.With(slot,ProductProfiles.DefaultProfile(slot)));
                    form.EditNode(0,-999,-999);form.EditNode(1,999,999);form.Draft.Validate();
                    var afterPeer=ProductProfilesStore.Serialize(form.Draft.With(slot,ProductProfiles.DefaultProfile(slot)));
                    require(peer==afterPeer,"Curve editing changed its peer profile.");
                    form.HandleCommand("node-add");form.HandleCommand("node-remove");form.HandleCommand("curve-reset");form.Draft.Validate();
                }
                form.HandleCommand("discard");require(!form.Dirty&&original==ProductProfilesStore.Serialize(form.Draft),"Discard did not restore the saved draft.");
                form.HandleCommand("profile-ac");canvas.Page=ProductPage.Curves;canvas.ShowCurvePoints=true;
                for(int i=0;i<70;i++)form.HandleCommand("node-add");
                require(form.Draft.Ac.Fan.UnifiedDemand!.Curve.Count==64,"Curve count cap is inaccessible or not enforced.");
                canvas.Refresh();canvas.HandleKey(Keys.End);canvas.Refresh();
                require(canvas.SelectedNode==63&&canvas.Hits.Any(h=>h.Id=="node-63"),"Last of 64 nodes is not reachable by keyboard/table.");
                canvas.HandleKey(Keys.Home);canvas.Refresh();require(canvas.SelectedNode==0,"Home failed to select first node.");
                form.HandleCommand("node-next");canvas.Refresh();require(canvas.SelectedNode==1,"Node pager did not move selection.");
                form.HandleCommand("discard");form.HandleCommand("profile-ac");form.EditValue("pl1",36);form.HandleCommand("save");
                PumpUntil(()=>!canvas.Busy,"Isolated profile save did not complete.");
                require(!form.Dirty&&File.Exists(path)&&ProductProfilesStore.Load(path,out _).Ac.CpuPl1Watts==36,"Saved preferences were not persisted.");
                form.EditValue("pl1",37);form.HandleCommand("discard");require(form.Draft.Ac.CpuPl1Watts==36&&!form.Dirty,"Discard lost the latest successful save.");
                canvas.Page=ProductPage.Performance;canvas.PerformanceTab=0;canvas.Refresh();
                Point ScreenPoint(float x,float y){var scale=Math.Min(canvas.Width/1672f,canvas.Height/941f);return new((int)((canvas.Width-1672*scale)/2+x*scale),(int)((canvas.Height-941*scale)/2+y*scale));}
                var slider=canvas.Hits.Last(h=>h.Id=="pl1");var left=ScreenPoint(slider.Bounds.Left+2,slider.Bounds.Top+20);var right=ScreenPoint(slider.Bounds.Right-2,slider.Bounds.Top+20);var beyond=ScreenPoint(slider.Bounds.Right+50,slider.Bounds.Top+20);
                canvas.PointerDown(left);require(canvas.Capture,"Pointer fixture did not acquire slider capture.");canvas.PointerMove(beyond);canvas.PointerUp(beyond);require(form.Draft.Ac.CpuPl1Watts==44,"Pointer slider failed its upper bound: "+form.Draft.Ac.CpuPl1Watts);
                canvas.Refresh();canvas.PointerDown(right);form.HandleCommand("profile-battery");canvas.Refresh();var priorBattery=form.Draft.Battery.CpuPl1Watts;canvas.PointerMove(left);canvas.PointerUp(left);
                require(form.Draft.Battery.CpuPl1Watts==priorBattery,"A captured drag leaked into a newly selected profile.");
                form.HandleCommand("profile-ac");canvas.Page=ProductPage.Curves;canvas.ShowCurvePoints=true;canvas.Refresh();
                var point=form.Draft.Ac.Fan.UnifiedDemand!.Curve[3];var from=ScreenPoint(958+(float)(point.Input/100)*634,648-(float)((point.Level-10)/40)*350);var to=ScreenPoint(958+(float)(71d/100)*634,648);
                canvas.PointerDown(from);canvas.PointerMove(to);canvas.PointerUp(to);require(form.Draft.Ac.Fan.UnifiedDemand!.Curve[3].Input==71,"Pointer node drag failed.");
                form.HandleCommand("discard");
                canvas.Refresh();var modeDraft=ProductProfilesStore.Serialize(form.Draft);var modePoint=form.Draft.Ac.Fan.UnifiedDemand!.Curve[3];
                var modeStart=ScreenPoint(958+(float)(modePoint.Input/100)*634,648-(float)((modePoint.Level-10)/40)*350);
                canvas.PointerDown(modeStart);require(canvas.Capture,"Editor/simulator transition fixture did not capture a node.");form.HandleCommand("curve-simulator");canvas.Refresh();canvas.PointerMove(to);canvas.PointerUp(to);
                require(modeDraft==ProductProfilesStore.Serialize(form.Draft),"Captured node drag edited a hidden curve after switching to simulation.");
                canvas.Refresh();var synthetic=canvas.Hits.Last(h=>h.Id=="sim-input-0");var syntheticStart=ScreenPoint(synthetic.Bounds.Left+2,synthetic.Bounds.Top+15);
                canvas.PointerDown(syntheticStart);require(canvas.Capture,"Simulator/editor transition fixture did not capture a slider.");var inputs=canvas.SimulationInputs;form.HandleCommand("curve-editor");canvas.Refresh();canvas.PointerMove(to);canvas.PointerUp(to);
                require(inputs==canvas.SimulationInputs,"Captured synthetic slider changed after switching to the editor.");
                form.HandleCommand("curve-simulator");var initialTime=canvas.Simulation.ElapsedSeconds;
                form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+1,"Visible simulator did not advance automatically.");
                form.HandleCommand("sim-run");form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+1,"Paused simulator advanced.");
                form.EditValue("sim-input-0",90);form.HandleCommand("sim-run");form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+2&&canvas.Simulation.Current?.ThermalOverride==true,"Live simulation did not use changed inputs.");
                form.Hide();form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+2,"Hidden simulator advanced.");form.Show();
                form.HandleCommand("curve-editor");form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+2,"Editor advanced simulation.");
                form.HandleCommand("curve-simulator");canvas.Page=ProductPage.Settings;form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==initialTime+2,"Other page advanced simulation.");
                canvas.Page=ProductPage.Curves;form.HandleCommand("sim-reset");require(canvas.Simulation.ElapsedSeconds==0,"Reset failed.");
                form.PresentationTick();require(canvas.Simulation.ElapsedSeconds==1,"Reset did not retain running playback.");
                form.HandleCommand("curve-editor");
                require(runtime.Commands==0,"Live simulation/persistence/draft fixtures wrote hardware.");
                var backup=Path.Combine(dir,"backup.json");var savedBytes=File.ReadAllText(path);var dirty=form.Dirty;
                var export=form.ExportProfilesAsync(backup);PumpUntil(()=>export.IsCompleted,"Profile export blocked.");export.GetAwaiter().GetResult();
                require(form.Dirty==dirty&&File.ReadAllText(path)==savedBytes,"Export changed persistence or dirty state.");
                var portable=ProductProfilesStore.Parse(File.ReadAllText(backup)) with{Battery=new ProductProfiles().Battery with{CpuPl1Watts=12,CpuPl2Watts=18}};ProductProfilesStore.Save(portable,backup);
                var import=form.ImportProfilesAsync(backup);PumpUntil(()=>import.IsCompleted,"Profile import blocked.");import.GetAwaiter().GetResult();
                require(form.Dirty&&form.Draft.Battery.CpuPl1Watts==12&&File.ReadAllText(path)==savedBytes&&runtime.Commands==0,"Import persisted/applied hardware or lost a profile.");
                var previous=ProductProfilesStore.Serialize(form.Draft);File.WriteAllText(backup,"broken");import=form.ImportProfilesAsync(backup);PumpUntil(()=>import.IsCompleted,"Invalid import blocked.");
                require(previous==ProductProfilesStore.Serialize(form.Draft)&&!string.IsNullOrWhiteSpace(canvas.Notice),"Invalid import replaced the draft.");
                var log=Path.Combine(dir,"fixture-events.log");File.WriteAllText(log,new string('x',2*1024*1024+100)+"\n");var diagnostic=Path.Combine(dir,"diagnostic.zip");
                var interrupted=Snapshot(DateTimeOffset.UtcNow.AddSeconds(-5),40,35,5,5) with{CpuPackagePowerW=61};
                canvas.State=canvas.State with{AutomaticInterruptionSnapshot=interrupted,Snapshot=interrupted with{CpuPackagePowerW=10},AppliedPerformance=new ProductProfiles().PerformanceConfiguration()};
                var bundle=form.ExportDiagnosticsAsync(diagnostic,log);PumpUntil(()=>bundle.IsCompleted,"Diagnostic export blocked.");bundle.GetAwaiter().GetResult();
                using(var zip=System.IO.Compression.ZipFile.OpenRead(diagnostic))
                {
                    require(zip.Entries.Count==5+(zip.GetEntry("telemetry-tail.jsonl") is null?0:1)&&zip.GetEntry("profiles-draft.json") is not null&&zip.GetEntry("events-tail.log")!.Length<=2*1024*1024,"Diagnostic leaked extra files or exceeded log bounds.");
                    using var reader=new StreamReader(zip.GetEntry("gui-state.json")!.Open());using var state=System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
                    require(state.RootElement.GetProperty("sessionId").GetString()==AppLog.SessionId&&state.RootElement.GetProperty("sessionStartedUtc").GetDateTimeOffset()==AppLog.SessionStartedUtc,"Diagnostic session identity mismatch.");
                    require(state.RootElement.GetProperty("physicalQualification").GetString()=="not-established-by-this-export","Diagnostic fabricated physical qualification.");
                    require(state.RootElement.GetProperty("automaticThermalContract").GetString()=="cpu-start90-active95-confirm2000ms-cpu99-immediate-raw-response",
                        "Diagnostic did not identify the thermal contract used by this build.");
                    require(state.RootElement.GetProperty("AutomaticInterruptionSnapshot").GetProperty("CpuPackagePowerW").GetDouble()==61 &&
                        state.RootElement.GetProperty("snapshot").GetProperty("CpuPackagePowerW").GetDouble()==10 &&
                        state.RootElement.GetProperty("AppliedPerformance").GetProperty("CpuEnabled").GetBoolean(),"Later healthy telemetry erased the triggering sample or applied limits from diagnostics.");
                }
                canvas.State=canvas.State with{AutomaticInterruptionSnapshot=interrupted with{GpuPowerW=double.NaN}};
                bundle=form.ExportDiagnosticsAsync(diagnostic,log);PumpUntil(()=>bundle.IsCompleted,"Invalid-metric diagnostic export blocked.");bundle.GetAwaiter().GetResult();
                using(var zip=System.IO.Compression.ZipFile.OpenRead(diagnostic))
                {
                    using var reader=new StreamReader(zip.GetEntry("gui-state.json")!.Open());using var state=System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
                    require(state.RootElement.GetProperty("AutomaticInterruptionSnapshot").GetProperty("GpuPowerW").GetString()=="NaN","Invalid triggering telemetry could not be exported faithfully.");
                }
                File.WriteAllText(log,"review-start\n"+new string('x',AppLog.DefaultTailBytes+100)+"\nreview-end\n");
                canvas.State=canvas.State with{AutomaticReview=true,AutomaticReviewMaximumSeconds=ProductAutomaticReview.ExtendedMaximumSeconds};
                bundle=form.ExportDiagnosticsAsync(diagnostic,log);PumpUntil(()=>bundle.IsCompleted,"Extended diagnostic export blocked.");bundle.GetAwaiter().GetResult();
                using(var zip=System.IO.Compression.ZipFile.OpenRead(diagnostic))
                {
                    using var logReader=new StreamReader(zip.GetEntry("events-tail.log")!.Open());var retained=logReader.ReadToEnd();
                    require(retained.StartsWith("review-start\n")&&retained.EndsWith("review-end\n")&&
                        zip.GetEntry("events-tail.log")!.Length<=AppLog.ExtendedReviewTailBytes,"Extended review lost the beginning of retained evidence or escaped its bound.");
                    using var reader=new StreamReader(zip.GetEntry("gui-state.json")!.Open());using var state=System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
                    require(state.RootElement.GetProperty("AutomaticReviewMaximumSeconds").GetInt32()==2700&&
                        state.RootElement.GetProperty("diagnosticMaximumBytesPerStream").GetInt32()==AppLog.ExtendedReviewTailBytes,"Diagnostic omitted its long review duration or retention bound.");
                }
                require(File.ReadAllText(path)==savedBytes&&runtime.Commands==0,"Diagnostic mutated preferences or dispatched hardware.");
                form.HandleCommand("discard");
                canvas.State=runtime.State with{Runtime="Healthy",FanMode="Automatic",FanAuthority="Custom",PerformanceSupported=true,CanApplyPerformance=true};
                form.EditValue("gpu",1800);form.HandleCommand("performance-apply");
                require(runtime.Commands==1&&!canvas.Busy,"Bounded custom GPU did not dispatch explicit Apply while Automatic was active.");
                form.EditValue("gpu",1850);runtime.CommandFailure="Simulated Guardian no response";
                form.HandleCommand("performance-apply");require(runtime.Commands==2&&!canvas.Busy&&canvas.Notice.Contains("Simulated"),"Apply failure was hidden or left UI stuck.");
                canvas.State=canvas.State with{PerformanceProcessPresent=true,CpuState="Recovering",GpuState="Failed",Failure="Guardian sin respuesta"};
                form.HandleCommand("performance-release");require(runtime.Commands==3&&!canvas.Busy&&canvas.AppliedCpu()=="Sin confirmación actual","Release failure fabricated reset or blocked UI.");
                runtime.CommandFailure=null;runtime.Publish(runtime.State with{Runtime="Recovering",ManualAuthorized=false,Snapshot=Snapshot(DateTimeOffset.UtcNow.AddSeconds(-10),40,45,5,5)});
                require(canvas.CurrentSnapshot is null,"Telemetry loss displayed current values.");form.HandleCommand("manual-apply");require(runtime.Commands==3,"Telemetry loss admitted Manual.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Persistence fixture shutdown failed.");
            }
            var corrupt=Path.Combine(dir,"corrupt.json");File.WriteAllText(corrupt,"broken");
            using(var form=new ProductForm("fixture://modules",fixture:new RecordingRuntime(),profilesPath:corrupt))
            {
                form.Show();Application.DoEvents();require(form.Dirty&&File.ReadAllText(corrupt)=="broken","Fallback preferences were presented as saved or overwritten.");
                form.EditValue("pl1",37);form.HandleCommand("discard");require(form.Dirty&&form.Draft.Ac.CpuPl1Watts==35,"Discard marked an unpersisted fallback as saved.");
                form.HandleCommand("save");PumpUntil(()=>!form.Canvas.Busy,"Fallback save did not finish.");require(!form.Dirty&&ProductProfilesStore.Load(corrupt,out var warning).Ac.CpuPl1Watts==35&&warning is null,"Explicit save failed to replace corrupt preferences.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Corrupt-preference fixture shutdown failed.");
            }
            // A failed atomic save preserves dirty state and cleans its temporary file.
            var blocked=Path.Combine(dir,"directory-target");Directory.CreateDirectory(blocked);
            using(var form=new ProductForm("fixture://modules",fixture:new RecordingRuntime(),fixtureProfiles:new ProductProfiles(),profilesPath:blocked))
            {
                form.Show();Application.DoEvents();form.EditValue("pl1",36);form.HandleCommand("save");PumpUntil(()=>!form.Canvas.Busy,"Failed save did not finish.");
                require(form.Dirty&&!string.IsNullOrWhiteSpace(form.Canvas.Notice)&&Directory.GetFiles(dir,"*.tmp").Length==0,"Failed save erased dirty state or left temporary data.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed-save fixture shutdown failed.");
            }
            var pending=new RecordingRuntime{ManualGate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
            using(var form=new ProductForm("fixture://modules",fixture:pending,fixtureProfiles:new ProductProfiles()))
            {
                form.Show();Application.DoEvents();form.Canvas.State=pending.State with{ManualAuthorized=true};form.HandleCommand("fan-mode-1");var exit=form.RequestExitAsync();
                require(!exit.IsCompleted&&pending.Disposals==0,"Exit disposed runtime before admitted command finished.");
                pending.ManualGate.SetResult();PumpUntil(()=>exit.IsCompleted,"Exit failed to drain admitted command.");require(pending.Disposals==1,"Pending command leaked runtime.");
            }
            using(var form=new ProductForm("fixture://modules",fixtureProfiles:new ProductProfiles(),runtimeFactory:()=>Task.FromException<IProductRuntime>(new IOException("Simulated startup failure"))))
            {
                form.Show();Application.DoEvents();require(form.Canvas.Notice.Contains("Simulated startup failure"),"Startup failure was hidden.");var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Failed startup prevented Exit.");
            }
            using var history=new ProductCanvas();var now=DateTimeOffset.UtcNow;
            history.AddSnapshot(Snapshot(now.AddSeconds(-12),40,45,5,5));history.AddSnapshot(Snapshot(now.AddSeconds(-11),41,46,5,5));
            history.AddSnapshot(Snapshot(now.AddSeconds(-10),double.NaN,47,5,5));history.AddSnapshot(Snapshot(now.AddSeconds(-9),43,48,5,5));
            history.AddSnapshot(Snapshot(now.AddSeconds(-1),44,49,5,5));history.AddSnapshot(Snapshot(now.AddHours(1),90,90,5,5));
            var series=history.HistorySeries(s=>s.CpuTemperatureC,now);
            require(series.Count==3&&series.Sum(x=>x.Count)==4,"History joined invalid/gapped/future samples.");
            require(history.HistorySeries(s=>s.CpuTemperatureC,now.AddMinutes(6)).Count==0,"History froze old values at the current edge.");
        }
        finally{Directory.Delete(dir,true);}
        Console.WriteLine("PASS: six-axis/two-profile bounds, 64-node navigation, save/discard/failure, no-response Apply/Release, telemetry gaps and Exit drain.");
    }
    internal static int RunSoak()
    {
        try
        {
            var records=new List<object>();int frames=0;
            void Cycle(int index)
            {
                var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://soak",fixture:runtime,fixtureProfiles:new ProductProfiles());
                form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Dock=DockStyle.None;
                for(int i=0;i<28;i++)
                {
                    form.HandleCommand("page-"+(i%Enum.GetValues<ProductPage>().Length));form.HandleCommand(i%2==0?"profile-ac":"profile-battery");canvas.AdvancedTab=i%4;canvas.Size=i%2==0?new(1040,660):new(1344,756);
                    canvas.SimulationVisible=i%4==0;form.EditValue("sim-input-4",i%101);form.HandleCommand("sim-60");
                    canvas.Refresh();canvas.Focus();canvas.HandleKey(Keys.Tab);canvas.HandleKey(Keys.Shift|Keys.Tab);
                    using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));frames++;
                    if(canvas.Hits.Count>100||runtime.Commands!=0)throw new InvalidOperationException("Soak accumulated controls or acquired authority.");
                }
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Soak Exit did not complete.");if(runtime.Disposals!=1)throw new InvalidOperationException("Soak leaked a runtime.");
            }
            for(int i=0;i<3;i++)Cycle(i);
            (uint Gdi,uint User,long Bytes) Measure(){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();using var p=System.Diagnostics.Process.GetCurrentProcess();p.Refresh();return(GetGuiResources(p.Handle,0),GetGuiResources(p.Handle,1),p.PrivateMemorySize64);}
            var before=Measure();if(before.Gdi==0)throw new InvalidOperationException("GDI measurement unavailable.");
            for(int i=0;i<30;i++){Cycle(i);if(i%5==4){var m=Measure();records.Add(new{cycle=i+1,gdi=m.Gdi,user=m.User,privateBytes=m.Bytes});}}
            var after=Measure();
            var report=new{kind="isolated-product-gui-soak",cycles=30,warmupCycles=3,frames,baseline=new{gdi=before.Gdi,user=before.User,privateBytes=before.Bytes},final=new{gdi=after.Gdi,user=after.User,privateBytes=after.Bytes},samples=records,hardwareCommands=0};
            var path=Path.GetFullPath("logs/product-gui-soak/report.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
            if(after.Gdi>before.Gdi+16||after.User>before.User+16||after.Bytes>before.Bytes+64*1024*1024)throw new InvalidOperationException("Soak resource growth exceeded bounded tolerances; inspect report.json.");
            Console.WriteLine($"PASS: product GUI soak, {frames} renders, 30 open/close cycles, GDI {before.Gdi}->{after.Gdi}, USER {before.User}->{after.User}, zero hardware commands.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetGuiResources(IntPtr process,uint flags);
    private static TelemetrySnapshot Snapshot(DateTimeOffset timestamp,double cpu,double gpu,double cpuLoad,double gpuLoad)=>new(timestamp,"Intel i7-13700H",cpu,18,cpuLoad,"RTX 4060 Laptop",gpu,42,gpuLoad,3020,2980)
    {CpuCoreTemperatures=[new(0,0,"Performance",cpu+3)]};
    private static void TestLiveCurveApply(Action<bool,string> require)
    {
        var dir=Path.Combine(Path.GetTempPath(),"vfc-live-curve-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var runtime=new RecordingRuntime();var path=Path.Combine(dir,"profiles.json");
            using var form=new ProductForm("fixture://curve-apply",fixture:runtime,fixtureProfiles:new ProductProfiles(),profilesPath:path);
            form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Page=ProductPage.Curves;
            form.EditNode(3,60,30);var curve=form.Draft.Ac.Fan.UnifiedDemand!;
            form.HandleCommand("curve-apply");require(runtime.Commands==0,"Firmware Apply acquired authority.");
            var active=new ProductRuntimeState{AutomaticAuthorized=true,Runtime="Healthy",Source="Ac",AppliedFanProfile="Ac",
                FanMode="Automatic",FanAuthority="Custom",Snapshot=Snapshot(DateTimeOffset.UtcNow,50,40,10,10)};
            runtime.Publish(active);canvas.Refresh();
            require(canvas.CanApplyCurve&&canvas.Hits.Any(h=>h.Id=="curve-apply"&&h.Enabled),"Live Apply button missing or disabled for active source.");
            form.HandleCommand("curve-apply");PumpUntil(()=>!canvas.Busy,"Live Apply did not drain.");
            require(runtime.Commands==1&&runtime.LastCurveSource==ProductPowerProfile.Ac&&runtime.LastCurve!.Curve.SequenceEqual(curve.Curve)&&
                form.Dirty&&!File.Exists(path),"Apply saved settings, lost draft data or dispatched another operation.");
            form.HandleCommand("profile-battery");form.HandleCommand("curve-apply");require(runtime.Commands==1,"Apply accepted the inactive battery profile.");
            form.HandleCommand("profile-ac");runtime.Publish(active with{LifecycleBlocked=true});form.HandleCommand("curve-apply");require(runtime.Commands==1,"Apply accepted a blocked lifecycle.");
            runtime.Publish(active with{AutomaticPreparing=true});form.HandleCommand("curve-apply");require(runtime.Commands==1,"Apply accepted a pending activation.");
            runtime.Publish(active with{AutomaticSourceTransition="Battery"});form.HandleCommand("curve-apply");require(runtime.Commands==1,"Apply accepted a pending source transition.");
            runtime.Publish(active with{Snapshot=active.Snapshot! with{Timestamp=DateTimeOffset.UtcNow.AddSeconds(-10)}});form.HandleCommand("curve-apply");require(runtime.Commands==1,"Apply accepted stale telemetry.");
            var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Live Apply fixture shutdown failed.");exit.GetAwaiter().GetResult();
        }
        finally{Directory.Delete(dir,true);}
    }
    private static void TestLivePerformanceApply(Action<bool,string> require)
    {
        ProductPerformanceUpdate.EnsureWaitAllowed(100,4099,"Ac","Ac",84,77);
        foreach(var input in new[]{(4100L,"Ac",84d,77d),(99L,"Ac",84d,77d),(101L,"Battery",84d,77d),(101L,"Ac",85d,77d),(101L,"Ac",84d,78d)})
        {
            var refused=false;try{ProductPerformanceUpdate.EnsureWaitAllowed(100,input.Item1,input.Item2,"Ac",input.Item3,input.Item4);}catch(InvalidOperationException){refused=true;}
            require(refused,"Live performance wait renewed its deadline or admitted source/thermal urgency.");
        }
        var profiles=new ProductProfiles();var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://performance",fixture:runtime,fixtureProfiles:profiles);
        form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Page=ProductPage.Performance;
        var active=runtime.State with{Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",FanMode="Automatic",FanAuthority="Custom",
            PerformanceSupported=true,CanApplyPerformance=true,PerformanceProcessPresent=true,PerformanceActive=true,CpuState="Active",GpuState="ActiveUnverified",
            AppliedPerformance=profiles.PerformanceConfiguration(),AppliedPerformanceSource="Ac",Snapshot=Snapshot(DateTimeOffset.UtcNow,45,40,10,5)};
        runtime.Publish(active);canvas.Refresh();require(!canvas.CanApplyPerformance,"Unchanged limits enabled Apply.");
        form.EditValue("pl2",38);canvas.Refresh();
        require(canvas.CanApplyPerformance&&canvas.Hits.Single(h=>h.Id=="performance-apply").Enabled&&canvas.AppliedCpu()=="35 / 60 W",
            "Edited PL2 did not enable Apply or was presented as already applied.");
        form.HandleCommand("performance-apply");PumpUntil(()=>!canvas.Busy,"Performance Apply did not drain.");
        var expected=form.Draft.PerformanceConfiguration();
        require(runtime.Commands==1&&runtime.LastPerformance==expected&&expected.AcPl2Watts==38,"Live Apply omitted exact presets.");
        runtime.Publish(active with{AppliedPerformance=expected});require(!canvas.CanApplyPerformance&&canvas.AppliedCpu()=="35 / 38 W","Confirmed values left Apply enabled or displayed old limits.");
        form.HandleCommand("cpu-toggle");require(form.Draft.CpuEnabled,"Active CPU domain checkbox silently changed ownership.");
        form.EditValue("gpu",1801);require(canvas.CanApplyPerformance,"Exact GPU edit did not enable Apply.");
        foreach(var blocked in new[]{active with{AppliedPerformance=expected,PerformanceUpdating=true},active with{AppliedPerformance=expected,LifecycleBlocked=true},
            active with{AppliedPerformance=expected,AutomaticSourceTransition="Battery"},active with{AppliedPerformance=expected,AutomaticPreparing=true},
            active with{AppliedPerformance=expected,Snapshot=active.Snapshot! with{Timestamp=DateTimeOffset.UtcNow.AddSeconds(-10)}}})
        {runtime.Publish(blocked);form.HandleCommand("performance-apply");require(!canvas.CanApplyPerformance&&runtime.Commands==1,"Unsafe/pending performance edit dispatched.");}
        var frozen=ProductPerformanceUpdate.WithPerformance(profiles,expected);
        require(ReferenceEquals(frozen.Ac.Fan,profiles.Ac.Fan)&&ReferenceEquals(frozen.Battery.Fan,profiles.Battery.Fan)&&frozen.PerformanceConfiguration()==expected,
            "Performance update replaced fan curves/tuning or omitted a power profile.");
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Performance fixture exit did not drain.");exit.GetAwaiter().GetResult();
    }

    private static void TestAdvancedSettings(Action<bool,string> require)
    {
        var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://advanced",fixture:runtime,fixtureProfiles:new ProductProfiles());
        form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Page=ProductPage.Advanced;
        var original=ProductProfilesStore.Serialize(form.Draft);
        foreach(var invalid in new[]{"NaN","Infinity","1e3","1.000.0","0.1","61"})
            require(!form.TryEditTuningValue("rise",invalid,out _),"Invalid tuning numeric value accepted: "+invalid);
        require(!form.TryEditTuningValue("cores","2.5",out _)&&ProductProfilesStore.Serialize(form.Draft)==original,"Rejected edits changed tuning.");
        require(form.TryEditTuningValue("rise","7,5",out _),"Decimal comma rejected.");
        require(form.Draft.Ac.Fan.Tuning.RiseTimeConstantSeconds==7.5&&form.Draft.Battery.Fan.Tuning==form.Draft.Ac.Fan.Tuning&&runtime.Commands==0,"Tuning edit wrote hardware or changed only one source.");
        require(!form.TryEditTuningValue("short-fall","50",out _),"Brief descent slower than prolonged descent accepted.");
        var caps=form.Draft.PerformanceConfiguration();
        require(form.TryEditTuningValue("rise","60",out _)&&form.TryEditTuningValue("rise-confirm","30",out _)&&
            form.TryEditTuningValue("fall","300",out _)&&form.TryEditTuningValue("short-fall","300",out _)&&
            form.TryEditTuningValue("fall-confirm","180",out _)&&form.TryEditTuningValue("short-confirm","180",out _)&&
            form.TryEditTuningValue("hysteresis","5",out _)&&form.TryEditTuningValue("thermal-hold","300",out _)&&
            form.TryEditTuningValue("sustained","7200",out _)&&form.TryEditTuningValue("pause","300",out _)&&
            form.TryEditTuningValue("cooldown","1800",out _),"Expanded advanced limits rejected.");
        form.HandleCommand("tuning-stable-presets");
        require(form.Draft.PerformanceConfiguration()==caps&&runtime.Commands==0&&
            form.Draft.Ac.Fan.UnifiedDemand!.Curve.SequenceEqual(UnifiedFanDemand.Default(false).Curve)&&
            form.Draft.Battery.Fan.UnifiedDemand!.Curve.SequenceEqual(UnifiedFanDemand.Default(true).Curve)&&
            form.Draft.Ac.Fan.Tuning.NormalDecreaseHysteresisLevels==1&&form.Draft.Battery.Fan.Tuning==form.Draft.Ac.Fan.Tuning,
            "Preparing stable presets changed caps, wrote hardware or lost common tuning.");
        require(form.TryEditTuningValue("rise","7.5",out _),"Live edit after preset preparation failed.");
        var active=canvas.State with{Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",AutomaticAuthorized=true,
            FanMode="Automatic",FanAuthority="Custom",Snapshot=Snapshot(DateTimeOffset.UtcNow,40,35,10,5),AppliedAutomaticConfiguration=new ProductProfiles().Ac.Fan};
        runtime.Publish(active);form.HandleCommand("profile-battery");form.HandleCommand("tuning-apply");PumpUntil(()=>!canvas.Busy,"Tuning apply did not drain.");
        require(runtime.Commands==1&&runtime.LastTuning==form.Draft.Ac.Fan.Tuning&&form.Dirty,"Common tuning apply depended on editing source or saved implicitly.");
        foreach(var blocked in new[]{active with{LifecycleBlocked=true},active with{AutomaticPreparing=true},active with{AutomaticSourceTransition="Battery"},
            active with{FanMode="Manual"},active with{Snapshot=active.Snapshot! with{Timestamp=DateTimeOffset.UtcNow.AddSeconds(-10)}}})
        {runtime.Publish(blocked);form.HandleCommand("tuning-apply");require(runtime.Commands==1,"Blocked tuning apply dispatched.");}
        var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Advanced fixture exit did not drain.");exit.GetAwaiter().GetResult();
    }
    private sealed class RecordingRuntime:IProductRuntime
    {
        internal ProductPowerProfile? LastCurveSource;
        internal UnifiedFanDemand? LastCurve;
        public Task ApplyFanCurveAsync(ProductPowerProfile source,UnifiedFanDemand demand){Commands++;LastCurveSource=source;LastCurve=demand with{Curve=demand.Curve.ToArray()};return Task.CompletedTask;}
        internal AdaptiveFanTuning? LastTuning;
        public Task ApplyFanTuningAsync(AdaptiveFanTuning tuning){Commands++;LastTuning=tuning;return Task.CompletedTask;}
        public event Action<ProductRuntimeState>? Changed;
        public ProductRuntimeState State {get;}=new();
        internal int Commands,Starts,Disposals,Fences,Releases,Resumes;
        internal int? LastManualLevel;
        internal PerformanceGuiSessionConfiguration? LastPerformance;
        internal TaskCompletionSource? ReleaseGate,ManualGate,PerformanceGate;
        internal string? CommandFailure,StartFailure;
        internal void Publish(ProductRuntimeState state)=>Changed?.Invoke(state);
        public void Start(){Starts++;if(StartFailure is not null)throw new InvalidOperationException(StartFailure);Changed?.Invoke(State);}
        public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles p){Commands++;return mode==AdaptiveFanProductionMode.Manual?ManualGate?.Task??Task.CompletedTask:Task.CompletedTask;}
        public Task ApplyManualAsync(int level){Commands++;LastManualLevel=level;return Task.CompletedTask;}
        public Task ApplyPerformanceAsync(ProductProfiles p){Commands++;LastPerformance=p.PerformanceConfiguration();return CommandFailure is null?PerformanceGate?.Task??Task.CompletedTask:Task.FromException(new IOException(CommandFailure));}
        public Task ReleasePerformanceAsync(){Commands++;return CommandFailure is null?Task.CompletedTask:Task.FromException(new IOException(CommandFailure));}
        public void FenceLifecycle(string r){Interlocked.Increment(ref Fences);}
        public Task ReleaseForLifecycleAsync(string r){Interlocked.Increment(ref Releases);return ReleaseGate?.Task??Task.CompletedTask;}
        public void ResumeTelemetry(string r){Resumes++;}
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);return ValueTask.CompletedTask;}
    }
}
