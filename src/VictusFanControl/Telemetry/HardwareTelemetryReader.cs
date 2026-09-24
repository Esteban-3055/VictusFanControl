using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Telemetry;

public sealed class HardwareTelemetryReader : IDisposable
{
    private readonly string _intelModulePath;
    private readonly string _ecModulePath;

    private IntelMsrReader? _intel;
    private AcpiEcReader? _ec;
    private NvmlClient? _nvml;
    private readonly WindowsCpuLoadReader _cpuLoad = new();

    private string _intelStatus = "NOT INITIALIZED";
    private string _ecStatus = "NOT INITIALIZED";
    private string _nvmlStatus = "NOT INITIALIZED";

    private string? _lastIntelReadError;
    private string? _lastEcReadError;
    private string? _lastNvmlReadError;
    private string? _lastWindowsReadError;

    private bool _lastSnapshotHealthy;

    private DateTimeOffset _nextIntelInitAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextEcInitAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextNvmlInitAttempt = DateTimeOffset.MinValue;
    private static readonly TimeSpan ReinitializeBackoff = TimeSpan.FromSeconds(5);

    public HardwareTelemetryReader(string modulesDirectory)
    {
        _intelModulePath = Path.Combine(modulesDirectory, "IntelMSR.bin");
        _ecModulePath = Path.Combine(modulesDirectory, "LpcACPIEC.bin");

        InitializeIntel();
        InitializeEc();
        InitializeNvml();
    }

    public bool BackendsInitialized => _intel is not null && _ec is not null && _nvml is not null;

    public bool IsReadyForBaseline => BackendsInitialized && _lastSnapshotHealthy;

    public int IntelRecoveries { get; private set; }
    public int EcRecoveries { get; private set; }
    public int NvmlRecoveries { get; private set; }

    public long TotalSnapshots { get; private set; }
    public long CompleteSnapshots { get; private set; }
    public long IncompleteSnapshots => TotalSnapshots - CompleteSnapshots;
    public int ConsecutiveIncompleteSnapshots { get; private set; }
    public int MaxConsecutiveIncompleteSnapshots { get; private set; }

    public TelemetrySnapshot ReadSnapshot()
    {
        var timestamp = DateTimeOffset.UtcNow;

        EnsureBackendsAvailable(timestamp);

        double? cpuTemperature = null;
        double? cpuPower = null;

        if (_intel is not null)
        {
            try
            {
                cpuTemperature = _intel.ReadPackageTemperatureC();
                cpuPower = _intel.ReadPackagePowerW();
                _lastIntelReadError = null;
            }
            catch (Exception ex)
            {
                _lastIntelReadError = ex.Message;
                if (RecoverIntel())
                {
                    try
                    {
                        cpuTemperature = _intel!.ReadPackageTemperatureC();
                        cpuPower = _intel.ReadPackagePowerW();
                        _lastIntelReadError = null;
                    }
                    catch (Exception retryEx)
                    {
                        _lastIntelReadError = $"After recovery: {retryEx.Message}";
                    }
                }
            }
        }

        double? cpuLoad = null;
        try
        {
            cpuLoad = _cpuLoad.ReadTotalLoadPercent();
            _lastWindowsReadError = null;
        }
        catch (Exception ex)
        {
            _lastWindowsReadError = ex.Message;
        }

        string? gpuName = null;
        double? gpuTemperature = null;
        double? gpuPower = null;
        double? gpuLoad = null;

        if (_nvml is not null)
        {
            try
            {
                gpuName = _nvml.DeviceName;
                var gpu = _nvml.ReadSample();
                gpuTemperature = gpu.TemperatureC;
                gpuPower = gpu.PowerW;
                gpuLoad = gpu.LoadPercent;
                _lastNvmlReadError = null;
            }
            catch (Exception ex)
            {
                _lastNvmlReadError = ex.Message;
                if (RecoverNvml())
                {
                    try
                    {
                        gpuName = _nvml!.DeviceName;
                        var gpu = _nvml.ReadSample();
                        gpuTemperature = gpu.TemperatureC;
                        gpuPower = gpu.PowerW;
                        gpuLoad = gpu.LoadPercent;
                        _lastNvmlReadError = null;
                    }
                    catch (Exception retryEx)
                    {
                        _lastNvmlReadError = $"After recovery: {retryEx.Message}";
                    }
                }
            }
        }

        double? cpuFanRpm = null;
        double? gpuFanRpm = null;

        if (_ec is not null)
        {
            try
            {
                var fans = _ec.ReadFanTachometers();
                cpuFanRpm = ValidateFanRpm(fans.CpuRpm, "CPU");
                gpuFanRpm = ValidateFanRpm(fans.GpuRpm, "GPU");
                _lastEcReadError = null;
            }
            catch (Exception ex)
            {
                _lastEcReadError = ex.Message;
                if (RecoverEc())
                {
                    try
                    {
                        var fans = _ec!.ReadFanTachometers();
                        cpuFanRpm = ValidateFanRpm(fans.CpuRpm, "CPU");
                        gpuFanRpm = ValidateFanRpm(fans.GpuRpm, "GPU");
                        _lastEcReadError = null;
                    }
                    catch (Exception retryEx)
                    {
                        _lastEcReadError = $"After recovery: {retryEx.Message}";
                    }
                }
            }
        }

        var snapshot = new TelemetrySnapshot(
            Timestamp: timestamp,
            CpuName: Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Intel CPU",
            CpuTemperatureC: cpuTemperature,
            CpuPackagePowerW: cpuPower,
            CpuLoadPercent: cpuLoad,
            GpuName: gpuName,
            GpuTemperatureC: gpuTemperature,
            GpuPowerW: gpuPower,
            GpuLoadPercent: gpuLoad,
            CpuFanRpm: cpuFanRpm,
            GpuFanRpm: gpuFanRpm);

        RecordHealth(snapshot);
        return snapshot;
    }

