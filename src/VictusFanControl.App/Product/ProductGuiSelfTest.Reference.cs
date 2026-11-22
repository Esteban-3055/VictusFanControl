using System.Drawing.Imaging;
using System.Text.Json;
using VictusFanControl.ExternalCooling;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunReferenceInterfaceChecks()
    {
        try
        {
            static void Require(bool ok,string message){if(!ok)throw new InvalidOperationException(message);}
            var output=Path.GetFullPath("reference-renders");Directory.CreateDirectory(output);
            var profiles=new ProductProfiles{Functions=new ProductFunctionPolicies().Independent()};
            var commands=0;
            using var icons=new ProductIcons();
            Require(icons.Program.Width==32&&icons.Default.Width>0&&icons.Automatic.Width>0&&icons.AutomaticMuted.Width>0&&icons.Error.Width>0,
                "The original multi-resolution icon containers could not load.");
            // Windows caps a top-level window to the desktop's tracking limits.
            // Keep a small host and size the child canvas independently so a
            // 3344px render remains 3344px on a smaller hosted CI desktop too.
            using var form=new Form{AutoScaleMode=AutoScaleMode.None,FormBorderStyle=FormBorderStyle.None,StartPosition=FormStartPosition.Manual,Location=Point.Empty,ClientSize=new(1040,660)};
            var canvas=new ProductCanvas{Profiles=profiles,Dock=DockStyle.None,StartupKnown=true,StartupEnabled=true};
            var externalSettings=new ExternalCoolingSettings();
            var manager=new ExternalCoolingManager(new TelemetryExportService(()=>canvas.State,new WindowsPhysicalMemoryUsage()),()=>null);
            using var external=new ExternalCoolingPage(canvas,externalSettings,manager,()=>false,(_,_,_,_)=>Task.CompletedTask);
            using var hyperx=new HyperXBatteryPage(canvas,new(),_=>Task.CompletedTask);
            canvas.CoolingPage=external;canvas.HyperX=hyperx;
            canvas.ExternalCurve=new(canvas,()=>externalSettings,_=>Task.FromResult<string?>(null),()=>manager.Diagnostics);
            var anchor=DateTimeOffset.UtcNow;
            for(var i=150;i>=1;i--)
            {
                var at=anchor.AddSeconds(-i*2);var percent=40+Math.Abs(Math.Sin(i*.31))*5+(i%29==0?35:i%13==0?19:0);
                canvas.ObserveHomeDemand(ProductHomeTelemetrySelfTest.StateAt(at,percent),at);
            }
            canvas.Command+=_=>commands++;form.Controls.Add(canvas);form.Show();Application.DoEvents();
            var frames=new List<object>();
            foreach(var size in new[]{new Size(1672,941),new Size(1040,660),new Size(2090,1176),new Size(2508,1412),new Size(3344,1882)})
            {
                canvas.Size=size;Application.DoEvents();
                foreach(var page in Enum.GetValues<ProductPage>())
                {
                    var now=DateTimeOffset.UtcNow;var state=ProductHomeTelemetrySelfTest.StateAt(now) with
                    {Hardware="Victus by HP Gaming Laptop 15-fa1xxx (8C40)",Target="HP-8C40-9D0R1LA-F18",FunctionPolicies=profiles.FunctionPolicies(),
                        FunctionCoordinationExplicit=true,FunctionRequests=new(true,true,true,true),CpuState="Active",GpuState="ActiveUnverified",CtgpState="Active"};
                    canvas.State=state;canvas.Page=page;canvas.Editing=ProductPowerProfile.Ac;
                    canvas.Notice="";canvas.Refresh();Application.DoEvents();
                    Require(canvas.Size==size,"The renderer did not retain the requested reference viewport.");
                    using var image=new Bitmap(size.Width,size.Height);canvas.DrawToBitmap(image,new(Point.Empty,size));
                    var filename=$"{page}-{size.Width}x{size.Height}.png";image.Save(Path.Combine(output,filename),ImageFormat.Png);
                    Require(canvas.Hits.All(h=>h.Bounds.Left>=0&&h.Bounds.Top>=0&&h.Bounds.Right<=1673&&h.Bounds.Bottom<=942),"Reference theme introduced an off-surface action.");
                    Require(canvas.Hits.Count(h=>h.Id.StartsWith("page-",StringComparison.Ordinal))==ProductCanvas.Navigation.Count,"Reference navigation lost an action.");
                    Require(canvas.Hits.GroupBy(h=>h.Id).All(group=>group.Count()==1),$"{page} introduced duplicate action identities: "+string.Join(", ",canvas.Hits.GroupBy(h=>h.Id).Where(group=>group.Count()>1).Select(group=>group.Key)));
                    if(size.Width==1672)
                        Require(Enumerable.Range(24,20).Any(x=>image.GetPixel(x,911).R>100),"A page erased the persistent footer status.");
                    frames.Add(new{page=page.ToString(),width=size.Width,height=size.Height,file=filename});
                }
            }
            Require(commands==0,"Rendering the reference theme dispatched a controller action.");
            // Inspect the same embedded ICO representations Windows uses in the tray.
            using(var atlas=new Bitmap(580,210))
            using(var graphics=Graphics.FromImage(atlas))
            using(var font=new Font("Segoe UI",12,GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.FromArgb(18,18,18));var column=0;
                foreach(var item in new[]{("Normal","tray-default"),("Automático","tray-automatic"),("Silenciado","tray-automatic-muted"),("Fallo","tray-error")})
                {
                    graphics.DrawString(item.Item1,font,Brushes.White,10+column*145,8);
                    foreach(var size in new[]{16,20,24,32})
                    {using var stream=typeof(ProductIcons).Assembly.GetManifestResourceStream("VictusFanControl.Icons."+item.Item2+".ico")!;
                        using var icon=new Icon(stream,new Size(size,size));var y=32+Array.IndexOf(new[]{16,20,24,32},size)*42;
                        graphics.DrawIcon(icon,new Rectangle(12+column*145,y,size,size));graphics.DrawString(size.ToString(),font,Brushes.Silver,55+column*145,y+4);}
                    column++;
                }
                atlas.Save(Path.Combine(output,"tray-icons.png"),ImageFormat.Png);
            }
            File.WriteAllText(Path.Combine(output,"manifest.json"),JsonSerializer.Serialize(new{frames,hardwareCommands=commands,
                measurements="Synthetic visual fixture; no user-device measurement is shown",
                scope="actual development WinForms renders at reference/minimum and 125/150/200 percent pixel sizes; not physical monitor-DPI or hardware validation"},new JsonSerializerOptions{WriteIndented=true}));
            manager.DisposeAsync().AsTask().GetAwaiter().GetResult();form.Close();
            Console.WriteLine("PASS 70 reference-theme WinForms renders of all 14 pages, unique actions, persistent footer, bounded hit geometry and tray-icon atlas; reference/minimum/125/150/200 percent pixel sizes; zero hardware commands.");
            return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
