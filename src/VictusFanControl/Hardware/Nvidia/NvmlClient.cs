using System.Runtime.InteropServices;
using System.Text;

namespace VictusFanControl.Hardware.Nvidia;

internal sealed class NvmlClient : IDisposable
{
    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0;

    private readonly IntPtr _library;
    private readonly NvmlShutdownDelegate _shutdown;
    private readonly NvmlDeviceGetPowerUsageDelegate _getPowerUsage;
    private readonly NvmlDeviceGetUtilizationRatesDelegate _getUtilizationRates;
    private readonly NvmlDeviceGetTemperatureDelegate _getTemperature;
    private readonly IntPtr _device;
    private bool _initialized;

    public NvmlClient()
    {
        _library = LoadNvmlLibrary();

        try
        {
            var init = GetDelegate<NvmlInitDelegate>("nvmlInit_v2");
            _shutdown = GetDelegate<NvmlShutdownDelegate>("nvmlShutdown");
            var getCount = GetDelegate<NvmlDeviceGetCountDelegate>("nvmlDeviceGetCount_v2", "nvmlDeviceGetCount");
            var getHandle = GetDelegate<NvmlDeviceGetHandleByIndexDelegate>("nvmlDeviceGetHandleByIndex_v2", "nvmlDeviceGetHandleByIndex");
            var getName = GetDelegate<NvmlDeviceGetNameDelegate>("nvmlDeviceGetName");
            _getPowerUsage = GetDelegate<NvmlDeviceGetPowerUsageDelegate>("nvmlDeviceGetPowerUsage");
            _getUtilizationRates = GetDelegate<NvmlDeviceGetUtilizationRatesDelegate>("nvmlDeviceGetUtilizationRates");
            _getTemperature = GetDelegate<NvmlDeviceGetTemperatureDelegate>("nvmlDeviceGetTemperature");

            ThrowIfError(init(), "nvmlInit_v2");
            _initialized = true;

            ThrowIfError(getCount(out var count), "nvmlDeviceGetCount");
            if (count == 0)
            {
                throw new InvalidOperationException("NVML initialized but no NVIDIA GPU was found.");
            }

            ThrowIfError(getHandle(0, out _device), "nvmlDeviceGetHandleByIndex");

            var nameBuffer = new byte[128];
            DeviceName = getName(_device, nameBuffer, (uint)nameBuffer.Length) == NvmlSuccess
                ? DecodeCString(nameBuffer)
                : "NVIDIA GPU";
        }
        catch
        {
            if (_initialized)
            {
                _shutdown();
            }

            NativeLibrary.Free(_library);
            throw;
        }
    }

    public string DeviceName { get; }

    public GpuSample ReadSample()
    {
        double? temperature = null;
        double? power = null;
        double? load = null;

        if (_getTemperature(_device, NvmlTemperatureGpu, out var tempC) == NvmlSuccess)
        {
            temperature = tempC;
        }

        if (_getPowerUsage(_device, out var powerMilliwatts) == NvmlSuccess)
        {
            power = powerMilliwatts / 1000.0;
        }

        if (_getUtilizationRates(_device, out var utilization) == NvmlSuccess)
        {
            load = utilization.Gpu;
        }

        return new GpuSample(temperature, power, load);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _shutdown();
            _initialized = false;
        }

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
        }
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

    internal readonly record struct GpuSample(double? TemperatureC, double? PowerW, double? LoadPercent);
}
