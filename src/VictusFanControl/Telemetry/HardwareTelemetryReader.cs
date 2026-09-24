using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Telemetry;

public sealed class HardwareTelemetryReader : IDisposable
{
    private readonly IntelMsrReader? _intel;
    private readonly AcpiEcReader? _ec;
    private readonly NvmlClient? _nvml;
    private readonly WindowsCpuLoadReader _cpuLoad = new();

    private readonly string _intelStatus;
    private readonly string _ecStatus;
    private readonly string _nvmlStatus;

    private string? _lastIntelReadError;
    private string? _lastEcReadError;
    private string? _lastNvmlReadError;
    private bool _lastSnapshotHealthy;

    public HardwareTelemetryReader(string modulesDirectory)
    {
        var intelModule = Path.Combine(modulesDirectory, "IntelMSR.bin");
        var ecModule = Path.Combine(modulesDirectory, "LpcACPIEC.bin");

        try
        {
            _intel = new IntelMsrReader(intelModule);
            _intelStatus = $"OK (PawnIO {_intel.PawnIoVersion}, IntelMSR.bin)";
        }
        catch (Exception ex)
        {
            _intelStatus = $"FAILED: {ex.Message}";
        }

        try
        {
            _ec = new AcpiEcReader(ecModule);
            _ecStatus = $"OK (PawnIO {_ec.PawnIoVersion}, LpcACPIEC.bin)";
        }
        catch (Exception ex)
        {
            _ecStatus = $"FAILED: {ex.Message}";
        }

        try
        {
            _nvml = new NvmlClient();
            _nvmlStatus = $"OK ({_nvml.DeviceName})";
        }
        catch (Exception ex)
        {
            _nvmlStatus = $"FAILED: {ex.Message}";
        }
    }

    public bool BackendsInitialized => _intel is not null && _ec is not null && _nvml is not null;

    public bool IsReadyForBaseline => BackendsInitialized && _lastSnapshotHealthy;

    public TelemetrySnapshot ReadSnapshot()
    {
        var timestamp = DateTimeOffset.UtcNow;

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
            }
        }

        var cpuLoad = _cpuLoad.ReadTotalLoadPercent();

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
            }
        }

        double? cpuFanRpm = null;
        double? gpuFanRpm = null;
        if (_ec is not null)
        {
            try
            {
                cpuFanRpm = _ec.ReadWordLittleEndian(0xB0);
                gpuFanRpm = _ec.ReadWordLittleEndian(0xB2);
                _lastEcReadError = null;
            }
            catch (Exception ex)
            {
                _lastEcReadError = ex.Message;
            }
        }

        _lastSnapshotHealthy =
            cpuTemperature.HasValue &&
            cpuPower.HasValue &&
            cpuLoad.HasValue &&
            gpuTemperature.HasValue &&
            gpuPower.HasValue &&
            gpuLoad.HasValue &&
            cpuFanRpm.HasValue &&
            gpuFanRpm.HasValue;

        return new TelemetrySnapshot(
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

        yield return $"Baseline ready  : {IsReadyForBaseline}";
    }

    public void Dispose()
    {
        _intel?.Dispose();
        _ec?.Dispose();
        _nvml?.Dispose();
    }
}
