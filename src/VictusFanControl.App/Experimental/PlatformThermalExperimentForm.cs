using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.OemShadow;
using VictusFanControl.OemShadowCapture;
using VictusFanControl.PlatformThermalReplay;
using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed class PlatformThermalExperimentForm : Form
{
    private readonly string _modules,_output;
    private readonly Label _status=new(){Dock=DockStyle.Fill,AutoSize=false,Font=new Font("Segoe UI",12),Padding=new Padding(16)};
    private readonly Button _start=new(){Text="Iniciar prueba física (43 min)",AutoSize=true};
    private readonly Button _stop=new(){Text="Detener y liberar controles",AutoSize=true,Enabled=false};
    internal readonly NumericUpDown CpuPl1=new(){Minimum=CpuPowerProductDefaults.MinimumPl1Watts,Maximum=CpuPowerProductDefaults.MaximumConfigurablePl1Watts,Value=25,Width=85};
    internal readonly NumericUpDown CpuPl2=new(){Minimum=CpuPowerProductDefaults.MinimumPl2Watts,Maximum=60,Value=30,Width=85};
    internal readonly NumericUpDown GpuMaximum=new(){Minimum=GpuProductPreferences.MinimumMHz,Maximum=GpuProductPreferences.ConfigurableMaximumMHz,Width=85};
    private ProductProfiles? _initialProfiles,_frozenProfiles;
    private string _profileSource="";
    private readonly System.Windows.Forms.Timer _timer=new(){Interval=500};
    private readonly CancellationTokenSource _cancel=new();
    private readonly Stopwatch _clock=new();
    private ProductRuntime? _runtime;
    private PhysicalPlatformExperiment? _experiment;
    private Task? _run;
    private bool _starting,_closing,_disposed,_wakeRequested;
    private volatile bool _finished;
    private IntPtr _displayRegistration,_suspendRegistration;
    private const int PowerBroadcast=0x218;
    private static readonly Guid DisplayGuid=new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    [StructLayout(LayoutKind.Sequential)]private struct PowerSetting{internal Guid Id;internal uint Length;}
    internal static void Run(string modules,string output)
    {
        using var form=new PlatformThermalExperimentForm(modules,output);Application.Run(form);
    }
    internal PlatformThermalExperimentForm(string modules,string output,ProductProfiles? fixtureProfiles=null)
    {
        _modules=modules;_output=Path.GetFullPath(output);
        Text="VictusFanControl · curva experimental física";ClientSize=new(850,600);MinimumSize=new(800,580);StartPosition=FormStartPosition.CenterScreen;AutoScaleMode=AutoScaleMode.Dpi;
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=55,Padding=new Padding(10)};buttons.Controls.AddRange([_start,_stop]);var limits=new GroupBox{Text="Límites AC para esta prueba",Dock=DockStyle.Bottom,Height=115,Padding=new Padding(10)};
        var values=new TableLayoutPanel{Dock=DockStyle.Top,Height=38,ColumnCount=3,RowCount=1};
        foreach(var (text,field) in new[]{("CPU PL1 (W)",CpuPl1),("CPU PL2 (W)",CpuPl2),("GPU máx. (MHz)",GpuMaximum)})
        {
            values.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100f/3));
            var cell=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false};
            cell.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(3,7,6,3)});cell.Controls.Add(field);values.Controls.Add(cell);
        }
        var hint=new Label{Dock=DockStyle.Bottom,Height=40,Text="PL2 ≥ PL1. CPU inicial 25/30 W; GPU tomada del perfil. Ambos controles habilitados.\nSe fijan al iniciar, se liberan al terminar y no cambian tus preferencias guardadas."};
        limits.Controls.Add(values);limits.Controls.Add(hint);Controls.Add(_status);Controls.Add(limits);Controls.Add(buttons);
        _status.Text="Prueba AC: 2 min Firmware + cuatro bloques de 9 min (Actual / Experimental / Experimental / Actual) + 5 min Firmware.\n\nCada bloque: 2 min reposo, 4 min con la misma carga que tú iniciarás, 3 min enfriamiento. No se genera carga automáticamente.\n\nSe usa tu curva guardada (o la predeterminada si no hay archivo). Elige abajo los límites CPU/GPU; se aplican mediante el Guardian existente. La candidata retiene ventilación durante el enfriamiento usando TZ01 y DTT3.\n\nCierra otras aplicaciones Victus y otros controladores de reloj GPU. Mantén el cargador conectado y la pantalla encendida. Al detener o finalizar se liberan ventiladores y límites.\n\nEvidencia: "+_output;
        try
        {
            _profileSource=fixtureProfiles is not null?"synthetic-fixture":File.Exists(ProductProfilesStore.DefaultPath)?"saved-product-profiles":"default-product-profiles";
            _initialProfiles=ProductProfilesStore.Copy(fixtureProfiles??(_profileSource=="saved-product-profiles"?ProductProfilesStore.Parse(File.ReadAllText(ProductProfilesStore.DefaultPath)):new ProductProfiles()));
            GpuMaximum.Value=_initialProfiles.Ac.GpuMaximumMHz;
            _status.Text+="\nCurva: "+(_profileSource=="saved-product-profiles"?"perfil AC guardado":_profileSource=="synthetic-fixture"?"fixture sin hardware":"perfil AC predeterminado");
        }
        catch(Exception ex){_status.Text="No se pudo cargar la curva: "+ex.Message;_start.Enabled=false;}
        _start.Click+=async(_,_)=>await StartAsync();_stop.Click+=(_,_)=>Stop("Detención solicitada por el usuario");
        _timer.Tick+=(_,_)=>RefreshStatus();
        FormClosing+=async(_,e)=>
        {
            if(_disposed)return;e.Cancel=true;if(_closing)return;_closing=true;Stop("Cierre de ventana");
            if(_run is not null)await _run;
            while(_starting)await Task.Delay(25);
            _disposed=true;Close();
        };
        FormClosed+=(_,_)=>{_timer.Dispose();if(_displayRegistration!=IntPtr.Zero)UnregisterPowerSettingNotification(_displayRegistration);
            if(_suspendRegistration!=IntPtr.Zero)UnregisterSuspendResumeNotification(_suspendRegistration);ReleaseWakeRequest();_experiment?.Dispose();_cancel.Dispose();};
    }
    internal ProductProfiles FreezeSelectedProfiles()
    {
        if(_frozenProfiles is not null)return ProductProfilesStore.Copy(_frozenProfiles);
        if(_initialProfiles is null)throw new InvalidOperationException("No hay un perfil válido para la prueba.");
        ValidateChildren();
        var profiles=ProductProfilesStore.Copy(_initialProfiles) with
        {
            Ac=_initialProfiles.Ac with{CpuPl1Watts=(int)CpuPl1.Value,CpuPl2Watts=(int)CpuPl2.Value,GpuMaximumMHz=(int)GpuMaximum.Value},
            CpuEnabled=true,GpuEnabled=true
        };
        profiles.Validate();
        _frozenProfiles=ProductProfilesStore.Copy(profiles);
        CpuPl1.Enabled=CpuPl2.Enabled=GpuMaximum.Enabled=false;
        return ProductProfilesStore.Copy(_frozenProfiles);
    }
    private void CheckEntry()
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Solo Windows.");
        if(Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString",null)?.ToString()?.Contains("i7-13700H",StringComparison.OrdinalIgnoreCase)!=true)
            throw new InvalidOperationException("CPU i7-13700H requerida.");
        using var identity=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("Abre PowerShell como administrador.");
        var hardware=HardwareIdentityReader.ReadCurrent();if(!Hp8C40TargetProfile.Matches(hardware,out var reason))throw new InvalidOperationException("Destino no autorizado: "+reason);
        const string hash="d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";
        var actualHash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_modules,"IntelMSR.bin")))).ToLowerInvariant();
        if(actualHash!=hash)throw new InvalidOperationException("IntelMSR.bin no corresponde al módulo validado.");
        foreach(var process in Process.GetProcesses())using(process)
        {
            string name;try{name=process.ProcessName;}catch(InvalidOperationException){continue;}
            if(process.Id!=Environment.ProcessId&&name.StartsWith("VictusFanControl",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cierra normalmente otros GUI/Guardian/watchdog/readers Victus antes de iniciar.");
        }
        if(new VictusFanControl.Performance.WindowsPerformancePowerSourceReader().Read().Source!=VictusFanControl.Performance.PerformancePowerSourceKind.Ac)
            throw new InvalidOperationException("Esta prueba requiere alimentación AC.");
        _displayRegistration=RegisterPowerSettingNotification(Handle,ref DisplayGuidForRegistration,0);
        _suspendRegistration=RegisterSuspendResumeNotification(Handle,0);
        if(_displayRegistration==IntPtr.Zero||_suspendRegistration==IntPtr.Zero)throw new InvalidOperationException("No se pudieron registrar ambas barreras de suspensión/pantalla.");
    }
    private Guid DisplayGuidForRegistration=DisplayGuid;
    private async Task StartAsync()
    {
        if(_starting||_run is not null)return;
        ProductProfiles profiles;
        try{profiles=FreezeSelectedProfiles();}
        catch(InvalidDataException ex){_status.Text="Revisa los límites antes de iniciar: "+ex.Message;return;}
        _starting=true;_start.Enabled=false;
        try
        {
            CheckEntry();
            if(SetThreadExecutionState(0x80000003)==0)throw new InvalidOperationException("No se pudo mantener la pantalla activa durante la prueba.");
            _wakeRequested=true;
            _experiment=new(_output,profiles.Ac.Fan,physicalExecution:true);
            File.WriteAllText(Path.Combine(_output,"profiles-before-test.json"),ProductProfilesStore.Serialize(_initialProfiles!));
            File.WriteAllText(Path.Combine(_output,"profiles.json"),ProductProfilesStore.Serialize(profiles));
            File.WriteAllText(Path.Combine(_output,"metadata.json"),JsonSerializer.Serialize(new{startedUtc=DateTimeOffset.UtcNow,
                sourceRevision=typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                appMvid=typeof(Program).Assembly.ManifestModule.ModuleVersionId,coreMvid=typeof(Hp8C40TargetProfile).Assembly.ManifestModule.ModuleVersionId,appSession=AppLog.SessionIdentity,
                moduleHash=hashForMetadata,profilesSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_output,"profiles.json")))).ToLowerInvariant(),
                cpu="i7-13700H",target="HP-8C40-9D0R1LA-F18",normalAutomaticPromoted=false,
                profileSource=_profileSource,performanceSelection=new{cpuPl1Watts=profiles.Ac.CpuPl1Watts,cpuPl2Watts=profiles.Ac.CpuPl2Watts,gpuMaximumMHz=profiles.Ac.GpuMaximumMHz,
                    cpuEnabled=true,gpuEnabled=true,scope="this-trial-only;frozen-for-all-ABBA-blocks"},
                originalProfilesSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_output,"profiles-before-test.json")))).ToLowerInvariant()},PhysicalPlatformExperiment.Json));
            _runtime=await Task.Run(()=>new ProductRuntime(_modules,profiles,ProductAutomaticReviewMode.Extended,_experiment));
            if(_cancel.IsCancellationRequested){await CleanupAsync("Interrumpido durante startup");return;}
            _runtime.Start();_stop.Enabled=true;_clock.Start();_timer.Start();
            _run=Task.Run(()=>RunSequenceAsync(profiles));
        }
        catch(Exception ex){AppLog.Write("Platform physical startup failed: "+ex);_status.Text="No se pudo iniciar: "+ex.Message;Environment.ExitCode=173;await CleanupAsync(ex.Message);ReleaseWakeRequest();_finished=true;}
        finally{_starting=false;}
    }
    private const string hashForMetadata="d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";
    private async Task RunSequenceAsync(ProductProfiles profiles)
    {
        using var auxStop=CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        var aux=Task.Run(async()=>
        {
            var tz=new ReadSlot<Source>();var dtt=new ReadSlot<DttSample>();
            try
            {
                while(!auxStop.IsCancellationRequested)
                {
                    var elapsed=(long)_clock.ElapsedMilliseconds;tz.Poll(elapsed,OemSources.ReadTz);dtt.Poll(elapsed,OemSources.ReadDtt);
                    _experiment!.SetSources(tz.Latest??new(),dtt.Latest?.Dtt1??new(),dtt.Latest?.Dtt2??new(),dtt.Latest?.Dtt3??new());
                    if(elapsed%1000<250)_experiment.RecordHost("source-health",new{elapsedMilliseconds=elapsed,tzError=tz.Error,dttError=dtt.Error,tzPending=tz.InFlight,dttPending=dtt.InFlight,
                        tzProgress=tz.CaptureProgress(),dttProgress=dtt.CaptureProgress()});
                    await Task.Delay(250,auxStop.Token);
                }
            }
            catch(OperationCanceledException) { }
            catch(Exception ex){_experiment!.Close("Auxiliary sampler failed: "+ex.Message);}
            finally{_experiment!.RecordHost("terminal-sources",new{tzPending=tz.InFlight,dttPending=dtt.InFlight,
                tzProgress=tz.CaptureProgress(),dttProgress=dtt.CaptureProgress()});}
        });
        int previousStage=int.MinValue;string reason="Protocolo completado";
        try
        {
            while(!_cancel.IsCancellationRequested&&_clock.Elapsed.TotalSeconds<ExperimentProtocol.TotalSeconds)
            {
                var stage=ExperimentProtocol.At((int)_clock.Elapsed.TotalSeconds);
                if(stage.Index!=previousStage)
                {
                    await _runtime!.SetExperimentalStageAsync(stage);
                    if(stage.Index==0)
                    {
                        if(!_experiment!.Ready||_runtime.State.Runtime!="Healthy")throw new InvalidOperationException("No hay telemetría y TZ01/DTT3 cualificados tras los dos minutos iniciales.");
                        await _runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Automatic,profiles);
                    }
                    else if(stage.Index==4)
                    {
                        await _runtime.SelectFanModeAsync(AdaptiveFanProductionMode.Firmware,profiles);
                        await _runtime.ReleasePerformanceAsync();
                        _experiment!.RecordHost("release-before-final-cooling",_runtime.State);
                    }
                    previousStage=stage.Index;
                }
                else await _runtime!.SetExperimentalStageAsync(stage);
                if(_experiment!.Failure is {} failure)throw new InvalidOperationException(failure);
                if(_runtime!.State.LifecycleBlocked)throw new InvalidOperationException(_runtime.State.LifecycleBlockReason??"Sesión interrumpida");
                if(stage.Custom&&(_runtime.State.FanMode!="Automatic"||_runtime.State.Failure is not null))throw new InvalidOperationException(_runtime.State.Failure??"Automatic terminó antes de la etapa prevista");
                await Task.Delay(1000,_cancel.Token);
            }
            if(_cancel.IsCancellationRequested)reason=_experiment!.Failure??"Detenido";
        }
        catch(OperationCanceledException){reason=_experiment!.Failure??"Detenido";}
        catch(Exception ex){reason=ex.Message;_experiment!.Close(reason);Environment.ExitCode=173;}
        finally
        {
            auxStop.Cancel();await aux;
            await CleanupAsync(reason);_clock.Stop();_finished=true;
        }
    }
    private async Task CleanupAsync(string reason)
    {
        bool succeeded=true;string? failure=null;
        _runtime?.FenceLifecycle("Fin del experimento: "+reason);
        if(_runtime is not null&&_experiment is not null)
        {
            try{ProductDiagnostics.Export(Path.Combine(_output,"gui-at-stop.zip"),_runtime.State,
                ProductProfilesStore.Parse(File.ReadAllText(Path.Combine(_output,"profiles.json"))),AppLog.CurrentLogPath);}
            catch(Exception ex){_experiment.RecordHost("diagnostic-export-failed",new{message=ex.Message});}
        }
        try{if(_runtime is not null){await _runtime.DisposeAsync();}}
        catch(Exception ex){succeeded=false;failure=ex.ToString();Environment.ExitCode=174;}
        finally{_experiment?.RecordHost("cleanup",new{succeeded,failure,reason,atUtc=DateTimeOffset.UtcNow});_experiment?.Complete(new{succeeded,failure,reason},
            succeeded&&_clock.Elapsed.TotalSeconds>=ExperimentProtocol.TotalSeconds&&reason=="Protocolo completado");}
    }
    private void Stop(string reason)
    {
        _experiment?.Close(reason);_runtime?.FenceLifecycle(reason);_cancel.Cancel();
    }
    private void RefreshStatus()
    {
        if(_experiment is null)return;
        var stage=ExperimentProtocol.At((int)_clock.Elapsed.TotalSeconds);var state=_runtime?.State;var sample=state?.Snapshot;
        _status.Text=(_finished?"Prueba terminada. ":"Prueba en curso. ")+"Etapa: "+stage.Controller+" · "+stage.Activity+"\n"+
            $"{(stage.Custom?$"Bloque {stage.Index+1}/4":"Fase Firmware")} · tiempo de etapa {stage.RemainingSeconds}s · total {(int)_clock.Elapsed.TotalSeconds}/{ExperimentProtocol.TotalSeconds}s\n\n"+
            $"CPU {sample?.CpuControlTemperatureC:0.#} °C · GPU {sample?.GpuTemperatureC:0.#} °C\n"+
            $"Límites AC fijos: CPU {_frozenProfiles?.Ac.CpuPl1Watts}/{_frozenProfiles?.Ac.CpuPl2Watts} W · GPU ≤{_frozenProfiles?.Ac.GpuMaximumMHz} MHz\n"+
            $"Ventiladores observados: CPU {sample?.CpuFanSpeedLevel} / GPU {sample?.GpuFanSpeedLevel}\n"+
            $"Objetivo aceptado: {state?.AutomaticDecision?.EqualFanLevel} · autoridad {state?.FanAuthority}\n"+
            $"Telemetría: {state?.Runtime} · TZ01/DTT3: {(_experiment.Ready?"cualificados":"no disponibles")}\n\n"+
            (_experiment.Failure??state?.Failure??state?.Message)+"\n\nRepite la misma carga en los cuatro bloques y ciérrala al indicar Enfriamiento.\nEvidencia: "+_output;
        if(_finished){ReleaseWakeRequest();_stop.Enabled=false;_timer.Stop();}
    }
    private void ReleaseWakeRequest()
    {
        // SetThreadExecutionState is thread-affine: both acquisition and release run on this form's UI thread.
        if(!_wakeRequested)return;SetThreadExecutionState(0x80000000);_wakeRequested=false;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint SetThreadExecutionState(uint flags);
    protected override void WndProc(ref Message m)
    {
        if(m.Msg==PowerBroadcast&&(_starting||_run is not null)&&!_finished)
        {
            int code=m.WParam.ToInt32();bool interrupt=code==4;
            if(code==0x8013&&m.LParam!=IntPtr.Zero){var setting=Marshal.PtrToStructure<PowerSetting>(m.LParam);interrupt|=setting.Id==DisplayGuid&&setting.Length>=4&&Marshal.ReadInt32(m.LParam,Marshal.SizeOf<PowerSetting>())==0;}
            if(interrupt){Stop("Suspensión o apagado de pantalla: prueba interrumpida");if(code==4&&_run is not null)_run.GetAwaiter().GetResult();}
        }
        base.WndProc(ref m);
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient,ref Guid setting,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr RegisterSuspendResumeNotification(IntPtr recipient,uint flags);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool UnregisterSuspendResumeNotification(IntPtr handle);
}
