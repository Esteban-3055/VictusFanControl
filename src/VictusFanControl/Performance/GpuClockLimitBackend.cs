using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal enum GpuClockBackendFailureKind
{
    None,
    WriteGateClosed,
    ExportUnavailable,
    InvalidRequest,
    NotSupported,
    PermissionDenied,
    DriverUnavailable,
    DeviceUnavailable,
    NotReady,
    Unknown
}

internal readonly record struct GpuClockBackendCapabilities(
    bool SetLockedGraphicsClocksExportAvailable,
    bool ResetLockedGraphicsClocksExportAvailable,
    bool CurrentGraphicsClockExportAvailable,
    bool ExactLockedRangeReadbackAvailable,
    bool HardwareWritesAuthorized)
{
    internal bool HasCompleteCommandSurface =>
        SetLockedGraphicsClocksExportAvailable &&
        ResetLockedGraphicsClocksExportAvailable;

    internal bool CanProveExactLockedRangeOwnership =>
        ExactLockedRangeReadbackAvailable;
}

internal readonly record struct GpuClockBackendWriteResult(
    bool Succeeded,
    GpuClockBackendFailureKind FailureKind,
    int? NvmlResult,
    string Status);

internal readonly record struct GpuClockBackendObservation(
    bool Succeeded,
    uint? CurrentGraphicsClockMHz,
    bool ProvesExactLockedRangeOwnership,
    GpuClockBackendFailureKind FailureKind,
    int? NvmlResult,
    string Status);

internal interface IGpuClockLimitBackend
{
    GpuClockBackendCapabilities Capabilities { get; }

    GpuClockBackendWriteResult SetLockedGraphicsClocks(
        GpuClockLimitRequest request);

    GpuClockBackendWriteResult ResetLockedGraphicsClocks();

    GpuClockBackendObservation ReadObservation();
}

