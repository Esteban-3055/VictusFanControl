namespace VictusFanControl.Hardware.Nvidia;

/// <summary>
/// Presence of the native NVML exports used by the future GPU clock owner.
///
/// Export availability is not the same as device support. A present setter can
/// still return NVML_ERROR_NOT_SUPPORTED for a particular GPU/driver state.
/// </summary>
internal readonly record struct NvmlGpuClockControlAvailability(
    bool SetLockedGraphicsClocksExportAvailable,
    bool ResetLockedGraphicsClocksExportAvailable,
    bool CurrentGraphicsClockExportAvailable,
    bool ApplicationGraphicsClockTargetExportAvailable,
    bool CurrentClocksEventReasonsExportAvailable,
    bool ExactLockedRangeReadbackAvailable)
{
    internal bool HasWriteExports =>
        SetLockedGraphicsClocksExportAvailable &&
        ResetLockedGraphicsClocksExportAvailable;
}

/// <summary>
/// Raw result of one NVML command invocation.
///
/// Result is null only when the required native export does not exist.
/// No retry semantics are hidden in this transport contract.
/// </summary>
internal readonly record struct NvmlControlCallResult(
    bool ExportAvailable,
    int? Result)
{
    internal bool IsSuccess =>
        ExportAvailable &&
        Result == 0;
}

/// <summary>
/// Raw result of one NVML unsigned-integer query.
/// </summary>
internal readonly record struct NvmlUIntCallResult(
    bool ExportAvailable,
    int? Result,
    uint Value)
{
    internal bool IsSuccess =>
        ExportAvailable &&
        Result == 0;
}

/// <summary>
/// Raw result of one NVML unsigned-64 query.
/// </summary>
internal readonly record struct NvmlULongCallResult(
    bool ExportAvailable,
    int? Result,
    ulong Value)
{
    internal bool IsSuccess =>
        ExportAvailable &&
        Result == 0;
}

/// <summary>
/// Minimal in-process NVML transport needed by the future GPU clock backend.
///
/// Set/Reset methods are intentionally "Once": a caller must treat the write
/// result as potentially ambiguous and must never rely on this layer to retry
/// a hardware mutation.
///
/// NVML has no public getter for the exact min/max range previously requested
/// through nvmlDeviceSetGpuLockedClocks. ReadCurrentGraphicsClockOnce is
/// therefore observation only and cannot establish ownership by itself.
/// </summary>
internal interface INvmlGpuClockControlTransport
{
    NvmlGpuClockControlAvailability
        GpuClockControlAvailability { get; }

    NvmlControlCallResult SetGpuLockedClocksOnce(
        uint minGraphicsClockMHz,
        uint maxGraphicsClockMHz);

    NvmlControlCallResult ResetGpuLockedClocksOnce();

    NvmlUIntCallResult ReadCurrentGraphicsClockOnce();

    NvmlUIntCallResult ReadApplicationGraphicsClockTargetOnce();

    NvmlULongCallResult ReadCurrentClocksEventReasonsOnce();
}
