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
            var corruptPath=Path.Combine(root,"corrupt-"+Guid.NewGuid().ToString("N")+".json");
            File.WriteAllText(corruptPath,"{invalid");
            var corruptRuntime=new RecordingRuntime();
            using(var corrupt=new ProductForm("fixture://external-corrupt",fixture:corruptRuntime,fixtureProfiles:new ProductProfiles(),externalCoolingPath:corruptPath))
            {
                corrupt.Show();PumpUntil(()=>corrupt.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Disabled,"Corrupt settings were not disabled.");
                ExternalCoolingSelfTest.Require(corrupt.ExternalCoolingError?.Contains("Configuración de base externa no válida")==true,"Corrupt settings notice was cleared by disabled startup.");
                corrupt.HandlePowerEventAsync(18).GetAwaiter().GetResult();Application.DoEvents();
                ExternalCoolingSelfTest.Require(corrupt.ExternalCoolingError is not null&&File.ReadAllText(corruptPath)=="{invalid"&&corruptRuntime.Commands==0,"Resume hid corrupt settings or changed source/control.");
                var closing=corrupt.RequestExitAsync();PumpUntil(()=>closing.IsCompleted,"Corrupt fixture did not close.");closing.GetAwaiter().GetResult();
            }
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
                        catch(Exception ex)when(ex is IOException or InvalidDataException or OperationCanceledException){}
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
                var navigation=form.Canvas.Hits.Where(h=>h.Id.StartsWith("page-")).OrderBy(h=>h.Bounds.Y).ToArray();
                var externalIndex=Array.FindIndex(navigation,h=>h.Id=="page-"+(int)ProductPage.ExternalCooling);
                ExternalCoolingSelfTest.Require(externalIndex>0&&navigation[externalIndex-1].Label=="Avanzado"&&navigation[externalIndex+1].Label=="Actualizaciones"&&
                    navigation.All(h=>h.Bounds.Bottom<882)&&!form.Canvas.Hits.Any(h=>h.Id=="external-cooling"),"External cooling navigation order/fit missing or obsolete Settings entry retained.");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new(Point.Empty,form.Size));image.Save(Path.Combine(root,"settings.png"),ImageFormat.Png);}
                var formCount=Application.OpenForms.Count;
                form.HandleCommand("page-"+(int)ProductPage.ExternalCooling);Application.DoEvents();
                var panel=form.Canvas.Controls.OfType<ExternalCoolingSettingsPanel>().Single();
                void RenderPage(string filename)
                {
                    using var image=new Bitmap(form.Canvas.Width,form.Canvas.Height);
                    // DrawToBitmap can paint nested native controls outside a scroll viewport.
                    // Composite the page at its real bounds to preserve HWND clipping in captures.
                    form.Canvas.Controls.Remove(panel);
                    try{form.Canvas.DrawToBitmap(image,new(Point.Empty,form.Canvas.Size));}
                    finally{form.Canvas.Controls.Add(panel);panel.SetPageSelected(true);}
                    using var pageImage=new Bitmap(panel.Width,panel.Height);
                    panel.DrawToBitmap(pageImage,new(Point.Empty,panel.Size));
                    using(var g=Graphics.FromImage(image))g.DrawImageUnscaled(pageImage,panel.Location);
                    image.Save(Path.Combine(root,filename),ImageFormat.Png);
                }
                ExternalCoolingSelfTest.Require(panel.Visible&&panel.Parent==form.Canvas&&Application.OpenForms.Count==formCount,"Base externa opened a separate window or did not embed its controls.");
                ExternalCoolingSelfTest.Require(panel.Controls.Find("external-status",true).Single().Text.Contains("Connected"),"Initial navigation did not render current diagnostics.");
                form.Canvas.Dock=DockStyle.None;
                foreach(var size in new[]{new Size(1672,941),new Size(1280,800),new Size(1040,660),new Size(1000,560)})
                {
                    form.Canvas.Size=size;form.Canvas.Refresh();Application.DoEvents();
                    ExternalCoolingSelfTest.Require(panel.Bounds.Left>0&&panel.Bounds.Right<=form.Canvas.Width&&panel.Bounds.Bottom<=form.Canvas.Height,"Embedded page escaped canvas bounds.");
                    RenderPage($"external-page-{size.Width}.png");
                }
                form.Canvas.Size=new(1280,800);Application.DoEvents();
                var mode=(ComboBox)panel.Controls.Find("external-mode",true).Single();
                var enabled=(CheckBox)panel.Controls.Find("external-enabled",true).Single();
                enabled.Checked=false;ExternalCoolingSelfTest.Require(mode.SelectedIndex==0,"Disable switch did not update the draft mode.");
                enabled.Checked=true;ExternalCoolingSelfTest.Require(mode.SelectedIndex==(int)ExternalCoolingMode.MonitorOnly,"Enable switch did not restore the monitoring draft.");
                var portDraft=(NumericUpDown)panel.Controls.Find("external-port",true).Single();var oldPort=portDraft.Value;portDraft.Value=8766;
                var oldSession=form.ExternalCoolingStatus?.SessionId;var beforePageChange=Volatile.Read(ref received);
                form.HandleCommand("page-"+(int)ProductPage.Updates);Application.DoEvents();
                ExternalCoolingSelfTest.Require(!panel.Visible,"External controls overlay another page.");
                PumpUntil(()=>Volatile.Read(ref received)>beforePageChange,"Leaving the page stopped communication.");
                form.HandleCommand("page-"+(int)ProductPage.ExternalCooling);Application.DoEvents();
                ExternalCoolingSelfTest.Require(panel.Visible&&portDraft.Value==8766&&form.ExternalCoolingStatus?.SessionId==oldSession&&form.Canvas.Controls.OfType<ExternalCoolingSettingsPanel>().Count()==1,"Navigation lost editor draft or restarted/duplicated its connection.");
                portDraft.Value=oldPort;
                var details=(CheckBox)panel.Controls.Find("external-details",true).Single();details.Checked=true;Application.DoEvents();
                var messages=panel.Controls.Find("external-messages",true).Single();
                ExternalCoolingSelfTest.Require(messages.Visible&&messages.Text.Contains("ackSequence"),"Operational message details are unavailable.");
                RenderPage("external-page-details.png");
                var before=Volatile.Read(ref received);form.Hide();
                PumpUntil(()=>Volatile.Read(ref received)>=before+2,"Minimize/tray stopped telemetry.");
                var watch=System.Diagnostics.Stopwatch.StartNew();var suspend=form.HandlePowerEventAsync(4);
                ExternalCoolingSelfTest.Require(watch.ElapsedMilliseconds<200,"Network blocked suspend GUI.");
                PumpUntil(()=>suspend.IsCompleted,"Suspend cleanup did not drain.");suspend.GetAwaiter().GetResult();
                var oldSessions=Volatile.Read(ref sessions);var resume=form.HandlePowerEventAsync(18);
                PumpUntil(()=>resume.IsCompleted&&Volatile.Read(ref sessions)>oldSessions&&form.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Connected,"Resume did not authenticate new session.");
                ExternalCoolingSelfTest.Require(runtime.Commands==0,"External module issued internal commands.");
                var exit=form.RequestExitAsync();PumpUntil(()=>exit.IsCompleted,"External cooling exit did not complete.");exit.GetAwaiter().GetResult();
                ExternalCoolingSelfTest.Require(form.ExternalCoolingStatus?.State==ExternalCoolingConnectionState.Disabled&&runtime.Disposals==1,"Network or runtime remained after exit.");
                Console.WriteLine("PASS ExternalCooling WinForms: DPAPI, persistence, sidebar order, embedded responsive page, enable draft, details, navigation/session isolation, real authenticated TCP, hidden/tray transmission, suspend/resume/new session, exit; zero internal control commands.");
            }
            finally{cancel.Cancel();listener.Stop();server.GetAwaiter().GetResult();CryptographicOperations.ZeroMemory(raw);}
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
