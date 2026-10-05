using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal enum GpuPowerLimitQualificationDisposition
{
    CandidateForControlledWriteQualification,
    PowerManagementUnsupportedOrDisabled,
    IncompleteReadSurface,
    InvalidReadback,
    FixedPowerRange,
    SetterExportUnavailable
}

internal readonly record struct GpuPowerLimitReadSnapshot(
    NvmlGpuPowerLimitAvailability Availability,
    NvmlUIntCallResult PowerManagementMode,
    NvmlUIntCallResult CurrentLimit,
    NvmlUIntCallResult DefaultLimit,
    NvmlPowerLimitConstraintsCallResult Constraints,
    NvmlUIntCallResult EnforcedLimit);

internal readonly record struct GpuPowerLimitQualificationResult(
    GpuPowerLimitQualificationDisposition Disposition,
    bool ExactConfiguredLimitReadbackAvailable,
    bool AdjustableRangeObserved,
    bool ControlledWriteQualificationRecommended,
    bool ProductionWriteAuthorized,
    uint? CurrentLimitMilliwatts,
    uint? DefaultLimitMilliwatts,
    uint? MinLimitMilliwatts,
    uint? MaxLimitMilliwatts,
    uint? EnforcedLimitMilliwatts,
    string Status);

/// <summary>
/// Pure read-only qualification for the NVML power-limit path.
///
/// nvmlDeviceGetPowerManagementLimit is the exact configured userspace power
/// management field a future setter would mutate. The enforced limit is
/// deliberately separate because other limiters can make it lower.
/// </summary>
internal static class GpuPowerLimitQualification
{
    private const uint NvmlFeatureEnabled = 1;

    internal static GpuPowerLimitQualificationResult Assess(
        GpuPowerLimitReadSnapshot snapshot)
    {
        if (!snapshot.PowerManagementMode.IsSuccess ||
            snapshot.PowerManagementMode.Value != NvmlFeatureEnabled)
        {
            return Blocked(
                GpuPowerLimitQualificationDisposition.PowerManagementUnsupportedOrDisabled,
                snapshot,
                "GPU_POWER_LIMIT_QUALIFICATION_POWER_MANAGEMENT_NOT_ENABLED");
        }

        if (!snapshot.CurrentLimit.IsSuccess ||
            !snapshot.DefaultLimit.IsSuccess ||
            !snapshot.Constraints.IsSuccess)
        {
            return Blocked(
                GpuPowerLimitQualificationDisposition.IncompleteReadSurface,
                snapshot,
                "GPU_POWER_LIMIT_QUALIFICATION_REQUIRED_READBACK_UNAVAILABLE");
        }

        var current = snapshot.CurrentLimit.Value;
        var defaultLimit = snapshot.DefaultLimit.Value;
        var min = snapshot.Constraints.MinMilliwatts;
        var max = snapshot.Constraints.MaxMilliwatts;

        if (min == 0 ||
            max == 0 ||
            min > max ||
            current < min ||
            current > max ||
            defaultLimit < min ||
            defaultLimit > max)
        {
            return Blocked(
                GpuPowerLimitQualificationDisposition.InvalidReadback,
                snapshot,
                "GPU_POWER_LIMIT_QUALIFICATION_INVALID_LIMIT_RELATION");
        }

        if (min == max)
        {
            return BuildReadable(
                GpuPowerLimitQualificationDisposition.FixedPowerRange,
                snapshot,
                adjustable: false,
                writeTest: false,
                "GPU_POWER_LIMIT_QUALIFICATION_FIXED_RANGE");
        }

        if (!snapshot.Availability.SetPowerManagementLimitExportAvailable)
        {
            return BuildReadable(
                GpuPowerLimitQualificationDisposition.SetterExportUnavailable,
                snapshot,
                adjustable: true,
                writeTest: false,
                "GPU_POWER_LIMIT_QUALIFICATION_SETTER_EXPORT_UNAVAILABLE");
        }

        return BuildReadable(
            GpuPowerLimitQualificationDisposition.CandidateForControlledWriteQualification,
            snapshot,
            adjustable: true,
            writeTest: true,
            "GPU_POWER_LIMIT_QUALIFICATION_READ_SURFACE_READY__WRITE_TEST_STILL_REQUIRED");
    }

    private static GpuPowerLimitQualificationResult BuildReadable(
        GpuPowerLimitQualificationDisposition disposition,
        GpuPowerLimitReadSnapshot snapshot,
        bool adjustable,
        bool writeTest,
        string status) =>
        new(
            disposition,
            ExactConfiguredLimitReadbackAvailable: true,
            AdjustableRangeObserved: adjustable,
            ControlledWriteQualificationRecommended: writeTest,
            ProductionWriteAuthorized: false,
            CurrentLimitMilliwatts: snapshot.CurrentLimit.Value,
            DefaultLimitMilliwatts: snapshot.DefaultLimit.Value,
            MinLimitMilliwatts: snapshot.Constraints.MinMilliwatts,
            MaxLimitMilliwatts: snapshot.Constraints.MaxMilliwatts,
            EnforcedLimitMilliwatts:
                snapshot.EnforcedLimit.IsSuccess
                    ? snapshot.EnforcedLimit.Value
                    : null,
            status);

    private static GpuPowerLimitQualificationResult Blocked(
        GpuPowerLimitQualificationDisposition disposition,
        GpuPowerLimitReadSnapshot snapshot,
        string status) =>
        new(
            disposition,
            ExactConfiguredLimitReadbackAvailable: false,
            AdjustableRangeObserved: false,
            ControlledWriteQualificationRecommended: false,
            ProductionWriteAuthorized: false,
            CurrentLimitMilliwatts:
                snapshot.CurrentLimit.IsSuccess ? snapshot.CurrentLimit.Value : null,
            DefaultLimitMilliwatts:
                snapshot.DefaultLimit.IsSuccess ? snapshot.DefaultLimit.Value : null,
            MinLimitMilliwatts:
                snapshot.Constraints.IsSuccess ? snapshot.Constraints.MinMilliwatts : null,
            MaxLimitMilliwatts:
                snapshot.Constraints.IsSuccess ? snapshot.Constraints.MaxMilliwatts : null,
            EnforcedLimitMilliwatts:
                snapshot.EnforcedLimit.IsSuccess ? snapshot.EnforcedLimit.Value : null,
            status);
}