    public IEnumerable<string> GetBackendDiagnostics()
    {
        yield return $"PawnIO Intel MSR : {_intelStatus}";
        yield return $"PawnIO ACPI EC   : {_ecStatus}";
        yield return $"NVIDIA NVML     : {_nvmlStatus}";
        yield return $"Backends init   : {BackendsInitialized}";
    }

    public IEnumerable<string> GetReadDiagnostics()
    {
        if (_lastIntelReadError is not null)
        {
            yield return $"Intel MSR read  : FAILED: {_lastIntelReadError}";
        }

        if (_lastNvmlReadError is not null)
        {
            yield return $"NVML read       : FAILED: {_lastNvmlReadError}";
        }

        if (_lastEcReadError is not null)
        {
            yield return $"ACPI EC read    : FAILED: {_lastEcReadError}";
        }

        if (_lastWindowsReadError is not null)
        {
            yield return $"Windows CPU load: FAILED: {_lastWindowsReadError}";
        }

        yield return $"Baseline ready  : {IsReadyForBaseline}";
    }

    public IEnumerable<string> GetHealthSummary()
    {
        yield return $"Snapshots       : {TotalSnapshots}";
        yield return $"Complete        : {CompleteSnapshots}";
        yield return $"Incomplete      : {IncompleteSnapshots}";
        yield return $"Max miss streak : {MaxConsecutiveIncompleteSnapshots}";
        yield return $"Recoveries      : Intel={IntelRecoveries}, EC={EcRecoveries}, NVML={NvmlRecoveries}";
    }

    public void Dispose()
    {
        _intel?.Dispose();
        _ec?.Dispose();
        _nvml?.Dispose();
    }

    private static double ValidateFanRpm(ushort rpm, string fanName)
    {
        // Zero is allowed because HP can stop a fan. Anything above 10k RPM
        // is outside the plausible range for this Victus family.
        if (rpm > 10_000)
        {
            throw new InvalidDataException($"{fanName} fan RPM is implausible: {rpm}.");
        }

        return rpm;
    }

