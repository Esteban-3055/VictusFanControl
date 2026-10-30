using System.Drawing.Imaging;
using VictusFanControl.Product;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    internal static int RunHyperXChecks()
    {
        try
        {
            static void Check(bool ok,string detail){if(!ok)throw new InvalidOperationException(detail);}
            HyperXSelfTest.Run();
            var runtime=new RecordingRuntime();using var form=new ProductForm("fixture://hyperx",fixture:runtime,fixtureProfiles:new());
            form.Show();Application.DoEvents();int nativeChildren=form.Canvas.Controls.Count;
            var notebook=ProductProfilesStore.Serialize(form.Draft);
            form.HandleCommand("page-"+(int)ProductPage.HyperXBattery);Application.DoEvents();
            var page=form.Canvas.HyperX??throw new InvalidOperationException("HyperX model missing");
            Check(form.Canvas.Controls.Count==nativeChildren&&!typeof(System.Windows.Forms.Control).IsAssignableFrom(typeof(HyperXBatteryPage)),"HyperX added a separate native settings surface");
            var now=DateTimeOffset.UtcNow;var reading=new HyperXSnapshot{ReceiverPresent=true,Status="Lectura de prueba",Percent=55,Millivolts=3795,SampledAt=now,Muted=true,Charging=false};
            var dir=Path.GetFullPath("logs/hyperx-self-test");Directory.CreateDirectory(dir);
            foreach(var size in new[]{new Size(1344,756),new Size(1040,660),new Size(1672,941)})
            foreach(var tab in new[]{"info","notifications","lcd"})
            {
                form.ClientSize=size;Application.DoEvents();form.HandleCommand("hyperx-"+tab);page.UpdateReading(reading,now);form.Canvas.Refresh();
                var hits=form.Canvas.Hits.Where(h=>h.Id.StartsWith("hyperx-")).ToArray();
                Check(hits.Select(h=>h.Id).Distinct().Count()==hits.Length&&hits.All(h=>h.Bounds.Left>=308&&h.Bounds.Right<=1648&&h.Bounds.Top>=80&&h.Bounds.Bottom<=859),"HyperX controls clipped, duplicated or need scrolling");
                Check(hits.Any(h=>h.Id=="hyperx-save")&&hits.Any(h=>h.Id=="hyperx-discard"),"Save/discard not visible on every tab");
                using var bitmap=new Bitmap(form.Width,form.Height);form.DrawToBitmap(bitmap,new(0,0,form.Width,form.Height));
                bitmap.Save(Path.Combine(dir,$"hyperx-{tab}-{size.Width}.png"),ImageFormat.Png);
                if(tab=="info")bitmap.Save(Path.Combine(dir,$"hyperx-{size.Width}.png"),ImageFormat.Png);
            }
            form.HandleCommand("hyperx-notifications");form.Canvas.Refresh();
            var slider=form.Canvas.Hits.Single(h=>h.Id=="hyperx-value-low");
            int childIndex=form.Canvas.Hits.ToList().FindIndex(h=>h.Id==slider.Id);
            var accessible=form.Canvas.AccessibilityObject.GetChild(childIndex)!;
            Check(accessible.Role==AccessibleRole.Slider&&accessible.Value=="20","HyperX accessible numeric value missing");
            accessible.Value="25";Check(page.Value("low")==25,"Accessible slider changed the wrong setting");
            accessible.Select(AccessibleSelection.TakeFocus);form.Canvas.HandleKey(Keys.Right);Check(page.Value("low")==26,"Keyboard slider lost its current value");
            form.HandleCommand("hyperx-value-low-minus");Check(page.Value("low")==25,"Minus button failed");
            var draft=page.Draft;
            foreach(var bad in new[]{"25.5","NaN","","100","-1"})Check(page.EditNumeric("low",bad) is not null&&page.Draft==draft,"Invalid exact input silently changed HyperX settings");
            Check(page.EditNumeric("low","30") is null&&page.Value("low")==30,"Exact numeric input failed");
            form.HandleCommand("hyperx-low-enabled");Check(!page.Draft.LowEnabled,"Notification toggle failed");
            form.HandleCommand("hyperx-discard");Check(!page.Dirty&&page.Draft.LowEnabled&&page.Value("low")==20,"Discard did not restore saved settings");
            form.EditValue("hyperx-value-low-seconds",8);form.EditValue("hyperx-value-low-cycles",4);
            form.EditValue("hyperx-value-critical-seconds",12);form.EditValue("hyperx-value-critical-cycles",6);
            page.SaveAsync().GetAwaiter().GetResult();
            Check(!page.Dirty&&page.Draft.DisplayConfiguration() is{LowDurationMs:8000,LowBlinkCycles:4,CriticalDurationMs:12000,CriticalBlinkCycles:6},"LCD settings lost while saving themed page");
            page.EditValue("critical",30);page.EditValue("low",20);page.EditValue("full",20);page.SaveAsync().GetAwaiter().GetResult();
            Check(page.Dirty&&!page.Busy&&!page.Notice.Contains("guardados"),"Invalid threshold combination saved silently");
            page.ExecuteAsync("discard").GetAwaiter().GetResult();
            Check(page.Value("critical")==10&&page.Value("low")==20,"Rejected thresholds changed the saved configuration");
            foreach(var endpoint in new[]{0,100})
            {
                Check(page.EditNumeric("critical",endpoint.ToString()) is null,"Critical endpoint rejected by exact editor");
                page.SaveAsync().GetAwaiter().GetResult();
                Check(!page.Dirty&&page.Draft.DisplayConfiguration().CriticalPercent==endpoint,"Critical endpoint could not be saved from UI");
            }
            foreach(var bad in new[]{"-1","101","1.5"})Check(page.EditNumeric("critical",bad) is not null,"Critical exact editor accepted an invalid percentage");
            page.EditValue("critical",10);page.SaveAsync().GetAwaiter().GetResult();
            var saved=page.Draft;
            using(var failed=new HyperXBatteryPage(form.Canvas,saved,_=>Task.FromException(new IOException("fixture only"))))
            {
                failed.EditValue("low",25);failed.SaveAsync().GetAwaiter().GetResult();
                Check(failed.Dirty&&!failed.Busy&&failed.Notice.Contains("No se pudieron"),"Failed persistence discarded the draft or claimed success");
            }
            var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var pending=new HyperXBatteryPage(form.Canvas,saved,_=>held.Task))
            {
                form.Canvas.HyperX=pending;pending.Selected=true;pending.EditValue("low",25);pending.ExecuteAsync("notifications").GetAwaiter().GetResult();
                var save=pending.SaveAsync();var frozen=pending.Draft;pending.EditValue("full",95);pending.ExecuteAsync("discard").GetAwaiter().GetResult();form.Canvas.Refresh();
                Check(pending.Busy&&pending.Draft==frozen&&form.Canvas.Hits.Where(h=>h.Id.StartsWith("hyperx-")).All(h=>!h.Enabled),"Busy HyperX save admitted edits or double-save");
                held.SetResult();PumpUntil(()=>save.IsCompleted,"HyperX save did not drain");Check(!pending.Busy&&!pending.Dirty,"Successful save retained dirty/busy state");
            }
            form.Canvas.HyperX=page;form.HandleCommand("hyperx-lcd");form.Canvas.Refresh();
            var drag=form.Canvas.Hits.Single(h=>h.Id=="hyperx-value-low-seconds");
            Point PointAt(float x,float y){var scale=Math.Min(form.Canvas.Width/1672f,form.Canvas.Height/941f);return new((int)((form.Canvas.Width-1672*scale)/2+x*scale),(int)((form.Canvas.Height-941*scale)/2+y*scale));}
            var point=PointAt(drag.Bounds.Right-5,drag.Bounds.Y+15);form.Canvas.PointerDown(point);
            form.HandleCommand("hyperx-info");var afterNavigation=page.Draft;form.Canvas.PointerMove(PointAt(drag.Bounds.Left,drag.Bounds.Y));form.Canvas.PointerUp(point);
            Check(page.Draft==afterNavigation&&!form.Canvas.Capture,"Tab switch left a hidden slider dragging");
            page.UpdateReading(reading with{SampledAt=now.AddSeconds(-180)},now);Check(!page.Reading.Fresh(page.PresentedAt),"Stale state rendered as live");
            form.HandleCommand("page-"+(int)ProductPage.ExternalCurve);Application.DoEvents();
            Check(!page.Selected&&runtime.Commands==0&&ProductProfilesStore.Serialize(form.Draft)==notebook,"HyperX editing/navigation changed notebook preferences or hardware authority");
            form.Hide();Console.WriteLine("PASS HyperX GUI: common canvas cards, three tabs, nine renders, no native settings overlay/scrolling, keyboard/accessibility, exact input, save/discard/error/busy and hidden-drag lifecycle; zero hardware commands.");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
