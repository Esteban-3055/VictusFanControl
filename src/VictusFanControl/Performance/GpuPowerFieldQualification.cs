using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal enum GpuPowerFieldQualificationDisposition
{
    CandidateForControlledWriteQualification,
    FieldApiUnavailable,
    RequiredFieldUnavailable,
    InvalidReadback,
    FixedPowerRange,
    SetterExportUnavailable
}

internal readonly record struct GpuPowerFieldQualificationResult(
    GpuPowerFieldQualificationDisposition Disposition,
    bool ExactUserspaceRequestedLimitReadbackAvailable,
    bool AdjustableRangeObserved,
    bool ControlledWriteQualificationRecommended,
    bool ProductionWriteAuthorized,
    uint? RequestedLimitMilliwatts,
    uint? CurrentEnforcedLimitMilliwatts,
    uint? DefaultLimitMilliwatts,
    uint? MinLimitMilliwatts,
    uint? MaxLimitMilliwatts,
    string Status);

/// <summary>
/// Pure qualification of NVML field-value power telemetry.
///
/// The key field is NVML_FI_DEV_POWER_REQUESTED_LIMIT, documented by NVIDIA as
/// the power limit requested by NVML or another userspace client. This is the
/// candidate exact ownership field for a future write/readback controller.
/// </summary>
internal static class GpuPowerFieldQualification
{
    internal static GpuPowerFieldQualificationResult Assess(
        NvmlGpuPowerFieldSnapshot snapshot,
        bool setterExportAvailable)
    {
        if (!snapshot.ExportAvailable ||
            snapshot.QueryResult != 0)
        {
            return Blocked(
                GpuPowerFieldQualificationDisposition.FieldApiUnavailable,
                "GPU_POWER_FIELD_QUALIFICATION_FIELD_API_UNAVAILABLE");
        }

        if (!snapshot.MinLimit.IsSuccess ||
            !snapshot.MaxLimit.IsSuccess ||
            !snapshot.DefaultLimit.IsSuccess ||
            !snapshot.RequestedLimit.IsSuccess)
        {
            return Blocked(
                GpuPowerFieldQualificationDisposition.RequiredFieldUnavailable,
                "GPU_POWER_FIELD_QUALIFICATION_REQUIRED_FIELD_UNAVAILABLE");
        }

        if (!TryUInt(
                snapshot.MinLimit,
                out var min) ||
            !TryUInt(
                snapshot.MaxLimit,
                out var max) ||
            !TryUInt(
                snapshot.DefaultLimit,
                out var defaultLimit) ||
            !TryUInt(
                snapshot.RequestedLimit,
                out var requested))
        {
            return Blocked(
                GpuPowerFieldQualificationDisposition.InvalidReadback,
                "GPU_POWER_FIELD_QUALIFICATION_VALUE_OUT_OF_RANGE");
        }

        if (min == 0 ||
            max == 0 ||
            min > max ||
            defaultLimit < min ||
            defaultLimit > max ||
            requested < min ||
            requested > max)
        {
            return Blocked(
                GpuPowerFieldQualificationDisposition.InvalidReadback,
                "GPU_POWER_FIELD_QUALIFICATION_INVALID_LIMIT_RELATION");
        }

        uint? current = null;

        if (snapshot.CurrentLimit.IsSuccess &&
            TryUInt(
                snapshot.CurrentLimit,
                out var currentValue))
        {
            current = currentValue;
        }

        if (min == max)
        {
            return BuildReadable(
                GpuPowerFieldQualificationDisposition.FixedPowerRange,
                min,
                max,
                defaultLimit,
                requested,
                current,
                adjustable: false,
                writeTest: false,
                "GPU_POWER_FIELD_QUALIFICATION_FIXED_RANGE");
        }

        if (!setterExportAvailable)
        {
            return BuildReadable(
                GpuPowerFieldQualificationDisposition.SetterExportUnavailable,
                min,
                max,
                defaultLimit,
                requested,
                current,
                adjustable: true,
                writeTest: false,
                "GPU_POWER_FIELD_QUALIFICATION_SETTER_EXPORT_UNAVAILABLE");
        }

        return BuildReadable(
            GpuPowerFieldQualificationDisposition.CandidateForControlledWriteQualification,
            min,
            max,
            defaultLimit,
            requested,
            current,
            adjustable: true,
            writeTest: true,
            "GPU_POWER_FIELD_QUALIFICATION_REQUESTED_LIMIT_READY__WRITE_TEST_STILL_REQUIRED");
    }

    private static bool TryUInt(
        NvmlFieldUnsignedCallResult field,
        out uint value)
    {
        if (!field.IsSuccess ||
            field.Value > uint.MaxValue)
        {
            value = 0;
            return false;
        }

        value = (uint)field.Value;
        return true;
    }

    private static GpuPowerFieldQualificationResult BuildReadable(
        GpuPowerFieldQualificationDisposition disposition,
        uint min,
        uint max,
        uint defaultLimit,
        uint requested,
        uint? current,
        bool adjustable,
        bool writeTest,
        string status) =>
        new(
            disposition,
            ExactUserspaceRequestedLimitReadbackAvailable: true,
            AdjustableRangeObserved: adjustable,
            ControlledWriteQualificationRecommended: writeTest,
            ProductionWriteAuthorized: false,
            RequestedLimitMilliwatts: requested,
            CurrentEnforcedLimitMilliwatts: current,
            DefaultLimitMilliwatts: defaultLimit,
            MinLimitMilliwatts: min,
            MaxLimitMilliwatts: max,
            status);

    private static GpuPowerFieldQualificationResult Blocked(
        GpuPowerFieldQualificationDisposition disposition,
        string status) =>
        new(
            disposition,
            ExactUserspaceRequestedLimitReadbackAvailable: false,
            AdjustableRangeObserved: false,
            ControlledWriteQualificationRecommended: false,
            ProductionWriteAuthorized: false,
            RequestedLimitMilliwatts: null,
            CurrentEnforcedLimitMilliwatts: null,
            DefaultLimitMilliwatts: null,
            MinLimitMilliwatts: null,
            MaxLimitMilliwatts: null,
            status);
}
