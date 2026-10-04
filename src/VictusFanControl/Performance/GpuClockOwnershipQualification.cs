namespace VictusFanControl.Performance;

internal enum GpuClockOwnershipQualificationDisposition
{
    ExactRangeVerified,
    BlockedNoExactRangeReadback,
    BlockedExactRangeUnavailable,
    BlockedExactRangeMismatch
}

internal readonly record struct GpuClockOwnershipQualificationResult(
    GpuClockOwnershipQualificationDisposition Disposition,
    bool ManagedOwnershipAllowed,
    bool AutomaticReacquireAllowed,
    bool ConditionalResetAllowed,
    string Status);

/// <summary>
/// Safety gate between a successful NVML command and managed GPU ownership.
///
/// CPU RAPL can compare exact owned fields after every write. Public NVML does
/// not expose the exact min/max range installed by
/// nvmlDeviceSetGpuLockedClocks. A successful command, current clock,
/// deprecated application-clock target, or clock-event reason is therefore
/// insufficient to authorize bounded reacquire or a conditional reset.
///
/// This policy intentionally blocks managed ownership until an exact-range
/// verifier is available and separately qualified for the target.
/// </summary>
internal static class GpuClockOwnershipQualification
{
    internal static GpuClockOwnershipQualificationResult Assess(
        GpuClockLimitRequest requested,
        GpuClockBackendCapabilities capabilities,
        GpuClockBackendObservation observation)
    {
        if (!capabilities.ExactLockedRangeReadbackAvailable)
        {
            return Blocked(
                GpuClockOwnershipQualificationDisposition
                    .BlockedNoExactRangeReadback,
                "GPU_CLOCK_OWNERSHIP_BLOCKED__NVML_HAS_NO_EXACT_LOCK_RANGE_GETTER");
        }

        if (!observation.ProvesExactLockedRangeOwnership ||
            !observation.ExactLockedRange.HasValue)
        {
            return Blocked(
                GpuClockOwnershipQualificationDisposition
                    .BlockedExactRangeUnavailable,
                "GPU_CLOCK_OWNERSHIP_BLOCKED__EXACT_RANGE_NOT_OBSERVED");
        }

        if (observation.ExactLockedRange.Value !=
            requested)
        {
            return Blocked(
                GpuClockOwnershipQualificationDisposition
                    .BlockedExactRangeMismatch,
                "GPU_CLOCK_OWNERSHIP_BLOCKED__EXACT_RANGE_MISMATCH");
        }

        return new GpuClockOwnershipQualificationResult(
            GpuClockOwnershipQualificationDisposition
                .ExactRangeVerified,
            ManagedOwnershipAllowed: true,
            AutomaticReacquireAllowed: true,
            ConditionalResetAllowed: true,
            Status:
                "GPU_CLOCK_OWNERSHIP_EXACT_RANGE_VERIFIED");
    }

    private static GpuClockOwnershipQualificationResult Blocked(
        GpuClockOwnershipQualificationDisposition disposition,
        string status) =>
        new(
            disposition,
            ManagedOwnershipAllowed: false,
            AutomaticReacquireAllowed: false,
            ConditionalResetAllowed: false,
            status);
}
