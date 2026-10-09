namespace VictusFanControl.Hardware.Nvidia;

/// <summary>
/// Public NVML power-management query surface used for read-only
/// qualification before any GPU power-limit write path exists.
/// </summary>
internal readonly record struct NvmlGpuPowerLimitAvailability(
    bool SetPowerManagementLimitExportAvailable,
    bool PowerManagementModeExportAvailable,
    bool PowerManagementLimitExportAvailable,
    bool DefaultPowerManagementLimitExportAvailable,
    bool PowerManagementLimitConstraintsExportAvailable,
    bool EnforcedPowerLimitExportAvailable);

internal readonly record struct NvmlPowerLimitConstraintsCallResult(
    bool ExportAvailable,
    int? Result,
    uint MinMilliwatts,
    uint MaxMilliwatts)
{
    internal bool IsSuccess =>
        ExportAvailable &&
        Result == 0;
}

/// <summary>
/// Read-only transport for power-management qualification.
///
/// The setter export is reported only as capability metadata. This contract
/// deliberately exposes no power-limit write method.
/// </summary>
internal interface INvmlGpuPowerLimitReadTransport
{
    NvmlGpuPowerLimitAvailability
        GpuPowerLimitAvailability { get; }

    NvmlUIntCallResult ReadPowerManagementModeOnce();

    NvmlUIntCallResult ReadPowerManagementLimitOnce();

    NvmlUIntCallResult ReadDefaultPowerManagementLimitOnce();

    NvmlPowerLimitConstraintsCallResult
        ReadPowerManagementLimitConstraintsOnce();

    NvmlUIntCallResult ReadEnforcedPowerLimitOnce();
}
