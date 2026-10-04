using System.Reflection;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.App;

/// <summary>Real Windows controls/rendering with a recording backend; never constructs hardware readers.</summary>
internal static class DashboardSelfTest
{
    internal static int Run()
    {
        try
        {
            static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Dashboard assertion failed."); }
            static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root) =>
                root.Controls.Cast<System.Windows.Forms.Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
            var hardware = new HardwareIdentity(Hp8C40TargetProfile.BoardManufacturer, Hp8C40TargetProfile.BoardProduct,
                Hp8C40TargetProfile.BoardVersion, Hp8C40TargetProfile.SystemManufacturer, Hp8C40TargetProfile.SystemProductName,
                Hp8C40TargetProfile.SystemSkuPrefix+"#AKH", Hp8C40TargetProfile.ValidatedBiosVersion);
            var backend = new RecordingBackend();
            var coordinator = new FanControlCoordinator(backend);
            var configuration = new FanConfiguration();
            var controller = new AdaptiveFanProductionController(coordinator, Hp8C40AdaptiveCandidateV1.Create(), true, false,
                automaticHardware: hardware, automaticConfiguration: configuration);
            var applies = 0;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var settings = new FanSettingsPanel(configuration, async c =>
            {
                applies++;
                await release.Task;
                await controller.ConfigureAutomaticAsync(c,CancellationToken.None);
            });
            var ventilation = new P13FanControlSurface(controller,hardware,"HP 8C40 / F.18",()=>null,_=>{});
            var monitor = new Panel();
            monitor.Controls.Add(new TelemetryHistoryChart());
            using var host = new Form { Text="Victus Fan Control",ClientSize=new Size(1240,880),MinimumSize=new Size(1040,700) };
            var shell = new DashboardShell(("Monitor",monitor),("Ventilación",ventilation),("Ajustes",settings),
                ("Diagnósticos",new Label {Text="No hay una captura en curso.",AutoSize=true}));
            host.Controls.Add(shell);DashboardTheme.Apply(host);host.Show();Application.DoEvents();
            var navigation = Descendants(shell).OfType<Button>().Where(b=>b.AccessibleName?.StartsWith("Navegación:")==true).ToArray();
            navigation.Single(b=>b.Text=="Ajustes").PerformClick();Application.DoEvents();
            var numbers = (Dictionary<string,NumericUpDown>)typeof(FanSettingsPanel)
                .GetField("_numbers",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(settings)!;
            Require(numbers.Count==11 && controller.Mode==AdaptiveFanProductionMode.Firmware && applies==0);
            var axis = Descendants(settings).OfType<ComboBox>().Single();
            Require(axis.Items.Count==6);
            for(var i=0;i<6;i++){axis.SelectedIndex=i;Application.DoEvents();}
            axis.SelectedIndex=0;
            numbers[nameof(AdaptiveFanTuning.MinimumLevel)].Value=28;
            Require(controller.AutomaticConfiguration!.Tuning.MinimumLevel==26 && applies==0);
            var apply = (Task)typeof(FanSettingsPanel).GetMethod("ApplyAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(settings,null)!;
            Require(applies==1 && !settings.Enabled && controller.AutomaticConfiguration!.Tuning.MinimumLevel==26);
            release.SetResult();
            var timer=System.Diagnostics.Stopwatch.StartNew();
            while(!apply.IsCompleted && timer.ElapsedMilliseconds<5000){Application.DoEvents();Thread.Yield();}
            apply.GetAwaiter().GetResult();
            Require(settings.Enabled && controller.AutomaticConfiguration!.Tuning.MinimumLevel==28 && backend.Commands==0 && !controller.AutomaticExecutionAuthorized);
            Console.WriteLine("PASS: dashboard staged settings, six axes, async edit lock, Firmware-only apply and closed Automatic gate.");
            void Render(string file)
            {
                host.PerformLayout();Application.DoEvents();
                using var bitmap=new Bitmap(host.Width,host.Height);
                host.DrawToBitmap(bitmap,new Rectangle(Point.Empty,host.Size));bitmap.Save(file);
            }
            Render("dashboard-settings-ui.png");
            host.ClientSize=new Size(1040,700);Render("dashboard-settings-small-ui.png");
            Require(settings.AutoScroll && Descendants(settings).OfType<NumericUpDown>().All(n=>n.Width>=60));
            host.ClientSize=new Size(1240,880);
            navigation.Single(b=>b.Text=="Ventilación").PerformClick();Application.DoEvents();Render("dashboard-ventilation-ui.png");
            Require(controller.Mode==AdaptiveFanProductionMode.Firmware && backend.Commands==0);
            host.Close();coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Require(settings.IsDisposed && ventilation.IsDisposed && monitor.IsDisposed);
            Console.WriteLine("PASS: dashboard navigation does not change fan mode; all detached pages dispose; large/small Windows renders saved.");
            return 0;
        }
        catch(Exception ex){Console.WriteLine("FAIL: dashboard UI — "+ex);return 38;}
    }
    private sealed class RecordingBackend : IFanControlBackend
    {
        public string Name=>"dashboard-self-test";
        public bool CanWrite=>true;
        public FanBackendCapabilities Capabilities=>new(Hp8C40TargetProfile.BoardProduct,10,50,false);
        public int Commands {get;private set;}
        public ValueTask ProbeControlDependencyAsync(CancellationToken ct)=>ValueTask.CompletedTask;
        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken ct)=>ValueTask.FromResult(new FanBackendStatus(Name,true,false,true,true,"synthetic"));
        public ValueTask EnterCustomModeAsync(CancellationToken ct){Commands++;return ValueTask.CompletedTask;}
        public ValueTask ApplyAsync(FanCommand command,CancellationToken ct){Commands++;return ValueTask.CompletedTask;}
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken ct){Commands++;return ValueTask.CompletedTask;}
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
