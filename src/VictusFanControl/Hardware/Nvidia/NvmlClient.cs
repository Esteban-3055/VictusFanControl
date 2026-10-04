using System.Runtime.InteropServices;
using System.Text;

namespace VictusFanControl.Hardware.Nvidia;

internal sealed class NvmlClient :
    IDisposable,
    INvmlGpuClockControlTransport,
    INvmlGpuPowerLimitReadTransport,
    INvmlGpuPowerFieldReadTransport
{
    private const int NvmlSuccess = 0;
    private const uint NvmlTemperatureGpu = 0;
    private const uint NvmlClockGraphics = 0;
    private const uint NvmlClockIdApplicationTarget = 1;

    // Public NVML field IDs from nvml.h / NVML API reference.
    private const uint NvmlFieldPowerMinLimit = 187;
    private const uint NvmlFieldPowerMaxLimit = 188;
    private const uint NvmlFieldPowerDefaultLimit = 189;
    private const uint NvmlFieldPowerCurrentLimit = 190;
    private const uint NvmlFieldPowerRequestedLimit = 192;

    private const int NvmlValueTypeUnsignedInt = 1;
    private const int NvmlValueTypeUnsignedLong = 2;
    private const int NvmlValueTypeUnsignedLongLong = 3;
    private const int NvmlValueTypeUnsignedShort = 6;

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

    // Clock-control exports are optional. Telemetry must continue to work on a
    // driver that does not expose the locked-clock command surface.
    private readonly NvmlDeviceSetGpuLockedClocksDelegate?
        _setGpuLockedClocks;

    private readonly NvmlDeviceResetGpuLockedClocksDelegate?
        _resetGpuLockedClocks;

    private readonly NvmlDeviceGetClockInfoDelegate?
        _getClockInfo;

    private readonly NvmlDeviceGetClockDelegate?
        _getClock;

    private readonly NvmlDeviceGetCurrentClocksEventReasonsDelegate?
        _getCurrentClocksEventReasons;

    private readonly NvmlDeviceSetPowerManagementLimitDelegate?
        _setPowerManagementLimit;

    private readonly NvmlDeviceGetPowerManagementModeDelegate?
        _getPowerManagementMode;

    private readonly NvmlDeviceGetPowerManagementLimitDelegate?
        _getPowerManagementLimit;

    private readonly NvmlDeviceGetPowerManagementDefaultLimitDelegate?
        _getPowerManagementDefaultLimit;

    private readonly NvmlDeviceGetPowerManagementLimitConstraintsDelegate?
        _getPowerManagementLimitConstraints;

    private readonly NvmlDeviceGetEnforcedPowerLimitDelegate?
        _getEnforcedPowerLimit;

    private readonly NvmlDeviceGetFieldValuesDelegate?
        _getFieldValues;

    private readonly string? _preferredDeviceName;
    private readonly bool _requirePreferredDevice;
    private IntPtr _device;
    private bool _initialized;
    private bool _disposed;

    public NvmlClient(
        string? preferredDeviceName = null,
        bool requirePreferredDevice = false)
    {
        _preferredDeviceName = preferredDeviceName;
        _requirePreferredDevice = requirePreferredDevice;
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

            _setGpuLockedClocks =
                TryGetDelegate<NvmlDeviceSetGpuLockedClocksDelegate>(
                    "nvmlDeviceSetGpuLockedClocks");

            _resetGpuLockedClocks =
                TryGetDelegate<NvmlDeviceResetGpuLockedClocksDelegate>(
                    "nvmlDeviceResetGpuLockedClocks");

            _getClockInfo =
                TryGetDelegate<NvmlDeviceGetClockInfoDelegate>(
                    "nvmlDeviceGetClockInfo");

            _getClock =
                TryGetDelegate<NvmlDeviceGetClockDelegate>(
                    "nvmlDeviceGetClock");

            _getCurrentClocksEventReasons =
                TryGetDelegate<NvmlDeviceGetCurrentClocksEventReasonsDelegate>(
                    "nvmlDeviceGetCurrentClocksEventReasons");

            _setPowerManagementLimit =
                TryGetDelegate<NvmlDeviceSetPowerManagementLimitDelegate>(
                    "nvmlDeviceSetPowerManagementLimit");

            _getPowerManagementMode =
                TryGetDelegate<NvmlDeviceGetPowerManagementModeDelegate>(
                    "nvmlDeviceGetPowerManagementMode");

            _getPowerManagementLimit =
                TryGetDelegate<NvmlDeviceGetPowerManagementLimitDelegate>(
                    "nvmlDeviceGetPowerManagementLimit");

            _getPowerManagementDefaultLimit =
                TryGetDelegate<NvmlDeviceGetPowerManagementDefaultLimitDelegate>(
                    "nvmlDeviceGetPowerManagementDefaultLimit");

            _getPowerManagementLimitConstraints =
                TryGetDelegate<NvmlDeviceGetPowerManagementLimitConstraintsDelegate>(
                    "nvmlDeviceGetPowerManagementLimitConstraints");

            _getEnforcedPowerLimit =
                TryGetDelegate<NvmlDeviceGetEnforcedPowerLimitDelegate>(
                    "nvmlDeviceGetEnforcedPowerLimit");

            _getFieldValues =
                TryGetDelegate<NvmlDeviceGetFieldValuesDelegate>(
                    "nvmlDeviceGetFieldValues");

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


    public NvmlGpuClockControlAvailability
        GpuClockControlAvailability =>
        new(
            SetLockedGraphicsClocksExportAvailable:
                _setGpuLockedClocks is not null,

            ResetLockedGraphicsClocksExportAvailable:
                _resetGpuLockedClocks is not null,

            CurrentGraphicsClockExportAvailable:
                _getClockInfo is not null,

            ApplicationGraphicsClockTargetExportAvailable:
                _getClock is not null,

            CurrentClocksEventReasonsExportAvailable:
                _getCurrentClocksEventReasons is not null,

            // NVML exposes set/reset for GPU locked clocks, but the public API
            // does not expose a getter for the exact requested min/max locked
            // range. Current clock is an observation, not ownership proof.
            ExactLockedRangeReadbackAvailable:
                false);

    /// <summary>
    /// Performs exactly one NVML set call and never retries/reinitializes.
    /// A future owner must durably journal authorization before invoking this
    /// method. This class does not itself grant hardware-write authority.
    /// </summary>
    public NvmlControlCallResult SetGpuLockedClocksOnce(
        uint minGraphicsClockMHz,
        uint maxGraphicsClockMHz)
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_setGpuLockedClocks is null)
        {
            return new NvmlControlCallResult(
                ExportAvailable: false,
                Result: null);
        }

        return new NvmlControlCallResult(
            ExportAvailable: true,
            Result:
                _setGpuLockedClocks(
                    _device,
                    minGraphicsClockMHz,
                    maxGraphicsClockMHz));
    }

    /// <summary>
    /// Performs exactly one NVML reset call and never retries/reinitializes.
    /// </summary>
    public NvmlControlCallResult ResetGpuLockedClocksOnce()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_resetGpuLockedClocks is null)
        {
            return new NvmlControlCallResult(
                ExportAvailable: false,
                Result: null);
        }

        return new NvmlControlCallResult(
            ExportAvailable: true,
            Result:
                _resetGpuLockedClocks(
                    _device));
    }

    /// <summary>
    /// Reads the current graphics clock once. This is deliberately not named
    /// "locked-clock readback": current frequency cannot prove the requested
    /// min/max locked range or VFC ownership.
    /// </summary>
    public NvmlUIntCallResult ReadCurrentGraphicsClockOnce()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_getClockInfo is null)
        {
            return new NvmlUIntCallResult(
                ExportAvailable: false,
                Result: null,
                Value: 0);
        }

        var result =
            _getClockInfo(
                _device,
                NvmlClockGraphics,
                out var clockMHz);

        return new NvmlUIntCallResult(
            ExportAvailable: true,
            Result: result,
            Value: clockMHz);
    }


    /// <summary>
    /// Reads the deprecated application-clock target for diagnostics only.
    /// nvmlDeviceSetGpuLockedClocks supersedes application clocks, and NVIDIA
    /// does not document this target as the exact locked-range max. Therefore
    /// this signal cannot prove ownership even when it numerically matches a
    /// requested maximum.
    /// </summary>
    public NvmlUIntCallResult ReadApplicationGraphicsClockTargetOnce()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_getClock is null)
        {
            return new NvmlUIntCallResult(
                ExportAvailable: false,
                Result: null,
                Value: 0);
        }

        var result =
            _getClock(
                _device,
                NvmlClockGraphics,
                NvmlClockIdApplicationTarget,
                out var clockMHz);

        return new NvmlUIntCallResult(
            ExportAvailable: true,
            Result: result,
            Value: clockMHz);
    }

    /// <summary>
    /// Reads current clock event reasons for observability qualification.
    /// User-defined/application-clock event bits can indicate a clock policy is
    /// affecting frequency, but they do not expose the min/max locked range or
    /// identify which process installed it.
    /// </summary>
    public NvmlULongCallResult ReadCurrentClocksEventReasonsOnce()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_getCurrentClocksEventReasons is null)
        {
            return new NvmlULongCallResult(
                ExportAvailable: false,
                Result: null,
                Value: 0);
        }

        var result =
            _getCurrentClocksEventReasons(
                _device,
                out var reasons);

        return new NvmlULongCallResult(
            ExportAvailable: true,
            Result: result,
            Value: reasons);
    }


    public NvmlGpuPowerLimitAvailability
        GpuPowerLimitAvailability =>
        new(
            SetPowerManagementLimitExportAvailable:
                _setPowerManagementLimit is not null,
            PowerManagementModeExportAvailable:
                _getPowerManagementMode is not null,
            PowerManagementLimitExportAvailable:
                _getPowerManagementLimit is not null,
            DefaultPowerManagementLimitExportAvailable:
                _getPowerManagementDefaultLimit is not null,
            PowerManagementLimitConstraintsExportAvailable:
                _getPowerManagementLimitConstraints is not null,
            EnforcedPowerLimitExportAvailable:
                _getEnforcedPowerLimit is not null);

    public NvmlUIntCallResult ReadPowerManagementModeOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getPowerManagementMode is null)
            return new NvmlUIntCallResult(false, null, 0);

        var result = _getPowerManagementMode(_device, out var mode);
        return new NvmlUIntCallResult(true, result, mode);
    }

    public NvmlUIntCallResult ReadPowerManagementLimitOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getPowerManagementLimit is null)
            return new NvmlUIntCallResult(false, null, 0);

        var result = _getPowerManagementLimit(_device, out var limitMilliwatts);
        return new NvmlUIntCallResult(true, result, limitMilliwatts);
    }

    public NvmlUIntCallResult ReadDefaultPowerManagementLimitOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getPowerManagementDefaultLimit is null)
            return new NvmlUIntCallResult(false, null, 0);

        var result =
            _getPowerManagementDefaultLimit(
                _device,
                out var limitMilliwatts);

        return new NvmlUIntCallResult(true, result, limitMilliwatts);
    }

    public NvmlPowerLimitConstraintsCallResult
        ReadPowerManagementLimitConstraintsOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getPowerManagementLimitConstraints is null)
            return new NvmlPowerLimitConstraintsCallResult(false, null, 0, 0);

        var result =
            _getPowerManagementLimitConstraints(
                _device,
                out var minMilliwatts,
                out var maxMilliwatts);

        return new NvmlPowerLimitConstraintsCallResult(
            true,
            result,
            minMilliwatts,
            maxMilliwatts);
    }

    public NvmlUIntCallResult ReadEnforcedPowerLimitOnce()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_getEnforcedPowerLimit is null)
            return new NvmlUIntCallResult(false, null, 0);

        var result = _getEnforcedPowerLimit(_device, out var limitMilliwatts);
        return new NvmlUIntCallResult(true, result, limitMilliwatts);
    }


    public bool PowerFieldValuesExportAvailable =>
        _getFieldValues is not null;

    /// <summary>
    /// Reads the official NVML power field-value set once, including
    /// NVML_FI_DEV_POWER_REQUESTED_LIMIT. No retry or mutation occurs here.
    /// </summary>
    public NvmlGpuPowerFieldSnapshot ReadPowerFieldSnapshotOnce()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);

        if (_getFieldValues is null)
        {
            var unavailable =
                new NvmlFieldUnsignedCallResult(
                    ExportAvailable: false,
                    QueryResult: null,
                    FieldResult: 0,
                    FieldId: 0,
                    ValueType: 0,
                    UnsignedValueDecoded: false,
                    Value: 0);

            return new NvmlGpuPowerFieldSnapshot(
                ExportAvailable: false,
                QueryResult: null,
                MinLimit: unavailable,
                MaxLimit: unavailable,
                DefaultLimit: unavailable,
                CurrentLimit: unavailable,
                RequestedLimit: unavailable);
        }

        var values =
            new[]
            {
                Field(NvmlFieldPowerMinLimit),
                Field(NvmlFieldPowerMaxLimit),
                Field(NvmlFieldPowerDefaultLimit),
                Field(NvmlFieldPowerCurrentLimit),
                Field(NvmlFieldPowerRequestedLimit)
            };

        var queryResult =
            _getFieldValues(
                _device,
                values.Length,
                values);

        return new NvmlGpuPowerFieldSnapshot(
            ExportAvailable: true,
            QueryResult: queryResult,
            MinLimit:
                DecodePowerField(
                    queryResult,
                    values[0]),
            MaxLimit:
                DecodePowerField(
                    queryResult,
                    values[1]),
            DefaultLimit:
                DecodePowerField(
                    queryResult,
                    values[2]),
            CurrentLimit:
                DecodePowerField(
                    queryResult,
                    values[3]),
            RequestedLimit:
                DecodePowerField(
                    queryResult,
                    values[4]));
    }

    private static NvmlFieldValue Field(
        uint fieldId) =>
        new()
        {
            FieldId = fieldId,
            ScopeId = 0
        };

    private static NvmlFieldUnsignedCallResult DecodePowerField(
        int queryResult,
        NvmlFieldValue field)
    {
        var decoded = false;
        ulong value = 0;

        if (queryResult == NvmlSuccess &&
            field.NvmlReturn == NvmlSuccess)
        {
            switch (field.ValueType)
            {
                case NvmlValueTypeUnsignedInt:
                    value =
                        field.Value.UnsignedInt;
                    decoded = true;
                    break;

                case NvmlValueTypeUnsignedLong:
                    // Windows NVML uses the Win32 C ABI where unsigned long is
                    // 32-bit even in a 64-bit process.
                    value =
                        field.Value.UnsignedLong;
                    decoded = true;
                    break;

                case NvmlValueTypeUnsignedLongLong:
                    value =
                        field.Value.UnsignedLongLong;
                    decoded = true;
                    break;

                case NvmlValueTypeUnsignedShort:
                    value =
                        field.Value.UnsignedShort;
                    decoded = true;
                    break;
            }
        }

        return new NvmlFieldUnsignedCallResult(
            ExportAvailable: true,
            QueryResult: queryResult,
            FieldResult: field.NvmlReturn,
            FieldId: field.FieldId,
            ValueType: field.ValueType,
            UnsignedValueDecoded: decoded,
            Value: value);
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
            // On the 8C40 hybrid-GPU target we have physically observed NVML
            // return SUCCESS with 0 C while the dGPU is in a low-power/RTD3-like
            // state. 0 C is not a usable thermal reading and must never make a
            // telemetry snapshot look healthy. Keep this aligned with the
            // SafetyGate validated GPU plausibility floor.
            value => value is >= 10 and <= 125);

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

        if (_requirePreferredDevice &&
            !string.IsNullOrWhiteSpace(_preferredDeviceName))
        {
            throw new InvalidOperationException(
                $"NVML did not find the exact validated GPU '{_preferredDeviceName}'.");
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
        return TryGetDelegate<T>(exports) ??
            throw new EntryPointNotFoundException(
                $"NVML export not found: {string.Join(" or ", exports)}");
    }

    private T? TryGetDelegate<T>(
        params string[] exports)
        where T : Delegate
    {
        foreach (var export in exports)
        {
            if (NativeLibrary.TryGetExport(
                    _library,
                    export,
                    out var address))
            {
                return Marshal.GetDelegateForFunctionPointer<T>(
                    address);
            }
        }

        return null;
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


    [StructLayout(LayoutKind.Explicit)]
    private struct NvmlValue
    {
        [FieldOffset(0)]
        public double Double;

        [FieldOffset(0)]
        public uint UnsignedInt;

        [FieldOffset(0)]
        public uint UnsignedLong;

        [FieldOffset(0)]
        public ulong UnsignedLongLong;

        [FieldOffset(0)]
        public long SignedLongLong;

        [FieldOffset(0)]
        public int SignedInt;

        [FieldOffset(0)]
        public ushort UnsignedShort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlFieldValue
    {
        public uint FieldId;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyUsec;
        public int ValueType;
        public int NvmlReturn;
        public NvmlValue Value;
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
    private delegate int NvmlDeviceGetTemperatureDelegate(
        IntPtr device,
        uint sensorType,
        out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceSetGpuLockedClocksDelegate(
        IntPtr device,
        uint minGpuClockMHz,
        uint maxGpuClockMHz);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceResetGpuLockedClocksDelegate(
        IntPtr device);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetClockInfoDelegate(
        IntPtr device,
        uint clockType,
        out uint clockMHz);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetClockDelegate(
        IntPtr device,
        uint clockType,
        uint clockId,
        out uint clockMHz);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetCurrentClocksEventReasonsDelegate(
        IntPtr device,
        out ulong clocksEventReasons);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceSetPowerManagementLimitDelegate(
        IntPtr device,
        uint limitMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetPowerManagementModeDelegate(
        IntPtr device,
        out uint mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetPowerManagementLimitDelegate(
        IntPtr device,
        out uint limitMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetPowerManagementDefaultLimitDelegate(
        IntPtr device,
        out uint defaultLimitMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetPowerManagementLimitConstraintsDelegate(
        IntPtr device,
        out uint minLimitMilliwatts,
        out uint maxLimitMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetEnforcedPowerLimitDelegate(
        IntPtr device,
        out uint limitMilliwatts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvmlDeviceGetFieldValuesDelegate(
        IntPtr device,
        int valuesCount,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
        NvmlFieldValue[] values);

    internal readonly record struct GpuSample(
        double TemperatureC,
        double PowerW,
        double LoadPercent);
}
