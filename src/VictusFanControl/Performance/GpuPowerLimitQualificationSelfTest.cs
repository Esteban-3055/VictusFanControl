using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal static class GpuPowerLimitQualificationSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            AdjustableExactReadSurfaceBecomesCandidate(output);
            FixedRangeIsNotAWriteCandidate(output);
            DisabledModeFailsClosed(output);
            MissingCurrentReadbackFailsClosed(output);
            SetterExportAbsenceBlocksWriteQualification(output);
            EnforcedLimitMayDifferWithoutDestroyingConfiguredReadback(output);

            output.WriteLine(
                "GPU power-limit qualification self-test: PASS (read-only, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU power-limit qualification self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void AdjustableExactReadSurfaceBecomesCandidate(TextWriter output)
    {
        var result =
            GpuPowerLimitQualification.Assess(
                Snapshot(115000, 115000, 60000, 115000, 115000));

        Require(
            result.Disposition ==
                GpuPowerLimitQualificationDisposition.CandidateForControlledWriteQualification &&
            result.ExactConfiguredLimitReadbackAvailable &&
            result.AdjustableRangeObserved &&
            result.ControlledWriteQualificationRecommended &&
            !result.ProductionWriteAuthorized,
            "adjustable exact read surface qualification");

        output.WriteLine(
            "PASS readable adjustable GPU power range becomes write-test candidate, not production authority");
    }

    private static void FixedRangeIsNotAWriteCandidate(TextWriter output)
    {
        var result =
            GpuPowerLimitQualification.Assess(
                Snapshot(115000, 115000, 115000, 115000, 115000));

        Require(
            result.Disposition ==
                GpuPowerLimitQualificationDisposition.FixedPowerRange &&
            result.ExactConfiguredLimitReadbackAvailable &&
            !result.AdjustableRangeObserved &&
            !result.ControlledWriteQualificationRecommended,
            "fixed range is read-only");

        output.WriteLine(
            "PASS fixed GPU power range is not promoted to writable capability");
    }

    private static void DisabledModeFailsClosed(TextWriter output)
    {
        var snapshot =
            Snapshot(115000, 115000, 60000, 115000, 115000) with
            {
                PowerManagementMode =
                    new NvmlUIntCallResult(true, 0, 0)
            };

        var result = GpuPowerLimitQualification.Assess(snapshot);

        RequireBlocked(
            result,
            GpuPowerLimitQualificationDisposition.PowerManagementUnsupportedOrDisabled,
            "disabled power-management mode");

        output.WriteLine(
            "PASS disabled power-management mode fails closed");
    }

    private static void MissingCurrentReadbackFailsClosed(TextWriter output)
    {
        var snapshot =
            Snapshot(115000, 115000, 60000, 115000, 115000) with
            {
                CurrentLimit =
                    new NvmlUIntCallResult(true, 3, 0)
            };

        var result = GpuPowerLimitQualification.Assess(snapshot);

        RequireBlocked(
            result,
            GpuPowerLimitQualificationDisposition.IncompleteReadSurface,
            "missing exact configured limit");

        output.WriteLine(
            "PASS missing configured-limit getter blocks ownership qualification");
    }

    private static void SetterExportAbsenceBlocksWriteQualification(TextWriter output)
    {
        var snapshot =
            Snapshot(115000, 115000, 60000, 115000, 115000) with
            {
                Availability = Availability(setter: false)
            };

        var result = GpuPowerLimitQualification.Assess(snapshot);

        Require(
            result.Disposition ==
                GpuPowerLimitQualificationDisposition.SetterExportUnavailable &&
            result.ExactConfiguredLimitReadbackAvailable &&
            result.AdjustableRangeObserved &&
            !result.ControlledWriteQualificationRecommended,
            "setter export absence");

        output.WriteLine(
            "PASS exact readback without setter export remains read-only");
    }

    private static void EnforcedLimitMayDifferWithoutDestroyingConfiguredReadback(TextWriter output)
    {
        var result =
            GpuPowerLimitQualification.Assess(
                Snapshot(90000, 115000, 60000, 115000, 80000));

        Require(
            result.ExactConfiguredLimitReadbackAvailable &&
            result.CurrentLimitMilliwatts == 90000 &&
            result.EnforcedLimitMilliwatts == 80000,
            "configured/enforced distinction");

        output.WriteLine(
            "PASS enforced power limit is modeled separately from exact configured NVML limit");
    }

    private static GpuPowerLimitReadSnapshot Snapshot(
        uint current,
        uint defaultLimit,
        uint min,
        uint max,
        uint enforced) =>
        new(
            Availability(setter: true),
            new NvmlUIntCallResult(true, 0, 1),
            new NvmlUIntCallResult(true, 0, current),
            new NvmlUIntCallResult(true, 0, defaultLimit),
            new NvmlPowerLimitConstraintsCallResult(true, 0, min, max),
            new NvmlUIntCallResult(true, 0, enforced));

    private static NvmlGpuPowerLimitAvailability Availability(bool setter) =>
        new(
            SetPowerManagementLimitExportAvailable: setter,
            PowerManagementModeExportAvailable: true,
            PowerManagementLimitExportAvailable: true,
            DefaultPowerManagementLimitExportAvailable: true,
            PowerManagementLimitConstraintsExportAvailable: true,
            EnforcedPowerLimitExportAvailable: true);

    private static void RequireBlocked(
        GpuPowerLimitQualificationResult result,
        GpuPowerLimitQualificationDisposition expected,
        string label)
    {
        Require(
            result.Disposition == expected &&
            !result.ExactConfiguredLimitReadbackAvailable &&
            !result.AdjustableRangeObserved &&
            !result.ControlledWriteQualificationRecommended &&
            !result.ProductionWriteAuthorized,
            label);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
