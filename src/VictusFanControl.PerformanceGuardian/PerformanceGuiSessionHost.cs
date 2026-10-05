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
    internal static async Task<int> RunAsync(string[] args, bool fixture = false, bool fixtureEnableFailure = false)
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
            var recording = new GuiFixtureDomains(fixtureEnableFailure);
            using var runtime = new GuardianPerformancePowerSourceRuntime(new GuiFixtureSource(),
                new RecordingGuardianCpuSourceTransitionSink(), new RecordingGuardianGpuSourceTransitionSink(),
                new WindowsGuardianPowerSourceNotificationListenerFactory());
            return await new PerformanceGuardianHost(options with { MutexName = "Local\\VFC.GuiFixture." + options.SessionNonce.ToString("N") }, recording, runtime)
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

        using var cpuBackend = configuration.CpuEnabled ? new PawnIoCpuPowerLimitBackend(module, hardwareWritesAuthorized: true) : null;
        if (cpuBackend is not null)
        {
            var baseline = cpuBackend.Read();
            if (baseline.Locked || baseline.Pl1Watts != 45 || baseline.Pl2Watts != 115)
                throw new InvalidOperationException("CPU baseline must remain unlocked 45/115 W; close external power writers first.");
        }
        using var nvml = configuration.GpuEnabled ? new NvmlClient("NVIDIA GeForce RTX 4060 Laptop GPU", requirePreferredDevice: true) : null;
        var gpuBackend = nvml is not null ? new NvmlGpuClockLimitBackend(nvml, hardwareWritesAuthorized: true) : null;
        if (gpuBackend is not null && !gpuBackend.Capabilities.HasCompleteCommandSurface)
            throw new InvalidOperationException("GPU NVML Set/Reset unavailable.");
        using var cpu = cpuBackend is not null ? new QualifiedCpuGuardianDomainLifecycle(cpuBackend,
            new JsonCpuPowerSessionJournal(cpuJournal, configuration.TargetProfileId), configuration.CpuPresets()) : null;
        using var gpu = gpuBackend is not null ? new QualifiedGpuGuardianDomainLifecycle(gpuBackend,
            new JsonGpuClockSessionJournal(gpuJournal, configuration.TargetProfileId), GpuClockPresetSet.UserRequestedVictus) : null;
        using var combined = cpu is not null && gpu is not null ? new CombinedGuardianDomainLifecycle(cpu, cpu, gpu, gpu) : null;
        IGuardianDomainLifecycle domain = (IGuardianDomainLifecycle?)combined ?? (IGuardianDomainLifecycle?)cpu ?? gpu!;
        using var source = new GuardianPerformancePowerSourceRuntime(new WindowsPerformancePowerSourceReader(),
            (ICpuPowerSourceTransitionSink?)combined ?? (ICpuPowerSourceTransitionSink?)cpu ?? new RecordingGuardianCpuSourceTransitionSink(),
            (IGpuClockSourceTransitionSink?)combined ?? (IGpuClockSourceTransitionSink?)gpu ?? new RecordingGuardianGpuSourceTransitionSink(),
            new WindowsGuardianPowerSourceNotificationListenerFactory());
        using var standby = new GuardianModernStandbyLifecycleRuntime(source, domain,
            new WindowsGuardianModernStandbyNotificationListenerFactory());
        return await new PerformanceGuardianHost(options, new ConfiguredDomains(domain, configuration), standby).RunAsync(CancellationToken.None).ConfigureAwait(false);
    }
    private sealed class GuiFixtureSource : IPerformancePowerSourceReader
    {
        public PerformancePowerSourceObservation Read() => new(true, PerformancePowerSourceKind.Ac, 1, 80, 0, "FIXTURE_AC");
    }

    private sealed class ConfiguredDomains(IGuardianDomainLifecycle inner, PerformanceGuiSessionConfiguration configuration) : IGuardianDomainLifecycle
    {
        public GuardianDomainLifecycleSnapshot Snapshot => inner.Snapshot;
        public ValueTask EnableAsync(bool cpuEnabled, bool gpuEnabled, PerformancePowerSourceKind source, CancellationToken token)
        {
            if (cpuEnabled != configuration.CpuEnabled || gpuEnabled != configuration.GpuEnabled)
                throw new InvalidOperationException("GUI domain selection differs from the validated launch configuration.");
            return inner.EnableAsync(cpuEnabled, gpuEnabled, source, token);
        }
        public ValueTask ReleaseAsync(bool cpuEnabled, bool gpuEnabled, string reason, CancellationToken token) =>
            inner.ReleaseAsync(cpuEnabled, gpuEnabled, reason, token);
    }

    private sealed class GuiFixtureDomains(bool failEnable) : IGuardianDomainLifecycle
    {
        private readonly RecordingGuardianDomainLifecycle _recording = new();
        private bool _active;
        public GuardianDomainLifecycleSnapshot Snapshot => _recording.Snapshot with
        {
            CpuState = _active ? "Active" : "Disabled", GpuState = _active ? "ActiveUnverified" : "Disabled"
        };
        public ValueTask EnableAsync(bool cpuEnabled, bool gpuEnabled, PerformancePowerSourceKind source, CancellationToken token)
        {
            if (failEnable) throw new IOException("Synthetic domain enable failure; zero hardware IO.");
            _active = true;
            return _recording.EnableAsync(cpuEnabled, gpuEnabled, source, token);
        }
        public ValueTask ReleaseAsync(bool cpuEnabled, bool gpuEnabled, string reason, CancellationToken token)
        {
            _active = false;
            return _recording.ReleaseAsync(cpuEnabled, gpuEnabled, reason, token);
        }
    }
}
