using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunHomeAnimationChecks()
    {
        try
        {
            void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            TestHomeDashboard(Require);TestLiveAnimationSettings(Require);TestHomeAnimationCadence(Require);return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
    private static void TestHomeAnimationCadence(Action<bool,string> require)
    {
        ProductRotorRasterSelfTest.Run(require);
        var runtime=new RecordingRuntime();
        using var form=new ProductForm("fixture://cadence",fixture:runtime,fixtureProfiles:new ProductProfiles());
        form.Show();Application.DoEvents();var canvas=form.Canvas;
        canvas.State=ProductHomeTelemetrySelfTest.StateAt(DateTimeOffset.UtcNow);
        var startFrames=canvas.HomePaintedFrames;
        Exception? failure=null;
        using var context=new ApplicationContext();
        void Finish()
        {
            try
            {
                var live=canvas.HomeAnimationDiagnostic();
                Console.WriteLine(JsonSerializer.Serialize(live));
                var measuredPath=Path.GetFullPath("logs/product-gui-self-test/animation-cadence.json");
                Directory.CreateDirectory(Path.GetDirectoryName(measuredPath)!);
                File.WriteAllText(measuredPath,JsonSerializer.Serialize(new{kind="isolated-home-animation-cadence",live},new JsonSerializerOptions{WriteIndented=true}));
                using var doc=JsonDocument.Parse(JsonSerializer.Serialize(live));
                require(canvas.HomePaintedFrames-startFrames>30&&doc.RootElement.GetProperty("frameIntervalMedianMs").GetDouble()>0,
                    "Real UI animation did not draw or measure its frame cadence.");
                require(doc.RootElement.GetProperty("paintedFramesPerSecond").GetDouble()>40&&doc.RootElement.GetProperty("frameIntervalMedianMs").GetDouble()<25,
                    "CI UI cadence fell below the measured animation threshold.");
                require(doc.RootElement.GetProperty("scheduler").GetString()=="waitable-high-resolution", "High-resolution scheduler unavailable on Windows CI.");
                // A busy UI must coalesce rather than queue a burst of catch-up callbacks.
                Thread.Sleep(100);Application.DoEvents();
                using(var stalled=JsonDocument.Parse(JsonSerializer.Serialize(canvas.HomeAnimationDiagnostic())))
                    require(stalled.RootElement.GetProperty("coalescedRequests").GetInt64()>0,"Busy UI queued catch-up callbacks instead of coalescing.");
                form.Hide();Application.DoEvents();var hiddenFrames=canvas.HomePaintedFrames;var hiddenPhase=canvas.HomeFanPhases;
                Thread.Sleep(40);Application.DoEvents();
                require(!canvas.HomeAnimationRunning&&canvas.HomePaintedFrames==hiddenFrames&&canvas.HomeFanPhases==hiddenPhase,
                    "Native producer painted or advanced while hidden.");
                form.Show();Application.DoEvents();canvas.Page=ProductPage.Fans;
                var otherFrames=canvas.HomePaintedFrames;Thread.Sleep(40);Application.DoEvents();
                require(!canvas.HomeAnimationRunning&&canvas.HomePaintedFrames==otherFrames,"Another tab left the native producer active.");
                canvas.Page=ProductPage.Home;Application.DoEvents();form.WindowState=FormWindowState.Minimized;
                Thread.Sleep(40);Application.DoEvents();require(!canvas.HomeAnimationRunning,"Minimize did not stop the native producer.");
                form.WindowState=FormWindowState.Normal;Application.DoEvents();
                require(runtime.Commands==0,"Animation scheduler acquired hardware authority.");
                using(var capture=new Bitmap(canvas.Width,canvas.Height))
                {canvas.DrawToBitmap(capture,new(Point.Empty,capture.Size));capture.Save(Path.Combine(Path.GetDirectoryName(measuredPath)!,"home-png-cadence.png"),System.Drawing.Imaging.ImageFormat.Png);}
                var output=measuredPath;
                var costs=new List<object>();
                using var rotor=new ProductFanRotor();
                foreach(var size in new[]{176,352})
                {
                    using var image=new Bitmap(size,size);using var g=Graphics.FromImage(image);
                    g.ScaleTransform(size/176f,size/176f);g.SmoothingMode=SmoothingMode.AntiAlias;
                    rotor.Draw(g,88,88,0,size/176f,ProductCanvas.Blue); // Cache warmup.
                    var watch=Stopwatch.StartNew();
                    for(int i=0;i<200;i++)rotor.Draw(g,88,88,i*3,size/176f,ProductCanvas.Blue);
                    var pngMs=watch.Elapsed.TotalMilliseconds/200;
                    using var blade=new GraphicsPath();blade.AddBezier(0,-53,-7,-62,-19,-75,-15,-85);
                    blade.AddBezier(-15,-85,-5,-81,4,-65,4,-53);blade.CloseFigure();using var brush=new SolidBrush(ProductCanvas.Blue);
                    watch.Restart();
                    for(int f=0;f<200;f++)
                    {var saved=g.Save();g.TranslateTransform(88,88);g.RotateTransform(f*3);for(int b=0;b<28;b++){g.FillPath(brush,blade);g.RotateTransform(360f/28);}g.Restore(saved);}
                    costs.Add(new{deviceRotorPixels=size,pngMeanMs=pngMs,previousVectorMeanMs=watch.Elapsed.TotalMilliseconds/200});
                }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllText(output,JsonSerializer.Serialize(new{kind="isolated-home-animation-cadence",live,drawCosts=costs,
                    hardwareCommands=runtime.Commands,limits="CI UI paint intervals and isolated GDI draw costs; not user-device or monitor presentation FPS"},new JsonSerializerOptions{WriteIndented=true}));
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"Cadence fixture exit stalled.");
                Console.WriteLine("Animation cadence: PASS (real Windows message loop, high-resolution native timer, Stopwatch UI paint intervals, bounded pending callback, hidden/tab/minimize pause and PNG/vector draw-cost measurement; zero hardware IO).");
                Console.WriteLine(JsonSerializer.Serialize(live));
            }
            catch(Exception ex){failure=ex;}
            finally{context.ExitThread();}
        }
        using(var finish=new System.Threading.Timer(_=>
        {try{form.BeginInvoke((Action)Finish);}catch(InvalidOperationException){}},null,3000,Timeout.Infinite))
            Application.Run(context); // Run all checks before ExitThread disposes thread-owned windows.
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

    }
}
