using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>Explicit GUI session over the qualified hardware/lifecycle adapters.</summary>
internal static class PerformanceGuiSessionHost
{
    internal static async Task<int> RunAsync(string[] args, bool fixture = false, bool fixtureEnableFailure = false, bool fixtureUpdateFailure = false,
        bool fixtureReleaseFailure = false, bool fixtureUnknownReleaseFailure = false, bool fixtureLostReply = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !values.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException("Invalid GUI Guardian arguments.");
        }
        string Get(string key) => values.TryGetValue(key, out var value) ? value : throw new ArgumentException(key);
        if (values.Count != 7) throw new ArgumentException("Unexpected GUI Guardian arguments.");
        var configuration = JsonSerializer.Deserialize<PerformanceGuiSessionConfiguration>(File.ReadAllText(Get("--configuration")))
            ?? throw new ArgumentException("Missing session configuration.");
        configuration.Validate();
        var options = new GuardianHostOptions(configuration.TargetProfileId, Get("--pipe"),
            PerformanceGuardianHost.ProductionMutexName(configuration.TargetProfileId),
            Guid.Parse(Get("--nonce")), int.Parse(Get("--owner-pid")), long.Parse(Get("--owner-start")), Get("--report"), null);
        if (fixture)
        {
            var recording = new GuiFixtureDomains(fixtureEnableFailure, fixtureUpdateFailure, configuration, fixtureReleaseFailure, fixtureUnknownReleaseFailure);
            using var runtime = new GuardianPerformancePowerSourceRuntime(new GuiFixtureSource(),
                new RecordingGuardianCpuSourceTransitionSink(), new RecordingGuardianGpuSourceTransitionSink(),
                new WindowsGuardianPowerSourceNotificationListenerFactory(),allowEmptySelection:true);
            return await new PerformanceGuardianHost(options with { MutexName = "Local\\VFC.GuiFixture." + options.SessionNonce.ToString("N") }, recording, runtime)
                { DropRecoveryReplyInFixture=fixtureLostReply }
                .RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("GUI performance requires elevation.");
        if (!Hp8C40TargetProfile.Matches(HardwareIdentityReader.ReadCurrent(), out var reason))
            throw new InvalidOperationException("GUI performance target mismatch: " + reason);
        var cpuName = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString();
        if (cpuName?.Contains("i7-13700H", StringComparison.OrdinalIgnoreCase) != true)
            throw new InvalidOperationException("GUI performance requires i7-13700H.");
        var module = Get("--module");
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module))).ToLowerInvariant() !=
            "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f")
            throw new InvalidOperationException("IntelMSR module identity mismatch.");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl", "Performance", configuration.TargetProfileId);
        var cpuJournal = Path.Combine(directory, "cpu-power-session.json");
        var gpuJournal = Path.Combine(directory, "gpu-clock-session.json");
        if (File.Exists(cpuJournal) || File.Exists(gpuJournal))
            throw new InvalidOperationException("Existing CPU/GPU journal requires recovery; it will not be overwritten.");

        GuiPerformanceDomain CreateCpu(PerformanceGuiSessionConfiguration next)
        {
            var backend=new PawnIoCpuPowerLimitBackend(module,hardwareWritesAuthorized:true,executionPolicy:CpuPowerExecutionPolicy.ProductPl1ToPl2);
            try
            {
                var baseline=backend.Read();
                if(baseline.Locked||baseline.Pl1Watts!=45||baseline.Pl2Watts!=115)
                    throw new InvalidOperationException("CPU baseline must remain unlocked 45/115 W; close external power writers first.");
                var domain=new QualifiedCpuGuardianDomainLifecycle(backend,new JsonCpuPowerSessionJournal(cpuJournal,next.TargetProfileId),
                    next.CpuPresets(),executionPolicy:CpuPowerExecutionPolicy.ProductPl1ToPl2);
                return new(domain,(updated,confirmed)=>domain.UpdatePresets(updated.CpuPresets(),confirmed),domain,null,domain.Dispose);
            }
            catch{backend.Dispose();throw;}
        }
        GuiPerformanceDomain CreateGpu(PerformanceGuiSessionConfiguration next)
        {
            var nvml=new NvmlClient("NVIDIA GeForce RTX 4060 Laptop GPU",requirePreferredDevice:true);
            try
            {
                var backend=new NvmlGpuClockLimitBackend(nvml,hardwareWritesAuthorized:true);
                if(!backend.Capabilities.HasCompleteCommandSurface)throw new InvalidOperationException("GPU NVML Set/Reset unavailable.");
                var domain=new QualifiedGpuGuardianDomainLifecycle(backend,new JsonGpuClockSessionJournal(gpuJournal,next.TargetProfileId),next.GpuPresets());
                return new(domain,(updated,confirmed)=>domain.UpdatePresets(updated.GpuPresets(),confirmed),null,domain,
                    ()=>{try{domain.Dispose();}finally{nvml.Dispose();}});
            }
            catch{nvml.Dispose();throw;}
        }
        using var domains=new SelectableGuiPerformanceDomains(configuration,CreateCpu,CreateGpu);
        using var source = new GuardianPerformancePowerSourceRuntime(new WindowsPerformancePowerSourceReader(),
            domains,domains,new WindowsGuardianPowerSourceNotificationListenerFactory(),allowEmptySelection:true);
        using var standby = new GuardianModernStandbyLifecycleRuntime(source, domains,
            new WindowsGuardianModernStandbyNotificationListenerFactory());
        return await new PerformanceGuardianHost(options, domains, standby).RunAsync(CancellationToken.None).ConfigureAwait(false);
    }
    private sealed class GuiFixtureSource : IPerformancePowerSourceReader
    {
        public PerformancePowerSourceObservation Read() => new(true, PerformancePowerSourceKind.Ac, 1, 80, 0, "FIXTURE_AC");
    }

    private sealed class GuiFixtureDomains(bool failEnable, bool failUpdate, PerformanceGuiSessionConfiguration initial,
        bool failRelease, bool unknownRelease) : IGuardianDomainLifecycle, IGuardianSelectableDomains
    {
        private readonly RecordingGuardianDomainLifecycle _recording = new();
        private bool _active;
        private bool _releaseFailed;
        private readonly Guid _session = Guid.NewGuid();
        public PerformanceGuiSessionConfiguration Configuration { get; private set; } = initial;
        public void UpdateConfiguration(PerformanceGuiSessionConfiguration next, PerformancePowerSourceKind source)
        {
            next.Validate();
            if (!_active || source != PerformancePowerSourceKind.Ac)
                throw new InvalidOperationException("Fixture update admission rejected.");
            if (failUpdate) throw new IOException("Synthetic update failure; prior committed configuration retained.");
            Configuration = next;
        }
        public GuardianDomainLifecycleSnapshot Snapshot => _recording.Snapshot with
        {
            CpuState = _releaseFailed ? "Disabled" : _active&&Configuration.CpuEnabled ? "Active" : "Disabled",
            GpuState = _releaseFailed ? "RecoveryRequired" : _active&&Configuration.GpuEnabled ? "ActiveUnverified" : "Disabled",
            RecoveryGpuSession = _releaseFailed && failRelease ? _session : null
        };
        public ValueTask EnableAsync(bool cpuEnabled, bool gpuEnabled, PerformancePowerSourceKind source, CancellationToken token)
        {
            if (failEnable) throw new IOException("Synthetic domain enable failure; zero hardware IO.");
            _active = true;
            return _recording.EnableAsync(cpuEnabled, gpuEnabled, source, token);
        }
        public ValueTask ReleaseAsync(bool cpuEnabled, bool gpuEnabled, string reason, CancellationToken token)
        {
            if ((failRelease || unknownRelease) && !_releaseFailed)
            {
                _recording.ReleaseAsync(cpuEnabled, gpuEnabled, reason, token).GetAwaiter().GetResult();
                _releaseFailed = true;
                throw new IOException("Synthetic release failure; zero hardware IO.");
            }
            _releaseFailed = false;
            _active = false;
            return _recording.ReleaseAsync(cpuEnabled, gpuEnabled, reason, token);
        }
    }
}
