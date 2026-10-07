using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.OemShadow;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.OemShadowCapture;
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if(args.SequenceEqual(new[]{"--self-test"}))return CaptureTests.Run();
            if(args.Length is not (7 or 9)||args[0]!="--live-read-only"||args[1]!="--modules-dir"||args[3]!="--output"||
                args[5]!="--duration-seconds"||(args.Length==9&&args[7]!="--parameters"))
                throw new ArgumentException("Usage: --self-test | --live-read-only --modules-dir directory --output new-directory --duration-seconds N (0 unlimited) [--parameters file.json]");
            double seconds=double.Parse(args[6],CultureInfo.InvariantCulture);
            if(!double.IsFinite(seconds)||seconds<0)throw new ArgumentException("Duration must be finite and nonnegative.");
            var parameters=args.Length==9?JsonSerializer.Deserialize<Parameters>(File.ReadAllText(args[8]),ShadowSession.Json)??throw new InvalidDataException("Empty parameters"):new();
            parameters.Validate();return Capture(Path.GetFullPath(args[2]),Path.GetFullPath(args[4]),seconds,parameters);
        }
        catch(Exception ex){Console.Error.WriteLine("OEM capture refused/failed: "+ex.Message);return 1;}
    }
    private static void NoOtherReaders()
    {
        foreach(var process in Process.GetProcesses())
        {
            using(process)
            {
                string name;try{name=process.ProcessName;}catch(InvalidOperationException){continue;}
                if(process.Id!=Environment.ProcessId&&name.StartsWith("VictusFanControl",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close other VictusFanControl GUI/Guardian/watchdog/readers so the observed fans are an OEM reference.");
            }
        }
    }
    private static int Capture(string modules,string output,double seconds,Parameters parameters)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Live capture requires Windows on the exact target.");
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new InvalidOperationException("Use elevated PowerShell.");
        var hardware=HardwareIdentityReader.ReadCurrent();
        if(!Hp8C40TargetProfile.Matches(hardware,out var reason))throw new InvalidOperationException("Wrong target: "+reason);
        if(Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0","ProcessorNameString",null)?.ToString()?.Contains("i7-13700H",StringComparison.OrdinalIgnoreCase)!=true)
            throw new InvalidOperationException("CPU must be i7-13700H.");
        var module=Path.Combine(modules,"IntelMSR.bin");
        const string expectedHash="d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";
        if(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module))).ToLowerInvariant()!=expectedHash)throw new InvalidOperationException("IntelMSR module identity mismatch.");
        NoOtherReaders();
        using var mutex=new Mutex(false,@"Local\VictusFanControl.OemShadow.HP-8C40-9D0R1LA-F18");bool owns=false;
        try
        {
            try{owns=mutex.WaitOne(0);}catch(AbandonedMutexException){owns=true;}
            if(!owns)throw new InvalidOperationException("Another OEM observer is running.");
            WmiOnlyInvestigationPolicy.Enable(); // Installed before opening any backend. Denies EC and non-2D HP methods.
            using var session=new ShadowSession(output,parameters,"LIVE_READ_ONLY");
            var options=new JsonSerializerOptions(ShadowSession.Json){WriteIndented=true};
            File.WriteAllText(Path.Combine(output,"identity.json"),JsonSerializer.Serialize(new{hardware,moduleHash=expectedHash,
                assemblyVersion=typeof(Program).Assembly.GetName().Version?.ToString(),
                sourceRevision=typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                coreModuleId=typeof(Hp8C40TargetProfile).Assembly.ManifestModule.ModuleVersionId,
                fanWriteAuthority=false,performanceWriteAuthority=false,sourceEpochRule="Per-query start timestamp, conservative age; not silicon sensor update timestamp.",
                fanAuthorityPrecondition="Operator must establish firmware fan mode; external fan controllers are not fully detectable. The observer neither acquires nor restores prior authority.",
                dttParticipant=OemSources.DttPrefix,tzInstance=OemSources.TzInstance},options));
            using var stop=new CancellationTokenSource();ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;stop.Cancel();};Console.CancelKeyPress+=handler;
            var fastSource=new FastSources(module);var fast=new ReadSlot<FastSample>();var tz=new ReadSlot<Source>();var dtt=new ReadSlot<DttSample>();
            var fan=new HpWmiFanTelemetryReader(Hp8C40TargetProfile.Instance);
            var stopwatch=Stopwatch.StartNew();DateTimeOffset? lastUtc=null;long lastElapsed=0,discontinuities=0;string? failure=null;
            try
            {
                Console.WriteLine("OEM SHADOW: read-only. Firmware fan mode is a prerequisite. Ctrl+C saves final summary. No fan/CPU-power/GPU-clock writes.");
                while(!stop.IsCancellationRequested&&(seconds==0||stopwatch.Elapsed.TotalSeconds<seconds))
                {
                    var now=DateTimeOffset.UtcNow;long elapsed=stopwatch.ElapsedMilliseconds;
                    if(lastUtc is { } prev)
                    {
                        double wall=(now-prev).TotalMilliseconds;
                        if(wall<=0||wall>parameters.MaxGapMs||Math.Abs(wall-(elapsed-lastElapsed))>2000)
                        {
                            discontinuities++;fast.Invalidate();tz.Invalidate();dtt.Invalidate();session.InvalidateClock();
                            fan.Dispose();fan=new(Hp8C40TargetProfile.Instance);
                        }
                    }
                    NoOtherReaders();fast.Poll(elapsed,fastSource.Read);tz.Poll(elapsed,OemSources.ReadTz);dtt.Poll(elapsed,OemSources.ReadDtt);
                    var actual=fan.ReadCached();var f=fast.Latest;var t=dtt.Latest;
                    session.Add(new(now,f?.Package??new(),f?.Core??new(),f?.Gpu??new(),tz.Latest??new(),t?.Dtt3??new(),t?.Dtt1,t?.Dtt2,
                        actual?.CpuSpeedLevel,actual?.GpuSpeedLevel,actual?.SampledAtUtc,f?.CpuPower,f?.GpuPower,null,f?.GpuLoad));
                    lastUtc=now;lastElapsed=elapsed;
                    if(elapsed%60000<1100)File.WriteAllText(Path.Combine(output,"sources.json"),JsonSerializer.Serialize(new{
                        fastError=fast.Error,fastBackendError=f?.Error,tzError=tz.Error,dttError=dtt.Error,fanDiagnostic=fan.Diagnostic,
                        fastPending=fast.InFlight,tzPending=tz.InFlight,dttPending=dtt.InFlight,clockDiscontinuities=discontinuities},options));
                    stop.Token.WaitHandle.WaitOne(1000);
                }
            }
            catch(Exception ex){failure=ex.Message;throw;}
            finally
            {
                Console.CancelKeyPress-=handler;fan.Dispose();
                // Never dispose a backend while a native call on its worker is still running.
                if(!fast.InFlight)fastSource.Dispose();
                File.WriteAllText(Path.Combine(output,"terminal.json"),JsonSerializer.Serialize(new{endedAtUtc=DateTimeOffset.UtcNow,failure,
                    clockDiscontinuities=discontinuities,fanWriteAuthority=false,performanceWriteAuthority=false,
                    pendingReads=new{fast=fast.InFlight,tz=tz.InFlight,dtt=dtt.InFlight}},options));
            }
            Console.WriteLine("Capture saved: "+output);return 0;
        }
        finally{if(owns)mutex.ReleaseMutex();}
    }
}
