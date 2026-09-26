using System.Runtime.InteropServices;
using System.Text;

namespace VictusFanControl.Hardware.Nvidia;

internal sealed class NvmlClient : IDisposable
{
    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0;
    private const int ReadAttempts = 3;
    private const int RetryDelayMs = 2;

    private readonly IntPtr _library;
    private readonly NvmlShutdownDelegate? _shutdown;
    private readonly NvmlInitDelegate _init;
    private readonly NvmlDeviceGetCountDelegate _getCount;
    private readonly NvmlDeviceGetHandleByIndexDelegate _getHandle;
    private readonly NvmlDeviceGetNameDelegate _getName;
    private readonly NvmlDeviceGetPowerUsageDelegate _getPowerUsage;
    private readonly NvmlDeviceGetUtilizationRatesDelegate _getUtilizationRates;
    private readonly NvmlDeviceGetTemperatureDelegate _getTemperature;

    private readonly string? _preferredDeviceName;
    private IntPtr _device;
    private bool _initialized;
    private bool _disposed;

    public NvmlClient(string? preferredDeviceName = null)
    {
        _preferredDeviceName = preferredDeviceName;
        _library = LoadNvmlLibrary();

        try
        {
            _init = GetDelegate<NvmlInitDelegate>("nvmlInit_v2");
            _shutdown = GetDelegate<NvmlShutdownDelegate>("nvmlShutdown");
            _getCount = GetDelegate<NvmlDeviceGetCountDelegate>("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
            _getHandle = GetDelegate<NvmlDeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
            _getName = GetDelegate<NvmlDeviceGetNameDelegate>("nvmlDeviceGetName");
            _getPowerUsage = GetDelegate<NvmlDeviceGetPowerUsageDelegate>("nvmlDeviceGetPowerUsage");
            _getUtilizationRates = GetDelegate<NvmlDeviceGetUtilizationRatesDelegate>("nvmlDeviceGetUtilizationRates");
            _getTemperature = GetDelegate<NvmlDeviceGetTemperatureDelegate>("nvmlDeviceGetTemperature");

            InitializeDevice();
        }
        catch
        {
            if (_initialized)
            {
                _shutdown?.Invoke();
            }

            NativeLibrary.Free(_library);
            throw;
        }
    }

    public string DeviceName { get; private set; } = "NVIDIA GPU";

    public GpuSample ReadSample()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            return ReadSampleWithRetries();
        }
        catch
        {
            // NVML handles can become stale across driver resets or sleep/resume.
            // Reinitialize once, then perform the bounded read sequence again.
            ReinitializeDevice();
            return ReadSampleWithRetries();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_initialized)
        {
            _shutdown?.Invoke();
            _initialized = false;
        }

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
        }

        _disposed = true;
    }

    private GpuSample ReadSampleWithRetries()
    {
        var temperature = RetryMetric(
            "GPU temperature",
            () =>
            {
                var result = _getTemperature(_device, NvmlTemperatureGpu, out var value);
                return (result, (double)value);
            },
            value => value is >= 0 and <= 125);

        var power = RetryMetric(
            "GPU power",
            () =>
            {
                var result = _getPowerUsage(_device, out var milliwatts);
                return (result, milliwatts / 1000.0);
            },
            value => value is >= 0 and < 300);

        var load = RetryMetric(
            "GPU utilization",
            () =>
            {
                var result = _getUtilizationRates(_device, out var utilization);
                return (result, (double)utilization.Gpu);
            },
            value => value is >= 0 and <= 100);

        return new GpuSample(temperature, power, load);
    }

    private static double RetryMetric(
        string name,
        Func<(int Result, double Value)> read,
        Func<double, bool> plausible)
    {
        var lastResult = int.MinValue;
        var lastValue = double.NaN;

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            var sample = read();
            lastResult = sample.Result;
            lastValue = sample.Value;

            if (sample.Result == NvmlSuccess &&
                double.IsFinite(sample.Value) &&
                plausible(sample.Value))
            {
                return sample.Value;
            }

            if (attempt < ReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new InvalidDataException(
            $"{name} failed after {ReadAttempts} attempts (NVML={lastResult}, value={lastValue}).");
    }

    private void ReinitializeDevice()
    {
        if (_initialized)
        {
            _shutdown?.Invoke();
            _initialized = false;
        }

        InitializeDevice();
    }

    private void InitializeDevice()
    {
        ThrowIfError(_init(), "nvmlInit_v2");
        _initialized = true;

        ThrowIfError(_getCount(out var count), "nvmlDeviceGetCount");
        if (count == 0)
        {
            throw new InvalidOperationException("NVML initialized but no NVIDIA GPU was found.");
        }

        IntPtr fallbackHandle = IntPtr.Zero;
        string fallbackName = "NVIDIA GPU";

        for (uint index = 0; index < count; index++)
        {
            ThrowIfError(_getHandle(index, out var candidate), "nvmlDeviceGetHandleByIndex");
            var candidateName = ReadDeviceName(candidate);

            if (index == 0)
            {
                fallbackHandle = candidate;
                fallbackName = candidateName;
            }

            if (!string.IsNullOrWhiteSpace(_preferredDeviceName) &&
                string.Equals(
                    candidateName,
                    _preferredDeviceName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _device = candidate;
                DeviceName = candidateName;
                return;
            }
        }

        _device = fallbackHandle;
        DeviceName = fallbackName;
    }


    private string ReadDeviceName(IntPtr device)
    {
        var nameBuffer = new byte[128];
        return _getName(device, nameBuffer, (uint)nameBuffer.Length) == NvmlSuccess
            ? DecodeCString(nameBuffer)
            : "NVIDIA GPU";
    }

    private static IntPtr LoadNvmlLibrary()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "nvml.dll")
        };

        var programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrWhiteSpace(programW6432))
        {
            candidates.Add(Path.Combine(programW6432, "NVIDIA Corporation", "NVSMI", "nvml.dll"));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        if (NativeLibrary.TryLoad("nvml.dll", out var defaultHandle))
        {
            return defaultHandle;
        }

        throw new DllNotFoundException(
            "nvml.dll was not found. Install/update the NVIDIA display driver; NVML is supplied by the NVIDIA driver.");
    }

    private T GetDelegate<T>(params string[] exports) where T : Delegate
    {
        foreach (var export in exports)
        {
            if (NativeLibrary.TryGetExport(_library, export, out var address))
            {
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
        }

        throw new EntryPointNotFoundException($"NVML export not found: {string.Join(" or ", exports)}");
    }

    private static void ThrowIfError(int result, string operation)
    {
        if (result != NvmlSuccess)
        {
            throw new InvalidOperationException($"{operation} failed with NVML error {result}.");
        }
    }

    private static string DecodeCString(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }

        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetCountDelegate(out uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetNameDelegate(IntPtr device, [Out] byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetPowerUsageDelegate(IntPtr device, out uint powerMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetUtilizationRatesDelegate(IntPtr device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetTemperatureDelegate(IntPtr device, uint sensorType, out uint temperature);

    internal readonly record struct GpuSample(double TemperatureC, double PowerW, double LoadPercent);
}
