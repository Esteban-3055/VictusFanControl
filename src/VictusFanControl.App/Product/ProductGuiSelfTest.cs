using System.Drawing.Imaging;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Product;
using VictusFanControl.Performance;
using VictusFanControl.Telemetry;
namespace VictusFanControl.App;

internal static class ProductGuiSelfTest
{
    internal static int Run()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://modules",fixture:runtime,fixtureProfiles:new ProductProfiles());
            form.ClientSize=new(1672,941);form.Show();Application.DoEvents();var canvas=form.Canvas;canvas.Dock=DockStyle.None;canvas.Size=new(1672,941);
            Require(runtime.Commands==0,"Startup acquired authority.");
            form.HandleCommand("profile-battery");form.EditValue("pl1",30);form.EditValue("pl2",15);
            Require(form.Draft.Battery.CpuPl1Watts==30&&form.Draft.Battery.CpuPl2Watts==30&&form.Draft.Ac.CpuPl1Watts==35,"PL1/PL2 or independent AC failed.");
            form.HandleCommand("profile-ac");canvas.Axis=AdaptiveCurveAxis.CpuTemperature;form.EditNode(3,70,31);
            Require(form.Draft.Ac.Fan.BuildPolicy().CpuTemperatureCurve[3].Level==31&&form.Draft.Battery.Fan.BuildPolicy().CpuTemperatureCurve[3].Level==30,"Curve edit leaked across profiles.");
            for(int i=0;i<7;i++)form.HandleCommand("page-"+i);
            Require(runtime.Commands==0,"Editing/navigation wrote hardware.");
            form.HandleCommand("fan-mode-2");Require(runtime.Commands==0,"Closed Automatic gate dispatched.");
            form.EditValue("gpu",99999);Require(form.Draft.Ac.GpuMaximumMHz==1850,"GPU slider escaped upper bound.");
            form.EditValue("gpu",0);Require(form.Draft.Ac.GpuMaximumMHz==210,"GPU slider escaped lower bound.");
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
            canvas.Page=ProductPage.Curves;canvas.Refresh();var priorAxis=canvas.Axis;canvas.HandleKey(Keys.Enter);
            Require(canvas.Axis==priorAxis,"A retained focus activated an unrelated control on another page.");
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
            canvas.Editing=ProductPowerProfile.Ac;canvas.SelectedNode=-1;canvas.StartupKnown=true;
            for(int i=300;i>=0;i--){var wave=Math.Sin(i*.13);canvas.AddSnapshot(Snapshot(DateTimeOffset.UtcNow.AddSeconds(-i),60-i*.06+wave*2,50-i*.025+wave,38+wave*12,24+wave*8));}
            void Render(string name)
            {
                canvas.State=canvas.State with{Snapshot=canvas.State.Snapshot is { } s?s with{Timestamp=DateTimeOffset.UtcNow}:null};
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
            canvas.Page=ProductPage.Fans;for(int i=1;i<=3;i++){canvas.FanTab=i;Render("fans-tab-"+i);}
            canvas.Page=ProductPage.Performance;for(int i=1;i<=3;i++){canvas.PerformanceTab=i;Render("performance-tab-"+i);}
            canvas.Page=ProductPage.Monitoring;for(int i=1;i<=3;i++){canvas.MonitorTab=i;Render("monitor-tab-"+i);}
            canvas.Page=ProductPage.Curves;foreach(var size in new[]{new Size(1040,660),new Size(1254,706),new Size(1920,1080),new Size(2508,1412),new Size(3344,1882)})
            {canvas.Size=size;Require(canvas.Size==size,"Requested layout was clamped.");Render("layout-"+size.Width+"x"+size.Height);}
            Require(runtime.Commands==0,"Rendering wrote hardware.");
            canvas.Size=new(1672,941);canvas.Page=ProductPage.Home;
            foreach(var state in new[]{"Unsupported","Recovering","Failed"}){canvas.State=canvas.State with{Runtime=state,CpuState=state,GpuState=state,PerformanceActive=false,CanApplyPerformance=false};Render("state-"+state);}
            canvas.Page=ProductPage.Performance;canvas.State=canvas.State with{Runtime="Healthy",CpuState="Active",GpuState="Failed",PerformanceActive=false};Render("state-partial-CPU-active-GPU-failed");
            form.HandleCommand("fan-mode-1");form.HandleCommand("manual-apply");form.HandleCommand("firmware");form.HandleCommand("performance-release");
            Require(runtime.Commands==4,"Explicit controls did not dispatch their separate contracts.");
            RunLifecycleFixtures(Require);
            Console.WriteLine("PASS: product GUI draft isolation, editing/navigation without authority, sliders, nodes, closed gates and real Windows renders.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void PumpUntil(Func<bool> completed,string failure)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!completed()){if(timer.Elapsed>TimeSpan.FromSeconds(10))throw new InvalidOperationException(failure);Application.DoEvents();Thread.Sleep(1);}
        Application.DoEvents();
    }
    private static void RunLifecycleFixtures(Action<bool,string> require)
    {
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
    private static TelemetrySnapshot Snapshot(DateTimeOffset timestamp,double cpu,double gpu,double cpuLoad,double gpuLoad)=>new(timestamp,"Intel i7-13700H",cpu,18,cpuLoad,"RTX 4060 Laptop",gpu,42,gpuLoad,3020,2980)
    {CpuCoreTemperatures=[new(0,0,"Performance",cpu+3)]};
    private sealed class RecordingRuntime:IProductRuntime
    {
        public event Action<ProductRuntimeState>? Changed;
        public ProductRuntimeState State {get;}=new();
        internal int Commands,Starts,Disposals,Fences,Releases,Resumes;
        internal TaskCompletionSource? ReleaseGate,ManualGate;
        public void Start(){Starts++;Changed?.Invoke(State);}
        public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles p){Commands++;return mode==AdaptiveFanProductionMode.Manual?ManualGate?.Task??Task.CompletedTask:Task.CompletedTask;}
        public Task ApplyManualAsync(int level){Commands++;return Task.CompletedTask;}
        public Task ApplyPerformanceAsync(ProductProfiles p){Commands++;return Task.CompletedTask;}
        public Task ReleasePerformanceAsync(){Commands++;return Task.CompletedTask;}
        public void FenceLifecycle(string r){Interlocked.Increment(ref Fences);}
        public Task ReleaseForLifecycleAsync(string r){Interlocked.Increment(ref Releases);return ReleaseGate?.Task??Task.CompletedTask;}
        public void ResumeTelemetry(string r){Resumes++;}
        public ValueTask DisposeAsync(){Interlocked.Increment(ref Disposals);return ValueTask.CompletedTask;}
    }
}