    private void RecordHealth(TelemetrySnapshot snapshot)
    {
        TotalSnapshots++;

        _lastSnapshotHealthy = snapshot.IsComplete;
        if (snapshot.IsComplete)
        {
            CompleteSnapshots++;
            ConsecutiveIncompleteSnapshots = 0;
            return;
        }

        ConsecutiveIncompleteSnapshots++;
        MaxConsecutiveIncompleteSnapshots =
            Math.Max(MaxConsecutiveIncompleteSnapshots, ConsecutiveIncompleteSnapshots);
    }

    private void InitializeIntel()
    {
        try
        {
            _intel = new IntelMsrReader(_intelModulePath);
            _intelStatus = $"OK (PawnIO {_intel.PawnIoVersion}, IntelMSR.bin)";
            _nextIntelInitAttempt = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _intel?.Dispose();
            _intel = null;
            _intelStatus = $"FAILED: {ex.Message}";
            _nextIntelInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
        }
    }

    private void InitializeEc()
    {
        try
        {
            _ec = new AcpiEcReader(_ecModulePath);
            _ecStatus = $"OK (PawnIO {_ec.PawnIoVersion}, LpcACPIEC.bin)";
            _nextEcInitAttempt = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _ec?.Dispose();
            _ec = null;
            _ecStatus = $"FAILED: {ex.Message}";
            _nextEcInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
        }
    }

    private void InitializeNvml()
    {
        try
        {
            _nvml = new NvmlClient(Hp88F8TargetProfile.ExpectedGpuName);
            _nvmlStatus = $"OK ({_nvml.DeviceName})";
            _nextNvmlInitAttempt = DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _nvml?.Dispose();
            _nvml = null;
            _nvmlStatus = $"FAILED: {ex.Message}";
            _nextNvmlInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
        }
    }


    private void EnsureBackendsAvailable(DateTimeOffset now)
    {
        if (_intel is null && now >= _nextIntelInitAttempt)
        {
            InitializeIntel();
            if (_intel is not null && TotalSnapshots > 0)
            {
                IntelRecoveries++;
            }
        }

        if (_ec is null && now >= _nextEcInitAttempt)
        {
            InitializeEc();
            if (_ec is not null && TotalSnapshots > 0)
            {
                EcRecoveries++;
            }
        }

        if (_nvml is null && now >= _nextNvmlInitAttempt)
        {
            InitializeNvml();
            if (_nvml is not null && TotalSnapshots > 0)
            {
                NvmlRecoveries++;
            }
        }
    }

    private bool RecoverIntel()
    {
        try
        {
            _intel?.Dispose();
            _intel = new IntelMsrReader(_intelModulePath);
            _intelStatus = $"OK (recovered, PawnIO {_intel.PawnIoVersion})";
            _nextIntelInitAttempt = DateTimeOffset.MinValue;
            IntelRecoveries++;
            return true;
        }
        catch (Exception ex)
        {
            _intel = null;
            _intelStatus = $"FAILED recovery: {ex.Message}";
            _nextIntelInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
            return false;
        }
    }

    private bool RecoverEc()
    {
        try
        {
            _ec?.Dispose();
            _ec = new AcpiEcReader(_ecModulePath);
            _ecStatus = $"OK (recovered, PawnIO {_ec.PawnIoVersion})";
            _nextEcInitAttempt = DateTimeOffset.MinValue;
            EcRecoveries++;
            return true;
        }
        catch (Exception ex)
        {
            _ec = null;
            _ecStatus = $"FAILED recovery: {ex.Message}";
            _nextEcInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
            return false;
        }
    }

    private bool RecoverNvml()
    {
        try
        {
            _nvml?.Dispose();
            _nvml = new NvmlClient(Hp88F8TargetProfile.ExpectedGpuName);
            _nvmlStatus = $"OK (recovered, {_nvml.DeviceName})";
            _nextNvmlInitAttempt = DateTimeOffset.MinValue;
            NvmlRecoveries++;
            return true;
        }
        catch (Exception ex)
        {
            _nvml = null;
            _nvmlStatus = $"FAILED recovery: {ex.Message}";
            _nextNvmlInitAttempt = DateTimeOffset.UtcNow + ReinitializeBackoff;
            return false;
        }
    }
}
