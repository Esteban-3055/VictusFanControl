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
            form.ClientSize=new(1672,941);form.Show();Application.DoEvents();var canvas=form.Canvas;
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
            var output=Path.GetFullPath("logs/product-gui-self-test");Directory.CreateDirectory(output);
            // Fixture telemetry is explicit and used only by this rendering entry. No hardware reader is constructed.
            var baseline=new ProductProfiles();canvas.Profiles=baseline;canvas.Notice="";canvas.State=runtime.State with
            {
                Hardware="HP Victus 15-fa1xxx (8C40)",Target="HP-8C40-9D0R1LA-F18",Runtime="Healthy",Source="Ac",
                ManualAuthorized=true,AutomaticAuthorized=false,PerformanceSupported=true,CanApplyPerformance=false,
                FanMode="Manual",FanAuthority="Custom",FanLevel=30,CpuState="Active",GpuState="ActiveUnverified",
                PerformanceActive=true,PerformanceProcessPresent=true,GuardianState="SessionEnabled",
                AppliedPerformance=new PerformanceGuiSessionConfiguration(),Snapshot=Snapshot(DateTimeOffset.UtcNow,36,48,12,28)
            };
            canvas.Editing=ProductPowerProfile.Ac;canvas.SelectedNode=-1;canvas.StartupKnown=true;
            for(int i=300;i>=0;i--){var wave=Math.Sin(i*.13);canvas.AddSnapshot(Snapshot(DateTimeOffset.UtcNow.AddSeconds(-i),60-i*.06+wave*2,50-i*.025+wave,38+wave*12,24+wave*8));}
            void Render(string name)
            {
                canvas.State=canvas.State with{Snapshot=canvas.State.Snapshot is { } s?s with{Timestamp=DateTimeOffset.UtcNow}:null};
                canvas.Refresh();Application.DoEvents();using var bitmap=new Bitmap(canvas.Width,canvas.Height);canvas.DrawToBitmap(bitmap,new(0,0,bitmap.Width,bitmap.Height));
                bitmap.Save(Path.Combine(output,name+".png"),ImageFormat.Png);
                Require(canvas.Hits.All(h=>h.Bounds.Left>=0&&h.Bounds.Top>=0&&h.Bounds.Right<=1673&&h.Bounds.Bottom<=942),"Hit area outside reference surface.");
                Require(bitmap.GetPixel(bitmap.Width/2,bitmap.Height/2).A==255,"Render is transparent.");
            }
            foreach(var page in Enum.GetValues<ProductPage>()){canvas.Page=page;canvas.FanTab=0;canvas.PerformanceTab=0;Render("page-"+page);}
            canvas.Page=ProductPage.Fans;for(int i=1;i<=3;i++){canvas.FanTab=i;Render("fans-tab-"+i);}
            canvas.Page=ProductPage.Performance;for(int i=1;i<=3;i++){canvas.PerformanceTab=i;Render("performance-tab-"+i);}
            canvas.Page=ProductPage.Monitoring;for(int i=1;i<=3;i++){canvas.MonitorTab=i;Render("monitor-tab-"+i);}
            canvas.Page=ProductPage.Curves;foreach(var size in new[]{new Size(1040,660),new Size(1254,706),new Size(1920,1080),new Size(2508,1412),new Size(3344,1882)})
            {form.ClientSize=size;Render("layout-"+size.Width+"x"+size.Height);}
            Require(runtime.Commands==0,"Rendering wrote hardware.");
            Console.WriteLine("PASS: product GUI draft isolation, editing/navigation without authority, sliders, nodes, closed gates and real Windows renders.");
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static TelemetrySnapshot Snapshot(DateTimeOffset timestamp,double cpu,double gpu,double cpuLoad,double gpuLoad)=>new(timestamp,"Intel i7-13700H",cpu,18,cpuLoad,"RTX 4060 Laptop",gpu,42,gpuLoad,3020,2980)
    {CpuCoreTemperatures=[new(0,0,"Performance",cpu+3)]};
    private sealed class RecordingRuntime:IProductRuntime
    {
        public event Action<ProductRuntimeState>? Changed;
        public ProductRuntimeState State {get;}=new();
        internal int Commands;
        public void Start()=>Changed?.Invoke(State);
        public Task SelectFanModeAsync(AdaptiveFanProductionMode mode,ProductProfiles p){Commands++;return Task.CompletedTask;}
        public Task ApplyManualAsync(int level){Commands++;return Task.CompletedTask;}
        public Task ApplyPerformanceAsync(ProductProfiles p){Commands++;return Task.CompletedTask;}
        public Task ReleasePerformanceAsync(){Commands++;return Task.CompletedTask;}
        public void FenceLifecycle(string r){Commands++;}
        public Task ReleaseForLifecycleAsync(string r){Commands++;return Task.CompletedTask;}
        public void ResumeTelemetry(string r){Commands++;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
