using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace VictusFanControl.Hardware.PawnIo;

internal enum IntelCoreType
{
    Unknown,
    Performance,
    Efficiency
}

internal readonly record struct IntelCoreTemperatureSample(
    int CoreIndex,
    int LogicalProcessorIndex,
    IntelCoreType CoreType,
    double TemperatureC);

internal sealed class IntelMsrReader : IDisposable
{
    private const uint MsrIa32ThermStatus = 0x19C;
    private const uint MsrIa32PackageThermStatus = 0x1B1;
    private const uint MsrIa32TemperatureTarget = 0x1A2;
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgEnergyStatus = 0x611;

    private const int MsrReadAttempts = 3;
    private const int RetryDelayMs = 1;

    private readonly PawnIoModuleSession _session;
    private readonly double _energyUnitJoules;
    private readonly IReadOnlyList<CoreAffinityTarget> _coreTargets;

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

        _coreTargets = DiscoverPhysicalCoreTargets();
    }

    public Version PawnIoVersion => _session.DriverVersion;

    public int PhysicalCoreCount => _coreTargets.Count;

    public double? ReadPackageTemperatureC()
    {
        var tjMax = ReadTjMaxC();

        for (var attempt = 1; attempt <= MsrReadAttempts; attempt++)
        {
            var status = ReadMsr(MsrIa32PackageThermStatus);
            if (TryDecodeTemperature(status, tjMax, out var temperature))
            {
                return temperature;
            }

            if (attempt < MsrReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new InvalidDataException(
            "Intel package thermal-status valid bit remained clear.");
    }

    /// <summary>
    /// Reads IA32_THERM_STATUS (0x19C) once per physical core. The current
    /// thread is temporarily pinned to one representative logical processor of
    /// each core so the MSR read is executed in that core's CPU context.
    ///
    /// This is read-only sensor telemetry. PawnIO's IntelMSR allow-list already
    /// permits 0x19C; no MSR write path is used by VictusFanControl.
    /// </summary>
    public IReadOnlyList<IntelCoreTemperatureSample> ReadCoreTemperaturesC()
    {
        if (_coreTargets.Count == 0)
        {
            throw new NotSupportedException(
                "Physical-core topology could not be resolved for per-core temperature telemetry.");
        }

        var tjMax = ReadTjMaxC();
        var samples = new IntelCoreTemperatureSample[_coreTargets.Count];

        for (var index = 0; index < _coreTargets.Count; index++)
        {
            var target = _coreTargets[index];
            var temperature = RunOnLogicalProcessor(
                target.LogicalProcessorIndex,
                () => ReadCoreTemperatureOnCurrentProcessorC(tjMax));

            samples[index] = new IntelCoreTemperatureSample(
                CoreIndex: target.CoreIndex,
                LogicalProcessorIndex: target.LogicalProcessorIndex,
                CoreType: target.CoreType,
                TemperatureC: temperature);
        }

        return samples;
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
            catch (Exception ex) when (ex is IOException or InvalidDataException or Win32Exception)
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

    private double ReadCoreTemperatureOnCurrentProcessorC(double tjMax)
    {
        for (var attempt = 1; attempt <= MsrReadAttempts; attempt++)
        {
            var status = ReadMsr(MsrIa32ThermStatus);
            if (TryDecodeTemperature(status, tjMax, out var temperature))
            {
                return temperature;
            }

            if (attempt < MsrReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new InvalidDataException(
            "Intel core thermal-status valid bit remained clear.");
    }

    private double ReadTjMaxC()
    {
        var target = ReadMsr(MsrIa32TemperatureTarget);
        var tjMax = (double)((target >> 16) & 0xFF);
        if (tjMax is < 70 or > 125)
        {
            throw new InvalidDataException($"Intel TjMax is implausible: {tjMax:0} C.");
        }

        return tjMax;
    }

    private static bool TryDecodeTemperature(
        ulong thermalStatus,
        double tjMax,
        out double temperature)
    {
        var valid = (thermalStatus & (1UL << 31)) != 0;
        if (!valid)
        {
            temperature = double.NaN;
            return false;
        }

        var distanceToTjMax = (double)((thermalStatus >> 16) & 0x7F);
        temperature = tjMax - distanceToTjMax;

        if (!double.IsFinite(temperature) || temperature is < 0 or > 125)
        {
            throw new InvalidDataException(
                $"Intel thermal sensor temperature is implausible: {temperature:0.0} C.");
        }

        return true;
    }

    private static IReadOnlyList<CoreAffinityTarget> DiscoverPhysicalCoreTargets()
    {
        if (!OperatingSystem.IsWindows() || !X86Base.IsSupported)
        {
            return Array.Empty<CoreAffinityTarget>();
        }

        var logicalProcessorCount = Environment.ProcessorCount;
        var maskWidth = IntPtr.Size * 8;

        // Both physically validated Victus targets are single-group systems
        // (16 and 20 logical processors). Refuse to invent topology if a future
        // target exceeds the affinity mask width; add processor-group support
        // deliberately when such hardware is qualified.
        if (logicalProcessorCount <= 0 || logicalProcessorCount > maskWidth)
        {
            return Array.Empty<CoreAffinityTarget>();
        }

        var uniqueCores = new Dictionary<uint, CoreAffinityTarget>();

        for (var logicalProcessor = 0;
             logicalProcessor < logicalProcessorCount;
             logicalProcessor++)
        {
            try
            {
                var topology = RunOnLogicalProcessor(
                    logicalProcessor,
                    ReadCurrentProcessorTopology);

                if (!uniqueCores.ContainsKey(topology.CoreKey))
                {
                    uniqueCores[topology.CoreKey] = new CoreAffinityTarget(
                        CoreIndex: 0,
                        LogicalProcessorIndex: logicalProcessor,
                        CoreType: topology.CoreType,
                        CoreKey: topology.CoreKey);
                }
            }
            catch
            {
                // Per-core telemetry is additive but safety-relevant once
                // available. Return no topology rather than silently claiming
                // a partial/incorrect physical-core map.
                return Array.Empty<CoreAffinityTarget>();
            }
        }

        return uniqueCores.Values
            .OrderBy(target => target.LogicalProcessorIndex)
            .Select((target, index) => target with { CoreIndex = index })
            .ToArray();
    }

    private static CurrentProcessorTopology ReadCurrentProcessorTopology()
    {
        var maxBasicLeaf = unchecked((uint)X86Base.CpuId(0, 0).Eax);

        var topologyLeaf = maxBasicLeaf >= 0x1F
            ? 0x1F
            : maxBasicLeaf >= 0x0B
                ? 0x0B
                : 0u;

        if (topologyLeaf == 0)
        {
            throw new NotSupportedException(
                "CPUID topology leaf 0x1F/0x0B is unavailable.");
        }

        var smtShift = 0;
        var x2ApicId = 0u;
        var sawTopology = false;

        for (var subleaf = 0; subleaf < 8; subleaf++)
        {
            var registers = X86Base.CpuId(
                unchecked((int)topologyLeaf),
                subleaf);

            var logicalAtLevel = unchecked((uint)registers.Ebx) & 0xFFFF;
            if (logicalAtLevel == 0)
            {
                break;
            }

            sawTopology = true;
            x2ApicId = unchecked((uint)registers.Edx);

            var levelType =
                (unchecked((uint)registers.Ecx) >> 8) & 0xFF;
            var shift =
                (int)(unchecked((uint)registers.Eax) & 0x1F);

            if (levelType == 1)
            {
                smtShift = shift;
            }
        }

        if (!sawTopology)
        {
            throw new NotSupportedException(
                "CPUID topology leaf returned no logical levels.");
        }

        var coreType = IntelCoreType.Unknown;
        if (maxBasicLeaf >= 0x1A)
        {
            var hybrid = X86Base.CpuId(0x1A, 0);
            var rawCoreType =
                (unchecked((uint)hybrid.Eax) >> 24) & 0xFF;

            coreType = rawCoreType switch
            {
                0x40 => IntelCoreType.Performance,
                0x20 => IntelCoreType.Efficiency,
                _ => IntelCoreType.Unknown
            };
        }

        // Shifting away the SMT-id bits yields one stable key for all logical
        // threads belonging to the same physical core. Package bits remain in
        // the key, so it also stays unique on multi-package systems.
        var coreKey = x2ApicId >> smtShift;

        return new CurrentProcessorTopology(coreKey, coreType);
    }

    private static T RunOnLogicalProcessor<T>(
        int logicalProcessor,
        Func<T> action)
    {
        if (logicalProcessor < 0 ||
            logicalProcessor >= IntPtr.Size * 8)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalProcessor));
        }

        var requestedMask = new UIntPtr(1UL << logicalProcessor);
        var thread = GetCurrentThread();
        var previousMask = SetThreadAffinityMask(thread, requestedMask);

        if (previousMask == UIntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"SetThreadAffinityMask failed for logical processor {logicalProcessor}.");
        }

        try
        {
            return action();
        }
        finally
        {
            if (SetThreadAffinityMask(thread, previousMask) == UIntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Failed to restore telemetry thread affinity.");
            }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(
        IntPtr hThread,
        UIntPtr dwThreadAffinityMask);

    private readonly record struct CoreAffinityTarget(
        int CoreIndex,
        int LogicalProcessorIndex,
        IntelCoreType CoreType,
        uint CoreKey);

    private readonly record struct CurrentProcessorTopology(
        uint CoreKey,
        IntelCoreType CoreType);
}
