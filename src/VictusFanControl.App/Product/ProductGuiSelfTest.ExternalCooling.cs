using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunExternalCoolingChecks()
    {
        try
        {
            var root=Path.GetFullPath("logs/external-cooling-self-test");Directory.CreateDirectory(root);
            var path=Path.Combine(root,"preferences-"+Guid.NewGuid().ToString("N")+".json");
            var key=ExternalCoolingProtocol.NewPairingKey();var raw=ExternalCoolingProtocol.ParsePairingKey(key);
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
            var settings=new ExternalCoolingSettings{Mode=ExternalCoolingMode.MonitorOnly,UseSimulator=true,Port=((IPEndPoint)listener.LocalEndpoint).Port};
            var store=new ExternalCoolingSettingsStore(path);store.Save(settings,key);
            ExternalCoolingSelfTest.Require(!File.ReadAllText(path).Contains(key,StringComparison.Ordinal),"Secret stored plaintext.");
            var loaded=new ExternalCoolingSettingsStore(path);
            ExternalCoolingSelfTest.Require(loaded.Load(out var notice)==settings&&notice is null&&loaded.Paired,"Preferences did not survive reload.");
            var decrypted=loaded.ReadKey()!;ExternalCoolingSelfTest.Require(CryptographicOperations.FixedTimeEquals(raw,decrypted),"DPAPI roundtrip failed.");CryptographicOperations.ZeroMemory(decrypted);
            loaded.Save(settings with{AutoReconnect=false});ExternalCoolingSelfTest.Require(loaded.Paired,"Settings update lost pairing.");
            using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));int received=0,sessions=0;
            var server=Task.Run(async()=>
            {
                try
                {
                    while(true)
                    {
                        using var tcp=await listener.AcceptTcpClientAsync(cancel.Token);
                        try
                        {
                            await using var wire=new FramedConnection(tcp.GetStream());using var auth=await AuthenticatedSession.ServerAsync(wire,raw,cancel.Token);Interlocked.Increment(ref sessions);
                            long previous=0;
                            while(true)
                            {
                                var frame=await wire.ReadAsync(cancel.Token);ExternalCoolingProtocol.Verify(frame,auth.ReceiveKey,auth.Id,previous);previous=frame.Sequence;
                                var t=ExternalCoolingProtocol.Telemetry(frame);ExternalCoolingSelfTest.Require(t.Solicitud is null,"MonitorOnly control escaped.");Interlocked.Increment(ref received);
                                await wire.WriteAsync(ExternalCoolingProtocol.Sign(ExternalCoolingProtocol.Create("deviceStatus",auth.Id,previous,new DeviceStatus
                                {AckSequence=previous,AmbientTempC=23,AppliedPwmPct=25,Mode="Local",Communication="Ready",Extensions=new(){Slot1=null,Slot2=null}}),auth.SendKey),cancel.Token);
                            }
                        }
                        catch(Exception ex)when(ex is IOException or OperationCanceledException){}
                    }
                }
                catch(Exception ex)when(cancel.IsCancellationRequested&&ex is OperationCanceledException or SocketException or InvalidOperationException){}
            });
            var runtime=new RecordingRuntime();
            using var form=new ProductForm("fixture://external-cooling",fixture:runtime,fixtureProfiles:new ProductProfiles(),externalCoolingPath:path);
            try
            {
                form.Show();PumpUntil(()=>form.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Connected,"Base connection did not complete.");
                form.Canvas.Page=ProductPage.Settings;form.Canvas.Refresh();
                ExternalCoolingSelfTest.Require(form.Canvas.Hits.Any(h=>h.Id=="external-cooling"&&h.Enabled),"Configuration entry missing.");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new(Point.Empty,form.Size));image.Save(Path.Combine(root,"settings.png"),ImageFormat.Png);}
                form.HandleCommand("external-cooling");Application.DoEvents();
                var panel=Application.OpenForms.OfType<ExternalCoolingSettingsForm>().Single();
                foreach(var size in new[]{new Size(790,700),new Size(650,520)})
                {
                    panel.ClientSize=new(size.Width*panel.DeviceDpi/96,size.Height*panel.DeviceDpi/96);panel.PerformLayout();Application.DoEvents();
                    using var image=new Bitmap(panel.Width,panel.Height);panel.DrawToBitmap(image,new(Point.Empty,panel.Size));image.Save(Path.Combine(root,$"panel-{size.Width}.png"),ImageFormat.Png);
                }
                panel.Close();var before=Volatile.Read(ref received);form.Hide();
                PumpUntil(()=>Volatile.Read(ref received)>=before+2,"Minimize/tray stopped telemetry.");
                var watch=System.Diagnostics.Stopwatch.StartNew();var suspend=form.HandlePowerEventAsync(4);
                ExternalCoolingSelfTest.Require(watch.ElapsedMilliseconds<200,"Network blocked suspend GUI.");
                PumpUntil(()=>suspend.IsCompleted,"Suspend cleanup did not drain.");suspend.GetAwaiter().GetResult();
                var oldSessions=Volatile.Read(ref sessions);var resume=form.HandlePowerEventAsync(18);
                PumpUntil(()=>resume.IsCompleted&&Volatile.Read(ref sessions)>oldSessions&&form.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Connected,"Resume did not authenticate new session.");
                ExternalCoolingSelfTest.Require(runtime.Commands==0,"External module issued internal commands.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"External cooling exit did not complete.");exit.GetAwaiter().GetResult();
                ExternalCoolingSelfTest.Require(form.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Disabled&&runtime.Disposals==1,"Network or runtime remained after exit.");
                Console.WriteLine("PASS ExternalCooling WinForms: DPAPI, persistence, settings entry, responsive layout renders, real authenticated TCP, hidden/tray transmission, suspend/resume/new session, exit; zero internal control commands.");
            }
            finally{cancel.Cancel();listener.Stop();server.GetAwaiter().GetResult();CryptographicOperations.ZeroMemory(raw);}
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
