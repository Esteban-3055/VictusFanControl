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
        var privateMarker="opaque-private-peer-field";
        var safeLog=ProductForm.ExternalCoolingLogLine(new(){State=ExternalCoolingConnectionState.Reconnecting,LastFailureKind=ExternalCoolingFailureKind.NameResolution,
            Detail=privateMarker,SessionId=privateMarker,LastReceived=privateMarker,LastSent=privateMarker,Failures=2});
        ExternalCoolingSelfTest.Require(!safeLog.Contains(privateMarker)&&safeLog.Contains("NameResolution")&&safeLog.Contains("failures=2"),"External connection log leaked opaque data or hid network failure.");
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
                ExternalCoolingSelfTest.Require(externalIndex>0&&navigation[externalIndex-1].Label=="Avanzado"&&navigation[externalIndex+1].Label=="Curva Externa"&&navigation[externalIndex+2].Label=="HyperX Battery"&&navigation[externalIndex+3].Label=="Actualizaciones"&&
                    navigation.All(h=>h.Bounds.Bottom<882)&&!form.Canvas.Hits.Any(h=>h.Id=="external-cooling"),"External cooling navigation order/fit missing or obsolete Settings entry retained.");
                using(var image=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(image,new(Point.Empty,form.Size));image.Save(Path.Combine(root,"settings.png"),ImageFormat.Png);}
                var formCount=Application.OpenForms.Count;
                form.HandleCommand("page-"+(int)ProductPage.ExternalCooling);Application.DoEvents();
                var page=form.ExternalPage??throw new InvalidOperationException("External page unavailable.");
                void RenderPage(string filename)
                {
                    var editors=new[]{page.Host,page.Port,page.Key};
                    // DrawToBitmap includes hidden native children; omit them for the diagnostics capture.
                    if(page.Details)foreach(var editor in editors)form.Canvas.Controls.Remove(editor);
                    try
                    {
                        using var image=new Bitmap(form.Canvas.Width,form.Canvas.Height);
                        form.Canvas.DrawToBitmap(image,new(Point.Empty,form.Canvas.Size));image.Save(Path.Combine(root,filename),ImageFormat.Png);
                    }
                    finally{if(page.Details){foreach(var editor in editors)form.Canvas.Controls.Add(editor);page.Layout(true);}}
                }
                ExternalCoolingSelfTest.Require(form.Canvas.CoolingPage==page&&Application.OpenForms.Count==formCount&&form.Canvas.Controls.OfType<TextBox>().Count(c=>c.Name.StartsWith("external-"))==3,"Page is not rendered by the product canvas with only native text editing.");
                ExternalCoolingSelfTest.Require(page.Status.State==ExternalCoolingConnectionState.Connected,"Initial page lost current status.");
                form.Canvas.Dock=DockStyle.None;
                foreach(var size in new[]{new Size(1672,941),new Size(1280,800),new Size(1040,660),new Size(1000,560),new Size(3344,1882)})
                {
                    form.Canvas.Size=size;form.Canvas.Refresh();Application.DoEvents();
                    ExternalCoolingSelfTest.Require(new[]{page.Host,page.Port,page.Key}.All(c=>c.Bounds.Left>0&&c.Bounds.Right<form.Canvas.Width&&c.Bounds.Bottom<form.Canvas.Height),"Native editor escaped the common product viewport.");
                    ExternalCoolingSelfTest.Require(form.Canvas.Hits.Any(h=>h.Id=="external-mode-2")&&!form.Canvas.Hits.Single(h=>h.Id=="external-save").Enabled,"Mode segments or clean draft admission missing.");
                    RenderPage($"external-page-{size.Width}.png");
                }
                form.Canvas.Size=new(1280,800);Application.DoEvents();
                var profilesBefore=ProductProfilesStore.Serialize(form.Draft);var dirtyBefore=form.Canvas.Dirty;
                form.HandleCommand("external-mode-0");ExternalCoolingSelfTest.Require(page.Mode==ExternalCoolingMode.Disabled&&page.Dirty&&page.Status.State==ExternalCoolingConnectionState.Connected,"Draft mode changed the live connection.");
                form.HandleCommand("external-mode-2");ExternalCoolingSelfTest.Require(page.Mode==ExternalCoolingMode.MonitorAndRequest,"Mode segments did not select request export.");
                form.HandleCommand("external-discard");ExternalCoolingSelfTest.Require(page.Mode==ExternalCoolingMode.MonitorOnly&&!page.Dirty,"Discard did not restore only external settings.");
                form.HandleCommand("external-forget");ExternalCoolingSelfTest.Require(page.ForgetPending&&page.Paired&&page.Dirty,"Forget immediately destroyed saved pairing.");
                form.HandleCommand("external-discard");ExternalCoolingSelfTest.Require(!page.ForgetPending&&page.Paired&&!page.Dirty,"Discard did not cancel pending forget.");
                var oldSession=form.ExternalCoolingStatus?.SessionId;var beforePageChange=Volatile.Read(ref received);page.Port.Text="8766";
                form.HandleCommand("external-generate");var newKey=page.Key.Text;form.HandleCommand("external-reveal");
                ExternalCoolingSelfTest.Require(newKey.Length==44&&!page.Key.UseSystemPasswordChar,"New key generation/reveal missing.");
                form.HandleCommand("page-"+(int)ProductPage.Updates);Application.DoEvents();
                ExternalCoolingSelfTest.Require(!page.Host.Visible&&page.Key.UseSystemPasswordChar,"Editors overlay another page or reveal leaked on navigation.");
                PumpUntil(()=>Volatile.Read(ref received)>beforePageChange,"Leaving the page stopped communication.");
                form.HandleCommand("external-cooling");Application.DoEvents();
                ExternalCoolingSelfTest.Require(form.Canvas.Page==ProductPage.ExternalCooling&&page.Host.Visible&&page.Port.Text=="8766"&&page.Key.Text==newKey&&form.ExternalCoolingStatus?.SessionId==oldSession&&form.Canvas.Controls.OfType<TextBox>().Count(c=>c.Name.StartsWith("external-"))==3,"Legacy navigation lost the draft or restarted/duplicated the connection.");
                form.HandleCommand("external-discard");page.Port.Text="0";form.HandleCommand("external-save");
                PumpUntil(()=>!page.Busy,"Invalid port blocked GUI.");
                ExternalCoolingSelfTest.Require(page.Dirty&&form.ExternalCoolingStatus?.SessionId==oldSession,"Invalid port disconnected or overwrote saved configuration.");
                form.HandleCommand("external-discard");page.Key.Text="invalid-base64";form.HandleCommand("external-save");
                PumpUntil(()=>!page.Busy,"Invalid key blocked GUI.");
                ExternalCoolingSelfTest.Require(page.Dirty&&form.ExternalCoolingStatus?.SessionId==oldSession,"Invalid key disconnected or overwrote saved pairing.");
                form.HandleCommand("external-discard");form.HandleCommand("external-reconnect");
                form.HandleCommand("external-save");PumpUntil(()=>!page.Busy,"Save did not drain.");
                ExternalCoolingSelfTest.Require(!page.Dirty&&page.Status.State==ExternalCoolingConnectionState.Disconnected&&!page.Live,"Save did not persist and stop remote control or retained live device values.");
                var persisted=new ExternalCoolingSettingsStore(path);var reloaded=persisted.Load(out _);
                ExternalCoolingSelfTest.Require(reloaded.AutoReconnect==page.Reconnect&&persisted.Paired,"Save lost preferences or pairing.");
                form.HandleCommand("external-connection");PumpUntil(()=>!page.Busy&&page.Status.State==ExternalCoolingConnectionState.Connected,"Connect action did not authenticate.");
                form.HandleCommand("external-details");form.HandleCommand("external-message-received");form.Canvas.Refresh();Application.DoEvents();
                ExternalCoolingSelfTest.Require(page.Details&&!page.Key.Visible&&page.Message.Contains("ackSequence")&&!page.Message.Contains(newKey)&&form.Canvas.Hits.Single(h=>h.Id=="external-copy").Enabled,"Diagnostics/copy admission includes no valid operational message or leaked key.");
                RenderPage("external-page-details.png");
                form.HandleCommand("external-overview");form.Canvas.State=form.Canvas.State with{Source="Battery"};form.Canvas.Refresh();RenderPage("external-page-battery.png");
                ExternalCoolingSelfTest.Require(form.Canvas.Accent==ProductCanvas.Yellow&&ProductProfilesStore.Serialize(form.Draft)==profilesBefore&&form.Canvas.Dirty==dirtyBefore&&runtime.Commands==0,"External draft/theme affected notebook profiles or authority.");
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
                Console.WriteLine("PASS ExternalCooling WinForms: DPAPI, persistence, sidebar order, embedded responsive page, shared canvas/theme, mode segments, independent save/discard, pairing staging, validation, diagnostics/copy admission, navigation/session isolation, real authenticated TCP, hidden/tray transmission, suspend/resume/new session, exit; zero internal control commands.");
            }
            finally{cancel.Cancel();listener.Stop();server.GetAwaiter().GetResult();CryptographicOperations.ZeroMemory(raw);}
            return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
