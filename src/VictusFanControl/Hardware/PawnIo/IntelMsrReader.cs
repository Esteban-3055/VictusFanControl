using System.Diagnostics;

namespace VictusFanControl.Hardware.PawnIo;

internal sealed class IntelMsrReader : IDisposable
{
    private const uint MsrIa32PackageThermStatus = 0x1B1;
    private const uint MsrIa32TemperatureTarget = 0x1A2;
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgEnergyStatus = 0x611;

    private readonly PawnIoModuleSession _session;
    private readonly double _energyUnitJoules;

    private uint? _lastEnergyRaw;
    private long? _lastEnergyTimestamp;

    public IntelMsrReader(string modulePath)
    {
        _session = new PawnIoModuleSession(modulePath);

        var raplUnits = ReadMsr(MsrRaplPowerUnit);
        var energyUnitExponent = (int)((raplUnits >> 8) & 0x1F);
        _energyUnitJoules = Math.Pow(0.5, energyUnitExponent);
    }

    public Version PawnIoVersion => _session.DriverVersion;

    public double? ReadPackageTemperatureC()
    {
        var target = ReadMsr(MsrIa32TemperatureTarget);
        var tjMax = (double)((target >> 16) & 0xFF);
        if (tjMax is < 70 or > 125)
        {
            return null;
        }

        var status = ReadMsr(MsrIa32PackageThermStatus);
        var valid = (status & (1UL << 31)) != 0;
        if (!valid)
        {
            return null;
        }

        var distanceToTjMax = (double)((status >> 16) & 0x7F);
        var temperature = tjMax - distanceToTjMax;
        return temperature is >= -20 and <= 125 ? temperature : null;
    }

    public double? ReadPackagePowerW()
    {
        var now = Stopwatch.GetTimestamp();
        var current = (uint)(ReadMsr(MsrPkgEnergyStatus) & 0xFFFFFFFF);

        if (_lastEnergyRaw is null || _lastEnergyTimestamp is null)
        {
            _lastEnergyRaw = current;
            _lastEnergyTimestamp = now;
            return null;
        }

        var elapsedSeconds = (now - _lastEnergyTimestamp.Value) / (double)Stopwatch.Frequency;
        var deltaRaw = unchecked(current - _lastEnergyRaw.Value);

        _lastEnergyRaw = current;
        _lastEnergyTimestamp = now;

        if (elapsedSeconds <= 0)
        {
            return null;
        }

        var watts = (deltaRaw * _energyUnitJoules) / elapsedSeconds;
        return double.IsFinite(watts) && watts is >= 0 and < 500 ? watts : null;
    }

    public ulong ReadMsr(uint msr)
    {
        var values = _session.Execute("ioctl_read_msr", new ulong[] { msr }, 1);
        if (values.Length != 1)
        {
            throw new InvalidDataException($"IntelMSR returned {values.Length} cells for MSR 0x{msr:X}.");
        }

        return values[0];
    }

    public void Dispose() => _session.Dispose();
}
