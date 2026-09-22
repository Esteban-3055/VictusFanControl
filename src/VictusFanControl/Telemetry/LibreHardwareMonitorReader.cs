using LibreHardwareMonitor.Hardware;

namespace VictusFanControl.Telemetry;

public sealed class LibreHardwareMonitorReader : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = false,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
        IsStorageEnabled = false,
        IsNetworkEnabled = false
    };

    private bool _opened;

    public void Open()
    {
        if (_opened)
        {
            return;
        }

        _computer.Open();
        _opened = true;
        UpdateAll();
    }

    public TelemetrySnapshot ReadSnapshot()
    {
        EnsureOpen();
        UpdateAll();

        var all = FlattenHardware(_computer.Hardware).ToArray();
        var cpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpu = all.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                  ?? all.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuIntel);

        return new TelemetrySnapshot(
            Timestamp: DateTimeOffset.UtcNow,
            CpuName: cpu?.Name,
            CpuTemperatureC: PickCpuTemperature(cpu),
            CpuPackagePowerW: PickCpuPackagePower(cpu),
            CpuLoadPercent: PickCpuLoad(cpu),
            GpuName: gpu?.Name,
            GpuTemperatureC: PickGpuTemperature(gpu),
            GpuPowerW: PickGpuPower(gpu),
            GpuLoadPercent: PickGpuLoad(gpu),
            CpuFanRpm: null,
            GpuFanRpm: null);
    }

    public IEnumerable<string> GetSensorInventory()
    {
        EnsureOpen();
        UpdateAll();

        foreach (var hardware in FlattenHardware(_computer.Hardware))
        {
            yield return $"[{hardware.HardwareType}] {hardware.Name}";
            foreach (var sensor in hardware.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
            {
                var value = sensor.Value.HasValue ? sensor.Value.Value.ToString("0.###") : "n/a";
                yield return $"  {sensor.SensorType,-12} | {sensor.Name,-35} | {value}";
            }
        }
    }

    public void Dispose()
    {
        if (!_opened)
        {
            return;
        }

        _computer.Close();
        _opened = false;
    }

    private static double? PickCpuTemperature(IHardware? cpu)
    {
        if (cpu is null) return null;
        var sensors = cpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? PickCpuPackagePower(IHardware? cpu)
    {
        if (cpu is null) return null;
        var sensors = cpu.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s => s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? PickCpuLoad(IHardware? cpu)
    {
        if (cpu is null) return null;
        var sensors = cpu.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s => s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? PickGpuTemperature(IHardware? gpu)
    {
        if (gpu is null) return null;
        var sensors = gpu.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? PickGpuPower(IHardware? gpu)
    {
        if (gpu is null) return null;
        var sensors = gpu.Sensors.Where(s => s.SensorType == SensorType.Power && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s =>
            s.Name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? PickGpuLoad(IHardware? gpu)
    {
        if (gpu is null) return null;
        var sensors = gpu.Sensors.Where(s => s.SensorType == SensorType.Load && s.Value.HasValue).ToArray();
        var preferred = sensors.FirstOrDefault(s => s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase));
        return preferred?.Value ?? MaxValue(sensors);
    }

    private static double? MaxValue(IEnumerable<ISensor> sensors)
    {
        var values = sensors
            .Where(s => s.Value.HasValue)
            .Select(s => (double)s.Value!.Value)
            .ToArray();

        return values.Length == 0 ? null : values.Max();
    }

    private void UpdateAll()
    {
        _computer.Accept(new UpdateVisitor());
    }

    private void EnsureOpen()
    {
        if (!_opened)
        {
            throw new InvalidOperationException("Telemetry reader is not open.");
        }
    }

    private static IEnumerable<IHardware> FlattenHardware(IEnumerable<IHardware> hardware)
    {
        foreach (var item in hardware)
        {
            yield return item;
            foreach (var child in FlattenHardware(item.SubHardware))
            {
                yield return child;
            }
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware) => hardware.Update();
        public void VisitParameter(IParameter parameter) { }
        public void VisitSensor(ISensor sensor) { }
    }
}