/// <summary>
/// Thin domain adapter over the in-process NVML transport.
///
/// This backend intentionally has no ownership state, journal, retry loop,
/// source policy or recovery policy. Those belong to the future GPU
/// controller. A write call maps to exactly one native NVML invocation.
///
/// Production writes are closed by default. The future qualified controller
/// must opt in explicitly when the hardware-write gate is opened.
/// </summary>
internal sealed class NvmlGpuClockLimitBackend :
    IGpuClockLimitBackend
{
    private const int NvmlSuccess = 0;
    private const int NvmlErrorUninitialized = 1;
    private const int NvmlErrorInvalidArgument = 2;
    private const int NvmlErrorNotSupported = 3;
    private const int NvmlErrorNoPermission = 4;
    private const int NvmlErrorDriverNotLoaded = 9;
    private const int NvmlErrorGpuIsLost = 15;
    private const int NvmlErrorResetRequired = 16;
    private const int NvmlErrorNotReady = 27;
    private const int NvmlErrorGpuNotFound = 28;

    private readonly INvmlGpuClockControlTransport _transport;
    private readonly bool _hardwareWritesAuthorized;

    internal NvmlGpuClockLimitBackend(
        INvmlGpuClockControlTransport transport,
        bool hardwareWritesAuthorized = false)
    {
        _transport =
            transport ??
            throw new ArgumentNullException(nameof(transport));

        _hardwareWritesAuthorized =
            hardwareWritesAuthorized;
    }

    public GpuClockBackendCapabilities Capabilities
    {
        get
        {
            var availability =
                _transport.GpuClockControlAvailability;

            return new GpuClockBackendCapabilities(
                availability.SetLockedGraphicsClocksExportAvailable,
                availability.ResetLockedGraphicsClocksExportAvailable,
                availability.CurrentGraphicsClockExportAvailable,
                availability.ExactLockedRangeReadbackAvailable,
                _hardwareWritesAuthorized);
        }
    }

    public GpuClockBackendWriteResult SetLockedGraphicsClocks(
        GpuClockLimitRequest request)
    {
        if (request.MinGraphicsClockMHz == 0 ||
            request.MaxGraphicsClockMHz == 0 ||
            request.MaxGraphicsClockMHz <
                request.MinGraphicsClockMHz)
        {
            return Failure(
                GpuClockBackendFailureKind.InvalidRequest,
                nvmlResult: null,
                "GPU_CLOCK_BACKEND_INVALID_REQUEST");
        }

        if (!_hardwareWritesAuthorized)
        {
            return Failure(
                GpuClockBackendFailureKind.WriteGateClosed,
                nvmlResult: null,
                "GPU_CLOCK_BACKEND_HARDWARE_WRITE_GATE_CLOSED");
        }

        var call =
            _transport.SetGpuLockedClocksOnce(
                request.MinGraphicsClockMHz,
                request.MaxGraphicsClockMHz);

        return MapWriteResult(
            call,
            "GPU_CLOCK_BACKEND_SET");
    }

    public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
    {
        if (!_hardwareWritesAuthorized)
        {
            return Failure(
                GpuClockBackendFailureKind.WriteGateClosed,
                nvmlResult: null,
                "GPU_CLOCK_BACKEND_HARDWARE_WRITE_GATE_CLOSED");
        }

        var call =
            _transport.ResetGpuLockedClocksOnce();

        return MapWriteResult(
            call,
            "GPU_CLOCK_BACKEND_RESET");
    }

    public GpuClockBackendObservation ReadObservation()
    {
        var call =
            _transport.ReadCurrentGraphicsClockOnce();

        if (!call.ExportAvailable)
        {
            return new GpuClockBackendObservation(
                Succeeded: false,
                CurrentGraphicsClockMHz: null,
                ProvesExactLockedRangeOwnership: false,
                FailureKind:
                    GpuClockBackendFailureKind.ExportUnavailable,
                NvmlResult: null,
                Status:
                    "GPU_CLOCK_BACKEND_CURRENT_CLOCK_EXPORT_UNAVAILABLE");
        }

        var failure =
            ClassifyNvmlResult(
                call.Result);

        if (failure !=
            GpuClockBackendFailureKind.None)
        {
            return new GpuClockBackendObservation(
                Succeeded: false,
                CurrentGraphicsClockMHz: null,
                ProvesExactLockedRangeOwnership: false,
                FailureKind: failure,
                NvmlResult: call.Result,
                Status:
                    $"GPU_CLOCK_BACKEND_CURRENT_CLOCK_NVML_{call.Result}");
        }

        return new GpuClockBackendObservation(
            Succeeded: true,
            CurrentGraphicsClockMHz: call.Value,
            // Current frequency is not a getter for the requested locked
            // min/max range. It must never be used as exact ownership proof.
            ProvesExactLockedRangeOwnership: false,
            FailureKind:
                GpuClockBackendFailureKind.None,
            NvmlResult:
                NvmlSuccess,
            Status:
                "GPU_CLOCK_BACKEND_CURRENT_CLOCK_OBSERVATION_ONLY");
    }

    private static GpuClockBackendWriteResult MapWriteResult(
        NvmlControlCallResult call,
        string operation)
    {
        if (!call.ExportAvailable)
        {
            return Failure(
                GpuClockBackendFailureKind.ExportUnavailable,
                nvmlResult: null,
                operation +
                "_EXPORT_UNAVAILABLE");
        }

        var failure =
            ClassifyNvmlResult(
                call.Result);

        if (failure ==
            GpuClockBackendFailureKind.None)
        {
            return new GpuClockBackendWriteResult(
                Succeeded: true,
                FailureKind:
                    GpuClockBackendFailureKind.None,
                NvmlResult:
                    NvmlSuccess,
                Status:
                    operation +
                    "_NVML_ACCEPTED");
        }

        return Failure(
            failure,
            call.Result,
            operation +
            $"_NVML_{call.Result}");
    }

    private static GpuClockBackendFailureKind ClassifyNvmlResult(
        int? result) =>
        result switch
        {
            NvmlSuccess =>
                GpuClockBackendFailureKind.None,

            NvmlErrorInvalidArgument =>
                GpuClockBackendFailureKind.InvalidRequest,

            NvmlErrorNotSupported =>
                GpuClockBackendFailureKind.NotSupported,

            NvmlErrorNoPermission =>
                GpuClockBackendFailureKind.PermissionDenied,

            NvmlErrorUninitialized or
            NvmlErrorDriverNotLoaded =>
                GpuClockBackendFailureKind.DriverUnavailable,

            NvmlErrorGpuIsLost or
            NvmlErrorResetRequired or
            NvmlErrorGpuNotFound =>
                GpuClockBackendFailureKind.DeviceUnavailable,

            NvmlErrorNotReady =>
                GpuClockBackendFailureKind.NotReady,

            _ =>
                GpuClockBackendFailureKind.Unknown
        };

    private static GpuClockBackendWriteResult Failure(
        GpuClockBackendFailureKind kind,
        int? nvmlResult,
        string status) =>
        new(
            Succeeded: false,
            FailureKind: kind,
            NvmlResult: nvmlResult,
            Status: status);
}
