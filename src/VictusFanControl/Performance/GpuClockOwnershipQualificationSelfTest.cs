namespace VictusFanControl.Performance;

internal static class GpuClockOwnershipQualificationSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            PublicNvmlTelemetryCannotGrantOwnership(output);
            MatchingHeuristicSignalsStillCannotGrantOwnership(output);
            ExactFutureVerifierCanGrantOwnership(output);
            ExactMismatchFailsClosed(output);

            output.WriteLine(
                "GPU clock ownership qualification self-test: PASS (heuristic NVML signals never grant exact ownership).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock ownership qualification self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void PublicNvmlTelemetryCannotGrantOwnership(
        TextWriter output)
    {
        var requested =
            new GpuClockLimitRequest(
                210,
                1850);

        var result =
            GpuClockOwnershipQualification.Assess(
                requested,
                Capabilities(
                    exactReadback: false),
                Observation(
                    current: 1800,
                    appTarget: null,
                    eventReasons: null));

        Require(
            result.Disposition ==
                GpuClockOwnershipQualificationDisposition
                    .BlockedNoExactRangeReadback,
            "public NVML no-getter disposition");

        RequireBlocked(
            result,
            "public NVML telemetry");

        output.WriteLine(
            "PASS successful/current-clock telemetry alone cannot grant managed GPU ownership");
    }

    private static void MatchingHeuristicSignalsStillCannotGrantOwnership(
        TextWriter output)
    {
        var requested =
            new GpuClockLimitRequest(
                210,
                1850);

        var result =
            GpuClockOwnershipQualification.Assess(
                requested,
                Capabilities(
                    exactReadback: false),
                Observation(
                    current: 1850,
                    appTarget: 1850,
                    eventReasons: 0x2));

        RequireBlocked(
            result,
            "matching app-target/event-reason heuristic");

        Require(
            result.Disposition ==
                GpuClockOwnershipQualificationDisposition
                    .BlockedNoExactRangeReadback,
            "heuristic match remains no-exact-readback");

        output.WriteLine(
            "PASS matching current/app-target/event-reason signals remain diagnostics, not ownership proof");
    }

    private static void ExactFutureVerifierCanGrantOwnership(
        TextWriter output)
    {
        var requested =
            new GpuClockLimitRequest(
                210,
                1200);

        var result =
            GpuClockOwnershipQualification.Assess(
                requested,
                Capabilities(
                    exactReadback: true),
                Observation(
                    current: 1200,
                    appTarget: 1200,
                    eventReasons: 0x2,
                    exact: requested,
                    provesExact: true));

        Require(
            result.Disposition ==
                GpuClockOwnershipQualificationDisposition
                    .ExactRangeVerified &&
            result.ManagedOwnershipAllowed &&
            result.AutomaticReacquireAllowed &&
            result.ConditionalResetAllowed,
            "exact future verifier grants full managed ownership");

        output.WriteLine(
            "PASS only an exact qualified min/max verifier can unlock reacquire and conditional reset");
    }

    private static void ExactMismatchFailsClosed(
        TextWriter output)
    {
        var result =
            GpuClockOwnershipQualification.Assess(
                new GpuClockLimitRequest(
                    210,
                    1850),
                Capabilities(
                    exactReadback: true),
                Observation(
                    current: 1200,
                    appTarget: 1200,
                    eventReasons: 0x2,
                    exact:
                        new GpuClockLimitRequest(
                            210,
                            1200),
                    provesExact: true));

        Require(
            result.Disposition ==
                GpuClockOwnershipQualificationDisposition
                    .BlockedExactRangeMismatch,
            "exact mismatch disposition");

        RequireBlocked(
            result,
            "exact mismatch");

        output.WriteLine(
            "PASS exact range mismatch blocks ownership and all automatic corrective actions");
    }

    private static GpuClockBackendCapabilities Capabilities(
        bool exactReadback) =>
        new(
            SetLockedGraphicsClocksExportAvailable: true,
            ResetLockedGraphicsClocksExportAvailable: true,
            CurrentGraphicsClockExportAvailable: true,
            ApplicationGraphicsClockTargetExportAvailable: true,
            CurrentClocksEventReasonsExportAvailable: true,
            ExactLockedRangeReadbackAvailable:
                exactReadback,
            HardwareWritesAuthorized: false);

    private static GpuClockBackendObservation Observation(
        uint? current,
        uint? appTarget,
        ulong? eventReasons,
        GpuClockLimitRequest? exact = null,
        bool provesExact = false) =>
        new(
            Succeeded: true,
            CurrentGraphicsClockMHz: current,
            ApplicationGraphicsClockTargetMHz:
                appTarget,
            CurrentClocksEventReasons:
                eventReasons,
            ExactLockedRange: exact,
            ProvesExactLockedRangeOwnership:
                provesExact,
            FailureKind:
                GpuClockBackendFailureKind.None,
            NvmlResult: 0,
            Status:
                "FIXTURE");

    private static void RequireBlocked(
        GpuClockOwnershipQualificationResult result,
        string label)
    {
        Require(
            !result.ManagedOwnershipAllowed &&
            !result.AutomaticReacquireAllowed &&
            !result.ConditionalResetAllowed,
            label + " must be fully fail-closed");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
