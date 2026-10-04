using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal static class GpuPowerFieldQualificationSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            RequestedFieldCanQualifyExactUserspaceOwnership(output);
            MissingRequestedFieldFailsClosed(output);
            FixedRangeDoesNotAuthorizeWriteTest(output);
            MissingSetterExportKeepsReadbackReadOnly(output);
            InvalidRequestedValueFailsClosed(output);

            output.WriteLine(
                "GPU power field-value qualification self-test: PASS (read-only, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU power field-value qualification self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void RequestedFieldCanQualifyExactUserspaceOwnership(
        TextWriter output)
    {
        var result =
            GpuPowerFieldQualification.Assess(
                Snapshot(
                    min: 5000,
                    max: 75000,
                    defaultLimit: 60000,
                    current: 70000,
                    requested: 60000),
                setterExportAvailable: true);

        Require(
            result.Disposition ==
                GpuPowerFieldQualificationDisposition.CandidateForControlledWriteQualification &&
            result.ExactUserspaceRequestedLimitReadbackAvailable &&
            result.AdjustableRangeObserved &&
            result.ControlledWriteQualificationRecommended &&
            !result.ProductionWriteAuthorized &&
            result.RequestedLimitMilliwatts == 60000 &&
            result.CurrentEnforcedLimitMilliwatts == 70000,
            "requested field should qualify exact userspace readback");

        output.WriteLine(
            "PASS NVML requested-limit field can qualify exact userspace power-limit readback");
    }

    private static void MissingRequestedFieldFailsClosed(
        TextWriter output)
    {
        var snapshot =
            Snapshot(
                5000,
                75000,
                60000,
                70000,
                60000) with
            {
                RequestedLimit =
                    Field(
                        192,
                        fieldResult: 3,
                        value: 0)
            };

        var result =
            GpuPowerFieldQualification.Assess(
                snapshot,
                setterExportAvailable: true);

        RequireBlocked(
            result,
            GpuPowerFieldQualificationDisposition.RequiredFieldUnavailable,
            "missing requested limit");

        output.WriteLine(
            "PASS unavailable requested-limit field blocks ownership qualification");
    }

    private static void FixedRangeDoesNotAuthorizeWriteTest(
        TextWriter output)
    {
        var result =
            GpuPowerFieldQualification.Assess(
                Snapshot(
                    60000,
                    60000,
                    60000,
                    60000,
                    60000),
                setterExportAvailable: true);

        Require(
            result.Disposition ==
                GpuPowerFieldQualificationDisposition.FixedPowerRange &&
            result.ExactUserspaceRequestedLimitReadbackAvailable &&
            !result.AdjustableRangeObserved &&
            !result.ControlledWriteQualificationRecommended,
            "fixed field range");

        output.WriteLine(
            "PASS exact requested readback with fixed range remains non-writable");
    }

    private static void MissingSetterExportKeepsReadbackReadOnly(
        TextWriter output)
    {
        var result =
            GpuPowerFieldQualification.Assess(
                Snapshot(
                    5000,
                    75000,
                    60000,
                    70000,
                    60000),
                setterExportAvailable: false);

        Require(
            result.Disposition ==
                GpuPowerFieldQualificationDisposition.SetterExportUnavailable &&
            result.ExactUserspaceRequestedLimitReadbackAvailable &&
            result.AdjustableRangeObserved &&
            !result.ControlledWriteQualificationRecommended,
            "setter missing but readback valid");

        output.WriteLine(
            "PASS requested-limit readback without setter export stays read-only");
    }

    private static void InvalidRequestedValueFailsClosed(
        TextWriter output)
    {
        var result =
            GpuPowerFieldQualification.Assess(
                Snapshot(
                    5000,
                    75000,
                    60000,
                    70000,
                    90000),
                setterExportAvailable: true);

        RequireBlocked(
            result,
            GpuPowerFieldQualificationDisposition.InvalidReadback,
            "requested outside constraints");

        output.WriteLine(
            "PASS requested power outside reported constraints fails closed");
    }

    private static NvmlGpuPowerFieldSnapshot Snapshot(
        uint min,
        uint max,
        uint defaultLimit,
        uint current,
        uint requested) =>
        new(
            ExportAvailable: true,
            QueryResult: 0,
            MinLimit: Field(187, 0, min),
            MaxLimit: Field(188, 0, max),
            DefaultLimit: Field(189, 0, defaultLimit),
            CurrentLimit: Field(190, 0, current),
            RequestedLimit: Field(192, 0, requested));

    private static NvmlFieldUnsignedCallResult Field(
        uint fieldId,
        int fieldResult,
        uint value) =>
        new(
            ExportAvailable: true,
            QueryResult: 0,
            FieldResult: fieldResult,
            FieldId: fieldId,
            ValueType: 1,
            UnsignedValueDecoded: fieldResult == 0,
            Value: value);

    private static void RequireBlocked(
        GpuPowerFieldQualificationResult result,
        GpuPowerFieldQualificationDisposition expected,
        string label)
    {
        Require(
            result.Disposition == expected &&
            !result.ExactUserspaceRequestedLimitReadbackAvailable &&
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
