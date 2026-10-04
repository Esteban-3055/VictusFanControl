using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.Performance;

internal static class GpuClockLimitBackendSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            Require(
                GpuClockOwnershipQualificationSelfTest.Run(output) == 0,
                "GPU clock ownership qualification");

            DefaultWriteGateIsClosed(output);
            AuthorizedSetIsExactlyOneNativeCall(output);
            FailedSetIsNotRetried(output);
            ResetIsExactlyOneNativeCall(output);
            CurrentClockIsObservationNotOwnership(output);
            MissingExportsFailClosed(output);
            DeviceLossIsClassifiedWithoutRetry(output);

            output.WriteLine(
                "GPU NVML locked-clock backend contract self-test: PASS (write gate closed by default, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU NVML locked-clock backend contract self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void DefaultWriteGateIsClosed(
        TextWriter output)
    {
        var transport = new FakeTransport();
        var backend =
            new NvmlGpuClockLimitBackend(
                transport);

        var set =
            backend.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        var reset =
            backend.ResetLockedGraphicsClocks();

        Require(
            !set.Succeeded &&
            set.FailureKind ==
                GpuClockBackendFailureKind.WriteGateClosed,
            "default set gate must be closed");

        Require(
            !reset.Succeeded &&
            reset.FailureKind ==
                GpuClockBackendFailureKind.WriteGateClosed,
            "default reset gate must be closed");

        Require(
            transport.SetCalls == 0 &&
            transport.ResetCalls == 0,
            "closed gate performs zero native write calls");

        Require(
            !backend.Capabilities.HardwareWritesAuthorized,
            "capabilities expose closed production write gate");

        output.WriteLine(
            "PASS GPU NVML backend is hardware-write closed by default");
    }

    private static void AuthorizedSetIsExactlyOneNativeCall(
        TextWriter output)
    {
        var transport = new FakeTransport();
        var backend =
            new NvmlGpuClockLimitBackend(
                transport,
                hardwareWritesAuthorized: true);

        var result =
            backend.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        Require(
            result.Succeeded,
            "authorized set succeeds when NVML accepts");

        Require(
            transport.SetCalls == 1,
            "set maps to exactly one native call");

        Require(
            transport.ReadCalls == 0 &&
            transport.AppTargetReadCalls == 0 &&
            transport.EventReasonReadCalls == 0,
            "backend does not fake exact readback after set");

        Require(
            transport.LastSetMin == 210 &&
            transport.LastSetMax == 1850,
            "exact requested range reaches transport");

        output.WriteLine(
            "PASS accepted GPU clock set performs one native invocation with no hidden retry/readback");
    }

    private static void FailedSetIsNotRetried(
        TextWriter output)
    {
        var transport =
            new FakeTransport
            {
                SetResult =
                    new NvmlControlCallResult(
                        true,
                        3)
            };

        var backend =
            new NvmlGpuClockLimitBackend(
                transport,
                hardwareWritesAuthorized: true);

        var result =
            backend.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1200));

        Require(
            !result.Succeeded &&
            result.FailureKind ==
                GpuClockBackendFailureKind.NotSupported,
            "NVML not-supported classification");

        Require(
            transport.SetCalls == 1,
            "failed set is never retried by backend");

        output.WriteLine(
            "PASS failed NVML set is classified and never retried implicitly");
    }

    private static void ResetIsExactlyOneNativeCall(
        TextWriter output)
    {
        var transport = new FakeTransport();
        var backend =
            new NvmlGpuClockLimitBackend(
                transport,
                hardwareWritesAuthorized: true);

        var result =
            backend.ResetLockedGraphicsClocks();

        Require(
            result.Succeeded &&
            transport.ResetCalls == 1,
            "reset maps to one native call");

        Require(
            transport.ReadCalls == 0 &&
            transport.AppTargetReadCalls == 0 &&
            transport.EventReasonReadCalls == 0,
            "reset does not infer release from diagnostic telemetry");

        output.WriteLine(
            "PASS reset is a single NVML mutation with no hidden follow-up write");
    }

    private static void CurrentClockIsObservationNotOwnership(
        TextWriter output)
    {
        var transport =
            new FakeTransport
            {
                ReadResult =
                    new NvmlUIntCallResult(
                        true,
                        0,
                        1725)
            };

        transport.AppTargetResult =
            new NvmlUIntCallResult(
                true,
                0,
                1850);

        transport.EventReasonsResult =
            new NvmlULongCallResult(
                true,
                0,
                0x2);

        var backend =
            new NvmlGpuClockLimitBackend(
                transport);

        var observation =
            backend.ReadObservation();

        Require(
            observation.Succeeded &&
            observation.CurrentGraphicsClockMHz ==
                1725 &&
            observation.ApplicationGraphicsClockTargetMHz ==
                1850 &&
            observation.CurrentClocksEventReasons ==
                0x2,
            "read-only diagnostic signals are observable");

        Require(
            observation.ExactLockedRange is null &&
            !observation.ProvesExactLockedRangeOwnership &&
            !backend.Capabilities
                .CanProveExactLockedRangeOwnership,
            "diagnostic signals cannot prove exact min/max locked range");

        output.WriteLine(
            "PASS current graphics clock is telemetry only and never promoted to ownership readback");
    }

    private static void MissingExportsFailClosed(
        TextWriter output)
    {
        var transport =
            new FakeTransport
            {
                Availability =
                    new NvmlGpuClockControlAvailability(
                        false,
                        false,
                        false,
                        false,
                        false,
                        false)
            };

        var backend =
            new NvmlGpuClockLimitBackend(
                transport,
                hardwareWritesAuthorized: true);

        var set =
            backend.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        var observation =
            backend.ReadObservation();

        Require(
            !set.Succeeded &&
            set.FailureKind ==
                GpuClockBackendFailureKind.ExportUnavailable,
            "missing setter export is explicit");

        Require(
            !observation.Succeeded &&
            observation.FailureKind ==
                GpuClockBackendFailureKind.ExportUnavailable,
            "missing read export is explicit");

        output.WriteLine(
            "PASS optional NVML clock exports fail closed without breaking the contract");
    }

    private static void DeviceLossIsClassifiedWithoutRetry(
        TextWriter output)
    {
        var transport =
            new FakeTransport
            {
                SetResult =
                    new NvmlControlCallResult(
                        true,
                        15)
            };

        var backend =
            new NvmlGpuClockLimitBackend(
                transport,
                hardwareWritesAuthorized: true);

        var result =
            backend.SetLockedGraphicsClocks(
                new GpuClockLimitRequest(
                    210,
                    1850));

        Require(
            !result.Succeeded &&
            result.FailureKind ==
                GpuClockBackendFailureKind.DeviceUnavailable,
            "GPU_IS_LOST maps to device unavailable");

        Require(
            transport.SetCalls == 1,
            "GPU loss never causes a hidden second write");

        output.WriteLine(
            "PASS GPU loss is surfaced for future driver-reset recovery with one write attempt maximum");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeTransport :
        INvmlGpuClockControlTransport
    {
        internal NvmlGpuClockControlAvailability
            Availability =
                new(
                    true,
                    true,
                    true,
                    true,
                    true,
                    false);

        internal NvmlControlCallResult
            SetResult =
                new(
                    true,
                    0);

        internal NvmlControlCallResult
            ResetResult =
                new(
                    true,
                    0);

        internal NvmlUIntCallResult
            ReadResult =
                new(
                    true,
                    0,
                    210);

        internal NvmlUIntCallResult
            AppTargetResult =
                new(
                    true,
                    3,
                    0);

        internal NvmlULongCallResult
            EventReasonsResult =
                new(
                    true,
                    3,
                    0);

        internal int SetCalls;
        internal int ResetCalls;
        internal int ReadCalls;
        internal int AppTargetReadCalls;
        internal int EventReasonReadCalls;
        internal uint LastSetMin;
        internal uint LastSetMax;

        public NvmlGpuClockControlAvailability
            GpuClockControlAvailability =>
            Availability;

        public NvmlControlCallResult SetGpuLockedClocksOnce(
            uint minGraphicsClockMHz,
            uint maxGraphicsClockMHz)
        {
            SetCalls++;
            LastSetMin =
                minGraphicsClockMHz;
            LastSetMax =
                maxGraphicsClockMHz;

            if (!Availability
                    .SetLockedGraphicsClocksExportAvailable)
            {
                return new NvmlControlCallResult(
                    false,
                    null);
            }

            return SetResult;
        }

        public NvmlControlCallResult ResetGpuLockedClocksOnce()
        {
            ResetCalls++;

            if (!Availability
                    .ResetLockedGraphicsClocksExportAvailable)
            {
                return new NvmlControlCallResult(
                    false,
                    null);
            }

            return ResetResult;
        }

        public NvmlUIntCallResult ReadCurrentGraphicsClockOnce()
        {
            ReadCalls++;

            if (!Availability
                    .CurrentGraphicsClockExportAvailable)
            {
                return new NvmlUIntCallResult(
                    false,
                    null,
                    0);
            }

            return ReadResult;
        }

        public NvmlUIntCallResult ReadApplicationGraphicsClockTargetOnce()
        {
            AppTargetReadCalls++;

            if (!Availability
                    .ApplicationGraphicsClockTargetExportAvailable)
            {
                return new NvmlUIntCallResult(
                    false,
                    null,
                    0);
            }

            return AppTargetResult;
        }

        public NvmlULongCallResult ReadCurrentClocksEventReasonsOnce()
        {
            EventReasonReadCalls++;

            if (!Availability
                    .CurrentClocksEventReasonsExportAvailable)
            {
                return new NvmlULongCallResult(
                    false,
                    null,
                    0);
            }

            return EventReasonsResult;
        }
    }
}
