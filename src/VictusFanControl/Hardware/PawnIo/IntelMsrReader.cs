using System.Diagnostics;

namespace VictusFanControl.Hardware.PawnIo;

internal sealed class IntelMsrReader : IDisposable
{
    private const uint MsrIa32PackageThermStatus = 0x1B1;
    private const uint MsrIa32TemperatureTarget = 0x1A2;
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgEnergyStatus = 0x611;

    private const int MsrReadAttempts = 3;
    private const int RetryDelayMs = 1;

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

        if (!double.IsFinite(_energyUnitJoules) || _energyUnitJoules <= 0)
        {
            throw new InvalidDataException("Invalid Intel RAPL energy unit.");
        }
    }

    public Version PawnIoVersion => _session.DriverVersion;

    public double? ReadPackageTemperatureC()
    {
        var target = ReadMsr(MsrIa32TemperatureTarget);
        var tjMax = (double)((target >> 16) & 0xFF);
        if (tjMax is < 70 or > 125)
        {
            throw new InvalidDataException($"Intel TjMax is implausible: {tjMax:0} C.");
        }

        for (var attempt = 1; attempt <= MsrReadAttempts; attempt++)
        {
            var status = ReadMsr(MsrIa32PackageThermStatus);
            var valid = (status & (1UL << 31)) != 0;
            if (valid)
            {
                var distanceToTjMax = (double)((status >> 16) & 0x7F);
                var temperature = tjMax - distanceToTjMax;
                if (temperature is >= 0 and <= 125)
                {
                    return temperature;
                }

                throw new InvalidDataException($"Intel package temperature is implausible: {temperature:0.0} C.");
            }

            if (attempt < MsrReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new InvalidDataException("Intel package thermal-status valid bit remained clear.");
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
            throw new InvalidDataException("Intel RAPL sample interval was not positive.");
        }

        var watts = (deltaRaw * _energyUnitJoules) / elapsedSeconds;
        if (!double.IsFinite(watts) || watts is < 0 or >= 500)
        {
            throw new InvalidDataException($"Intel package power is implausible: {watts:0.###} W.");
        }

        return watts;
    }

    public ulong ReadMsr(uint msr)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MsrReadAttempts; attempt++)
        {
            try
            {
                var values = _session.Execute("ioctl_read_msr", new ulong[] { msr }, 1);
                if (values.Length == 1)
                {
                    return values[0];
                }

                lastError = new InvalidDataException(
                    $"IntelMSR returned {values.Length} cells for MSR 0x{msr:X}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception)
            {
                lastError = ex;
            }

            if (attempt < MsrReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new IOException(
            $"Intel MSR 0x{msr:X} failed after {MsrReadAttempts} attempts: {lastError?.Message}",
            lastError);
    }

    public void Dispose() => _session.Dispose();
}
