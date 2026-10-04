using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

public static class Hp8C40FanControlBackendSelfTest
{
    private static readonly Hp8C40FanBackendTiming FastTiming = new(
        SetpointAckTimeout: TimeSpan.FromMilliseconds(100),
        RestoreAckTimeout: TimeSpan.FromMilliseconds(100),
        TachometerAckTimeout: TimeSpan.FromMilliseconds(250),
        PollInterval: TimeSpan.FromMilliseconds(10));

    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        failures += await TestProductionCapabilitiesAsync(output);
        failures += await TestHappyPathAsync(output);
        failures += await TestMaxFanBitDiagnosticsRemainConservativeAsync(output);
        failures += await TestTransientSetpointAckReadFailureRecoversAsync(output);
        failures += await TestRepeatedSetpointAckReadFailureFailsClosedAsync(output);
        failures += TestSetpointSnapshotStabilizer(output);
        failures += await TestSameSetpointSkipsRedundantWmiWriteAsync(output);
        failures += await TestExistingOverrideRefusedAsync(output);
        failures += await TestCancelledAdmissionIsNoWriteAsync(output);
        failures += await TestAdmissionFailurePreservesCauseAsync(output);
        failures += await TestFirstCommandExternalOverrideIsNoWriteAsync(output);
        failures += await TestFirstCommandSpeedReadFailurePreservesCauseAsync(output);
        failures += await TestUnsupportedTargetRefusedAsync(output);
        failures += await TestRangeRefusedAsync(output);
        failures += await TestRestoreVerificationAsync(output);
        failures += await TestRestoreTimeoutExcludesSuspendedWallTimeAsync(output);
        failures += await TestCpuTachFailureAsync(output);
        failures += await TestGpuTachFailureAsync(output);
        failures += await TestOwnershipLossAsync(output);
        failures += await TestStatusDetectsOwnershipLossAsync(output);
        failures += await TestStatusDetectsRuntimeTachFailureAsync(output);
        failures += await TestStatusToleratesSingleGuardTransientAsync(output);
        failures += await TestStatusRejectsRepeatedGuardConflictAsync(output);
        failures += await TestStoppedFansCanSpinUpWithinAckWindowAsync(output);
        failures += await TestTransientEcReadDuringTachAckRecoversAsync(output);
        failures += await TestRepeatedEcReadDuringTachAckFailsClosedAsync(output);
        failures += await TestOneSampleDirectionalSpikeIsRejectedAsync(output);
        failures += await TestCancellationAtPreDispatchPreventsWriteAsync(output);
        failures += await TestWatchdogPrepareFailureIsNoWriteAsync(output);
        failures += await TestWatchdogWriteIntentOrderingAsync(output);
        failures += await TestQualificationHookRunsAfterHardwareAckBeforeCommitAsync(output);
        failures += await TestWatchdogPostIntentExternalRaceAsync(output);
        failures += await TestWatchdogHeartbeatCouplingAsync(output);
        failures += await TestWatchdogProbePreemptsEcReadAsync(output);
        failures += await TestWatchdogLossWinsConcurrentEcFailureAsync(output);
        failures += await TestWatchdogCommitFailureRestoresAsync(output);
        failures += await TestWatchdogRestoreIpcFailureDoesNotBlockLocalRestoreAsync(output);
        failures += await TestWatchdogCancellationAfterIntentAbortsAsync(output);
        failures += await TestWmiControlProofAsync(output);
        failures += await TestWindowCannotReplaceCommandProofAsync(output);
        failures += await TestHungWmiProofRestoresThroughCoordinatorAsync(output);
        failures += await TestExpiredAdmissionBaselineIsNoWriteAsync(output);
        failures += await TestAcknowledgementTelemetryContinuationAsync(output);
        failures += await TestSlowAcknowledgementKeepsRefreshingAsync(output);
        failures += await TestAcknowledgementTelemetryFailureRestoresAsync(output);
        failures += await TestAcknowledgementTelemetryCannotExtendDeadlineAsync(output);
        failures += await TestAutomaticRefreshPreservesAdmissionAsync(output);

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "Hp8C40FanControlBackend self-test: PASS"
            : $"Hp8C40FanControlBackend self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 12;
    }

    private static async Task<int> TestMaxFanBitDiagnosticsRemainConservativeAsync(TextWriter output)
    {
        var failures = 0;
        foreach (var (raw, bitSet) in new (byte, bool)[] { (0x00, false), (0x02, false), (0x04, true), (0x90, false), (0x94, true) })
        {
            var state = FakeHardware.AutoState with { MaxFan = raw };
            failures += Report(output, $"8C40 FFFS bit decoding raw 0x{raw:X2}", state.DecodedMaxFanBitSet == bitSet);
            if (raw == 0) continue;
            var hardware = new FakeHardware { State = state };
            await using var backend = NewBackend(hardware);
            string? refusal = null;
            try { await backend.EnterCustomModeAsync(CancellationToken.None); }
            catch (FanControlOwnershipConflictException ex) { refusal = ex.Message; }
            failures += Report(output, $"unqualified raw 0x{raw:X2} still prevents Manual admission",
                refusal is not null && hardware.SetCalls == 0 &&
                refusal.Contains(bitSet ? "FFFS is set" : "FFFS bit is clear", StringComparison.Ordinal));
        }

        var runtime = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        await using (var backend = NewProtectedBackend(runtime, lease))
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
            await backend.ApplyAsync(new FanCommand(30, 30, "maxfan-bit-runtime"), CancellationToken.None);
            runtime.GuardReadOverrides.Enqueue((0x90, 0));
            runtime.GuardReadOverrides.Enqueue((0x90, 0));
            var status = await backend.GetStatusAsync(CancellationToken.None);
            failures += Report(output, "raw 90h is not decoded Max Fan but still blocks heartbeat",
                !status.FeedbackHealthy && status.Detail.Contains("decodedMaxFanBit=0", StringComparison.Ordinal) &&
                lease.Calls.Count(call => call == "heartbeat") == 0);
            await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        }

        var ack = new FakeHardware();
        var ackLease = new FakeWatchdogLeaseClient();
        ack.OnSetFanLevel = () =>
        {
            ack.GuardReadOverrides.Enqueue((0x00, 0)); // Setpoint read.
            ack.GuardReadOverrides.Enqueue((0x90, 0)); // Tachometer ACK state.
        };
        await using (var backend = NewProtectedBackend(ack, ackLease))
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
            string? failure = null;
            try { await backend.ApplyAsync(new FanCommand(30, 30, "maxfan-bit-ack"), CancellationToken.None); }
            catch (InvalidOperationException ex) { failure = ex.Message; }
            // As in production, recovery is a separate operation after Apply fails.
            await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
            failures += Report(output, "raw 90h during ACK preserves abort and independent restore without false Max Fan claim",
                failure?.Contains("FFFS bit is clear", StringComparison.Ordinal) == true &&
                ack.SetCalls == 1 && ack.RestoreCalls >= 1 && ack.State.CpuSetpoint == 255 &&
                ack.State.GpuSetpoint == 255 && !ackLease.Calls.Contains("commit"));
        }
        return failures;
    }

    private static async Task<int> TestProductionCapabilitiesAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        var capabilities = backend.Capabilities;

        return Report(
            output,
            "production capabilities advertise equal-only 10..50",
            capabilities.BoardProduct == Hp8C40TargetProfile.BoardProduct &&
            capabilities.MinimumLevel == 10 &&
            capabilities.MaximumLevel == 50 &&
            !capabilities.SupportsIndependentLevels);
    }

    private static async Task<int> TestHappyPathAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(50, 50, "backend-self-test-upper-bound"),
            CancellationToken.None);

        var active = await backend.GetStatusAsync(CancellationToken.None);

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        var restored = await backend.GetStatusAsync(CancellationToken.None);

        return Report(
            output,
            "real backend boundary: enter -> EC+tachs ack -> verified restore",
            hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            active.CustomModeActive &&
            active.Detail.Contains("both tachometers", StringComparison.OrdinalIgnoreCase) &&
            !restored.CustomModeActive);
    }

    private static async Task<int> TestTransientSetpointAckReadFailureRecoversAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware
        {
            TransientSetpointAckReadFailures = 1
        };

        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "transient-setpoint-ack-contention"),
            CancellationToken.None);

        var status = await backend.GetStatusAsync(CancellationToken.None);
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "single transient EC mutex failure during setpoint acknowledgement is retried",
            hardware.SetCalls == 1 &&
            hardware.TransientSetpointAckReadFailures == 0 &&
            status.CustomModeActive &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestRepeatedSetpointAckReadFailureFailsClosedAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware
        {
            TransientSetpointAckReadFailures = 3
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failedClosed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "repeated-setpoint-ack-contention"),
                CancellationToken.None);
        }
        catch (IOException ex)
            when (ex.Message.Contains(
                "Setpoint acknowledgement lost EC observability",
                StringComparison.Ordinal))
        {
            failedClosed = true;
        }

        hardware.TransientSetpointAckReadFailures = 0;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "repeated EC mutex failures during setpoint acknowledgement remain fail-closed",
            failedClosed &&
            hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestSameSetpointSkipsRedundantWmiWriteAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);

        await backend.ApplyAsync(
            new FanCommand(30, 30, "initial-30"),
            CancellationToken.None);

        var writesAfterFirstCommand = hardware.SetCalls;

        await backend.ApplyAsync(
            new FanCommand(30, 30, "repeat-same-30"),
            CancellationToken.None);

        var writesAfterRepeatedCommand = hardware.SetCalls;

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "same owned setpoint is verified without redundant WMI SetFanLevel write",
            writesAfterFirstCommand == 1 &&
            writesAfterRepeatedCommand == 1 &&
            hardware.RestoreCalls == 1);
    }

    private static async Task<int> TestExistingOverrideRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            State = FakeHardware.AutoState with
            {
                CpuSetpoint = 30,
                GpuSetpoint = 30
            }
        };

        await using var backend = NewBackend(hardware);

        var refused = false;
        try
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
        }
        catch (FanControlOwnershipConflictException)
        {
            refused = true;
        }

        return Report(
            output,
            "existing fixed override blocks authority acquisition",
            refused && hardware.SetCalls == 0);
    }


    private static async Task<int> TestCancelledAdmissionIsNoWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var refusedAsNoWrite = false;
        try
        {
            await backend.EnterCustomModeAsync(cts.Token);
        }
        catch (FanControlAdmissionException ex)
        {
            refusedAsNoWrite = ex.InnerException is OperationCanceledException;
        }

        return Report(
            output,
            "cancelled authority admission is classified as no-write",
            refusedAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestAdmissionFailurePreservesCauseAsync(
        TextWriter output)
    {
        var expected =
            new IOException(
                "synthetic EC admission contention");

        var hardware = new FakeHardware
        {
            ReadEcStateException = expected
        };

        await using var backend = NewBackend(hardware);

        FanControlAdmissionException? observed = null;
        try
        {
            await backend.EnterCustomModeAsync(
                CancellationToken.None);
        }
        catch (FanControlAdmissionException ex)
        {
            observed = ex;
        }

        return Report(
            output,
            "no-write admission failure preserves the exact inner cause",
            observed is not null &&
            ReferenceEquals(observed.InnerException, expected) &&
            observed.Message.Contains(
                nameof(IOException),
                StringComparison.Ordinal) &&
            observed.Message.Contains(
                expected.Message,
                StringComparison.Ordinal) &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0);
    }

    private static async Task<int> TestFirstCommandExternalOverrideIsNoWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var refusedAsNoWrite = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "first-command-race"),
                CancellationToken.None);
        }
        catch (FanControlAdmissionException ex)
        {
            refusedAsNoWrite = ex.InnerException is InvalidOperationException;
        }

        var status = await backend.GetStatusAsync(CancellationToken.None);

        return Report(
            output,
            "external override before first write is preserved as no-write",
            refusedAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == 31 &&
            hardware.State.GpuSetpoint == 31 &&
            !status.CustomModeActive);
    }

    private static async Task<int> TestFirstCommandSpeedReadFailurePreservesCauseAsync(
        TextWriter output)
    {
        var expected =
            new HpBiosCallException(
                "synthetic GetFanLevel telemetry failure");

        var hardware = new FakeHardware
        {
            GetCurrentFanLevelsException = expected
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        FanControlAdmissionException? observed = null;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(10, 10, "first-command-speed-read-failure"),
                CancellationToken.None);
        }
        catch (FanControlAdmissionException ex)
        {
            observed = ex;
        }

        return Report(
            output,
            "first-command no-write failure reports exact read-only admission stage and cause",
            observed is not null &&
            ReferenceEquals(observed.InnerException, expected) &&
            observed.Message.Contains(
                "read BIOS current-speed telemetry",
                StringComparison.Ordinal) &&
            observed.Message.Contains(
                nameof(HpBiosCallException),
                StringComparison.Ordinal) &&
            observed.Message.Contains(
                expected.Message,
                StringComparison.Ordinal) &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestUnsupportedTargetRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = new Hp8C40FanControlBackend(
            hardware,
            targetSupported: false,
            supportDetail: "synthetic mismatch",
            timing: FastTiming);

        var refused = false;
        try
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        return Report(
            output,
            "unsupported target remains fail-closed",
            refused && !backend.CanWrite && hardware.SetCalls == 0);
    }

    private static async Task<int> TestRangeRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var belowRefused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(9, 9, "below-qualified-range"),
                CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException)
        {
            belowRefused = true;
        }

        var aboveRefused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(51, 51, "above-qualified-range"),
                CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException)
        {
            aboveRefused = true;
        }

        var asymmetricRefused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(10, 11, "unqualified-asymmetric-command"),
                CancellationToken.None);
        }
        catch (ArgumentException)
        {
            asymmetricRefused = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend independently enforces equal-only validated 10-50 range",
            belowRefused &&
            aboveRefused &&
            asymmetricRefused &&
            hardware.SetCalls == 0);
    }

    private static async Task<int> TestRestoreVerificationAsync(TextWriter output)
    {
        var hardware = new FakeHardware { IgnoreRestore = true };
        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "restore-timeout"),
            CancellationToken.None);

        var failed = false;
        try
        {
            await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        // Allow DisposeAsync to finish without repeating the synthetic failure.
        hardware.IgnoreRestore = false;

        return Report(
            output,
            "restore is not accepted until EC returns to FF/FF",
            failed && hardware.State.CpuSetpoint == 30 && hardware.State.GpuSetpoint == 30);
    }

    private static async Task<int> TestRestoreTimeoutExcludesSuspendedWallTimeAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware
        {
            IgnoreRestore = true,
            State = FakeHardware.AutoState with
            {
                CpuSetpoint = 30,
                GpuSetpoint = 30
            }
        };
        var clock = new FrozenActiveTimeClock();

        hardware.OnEcRead = readCount =>
        {
            // FastTiming.RestoreAckTimeout is 100 ms and PollInterval is 10 ms.
            // Delay the synthetic FF/FF acknowledgement beyond 100 ms of real
            // wall time while the active-time clock remains frozen, exactly as
            // it would across S3. A Stopwatch/QPC timeout would fail this case.
            if (readCount >= 15)
            {
                hardware.State = hardware.State with
                {
                    CpuSetpoint = byte.MaxValue,
                    GpuSetpoint = byte.MaxValue
                };
            }
        };

        await using var backend =
            NewBackend(
                hardware,
                activeTimeClock: clock);

        await backend.RestoreFirmwareAutoAsync(
            CancellationToken.None);

        var status =
            await backend.GetStatusAsync(
                CancellationToken.None);

        return Report(
            output,
            "restore acknowledgement timeout excludes suspended wall time",
            hardware.RestoreCalls == 1 &&
            hardware.EcReadCalls >= 15 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            !status.CustomModeActive);
    }

    private static async Task<int> TestCpuTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware { FreezeCpuTach = true };
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "cpu-tach-failure"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "CPU tachometer non-response rejects command",
            failed);
    }

    private static async Task<int> TestGpuTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware { FreezeGpuTach = true };
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "gpu-tach-failure"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "GPU tachometer non-response rejects command",
            failed);
    }

    private static async Task<int> TestOwnershipLossAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        await backend.ApplyAsync(
            new FanCommand(30, 30, "establish-ownership"),
            CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var refused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "external-overwrite"),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        // Return synthetic state to the owned value so the explicit restore can
        // exercise the normal path rather than hiding the assertion in Dispose.
        hardware.State = hardware.State with
        {
            CpuSetpoint = 30,
            GpuSetpoint = 30
        };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "external setpoint overwrite is detected instead of fought",
            refused && hardware.SetCalls == 1);
    }


    private static async Task<int> TestStatusDetectsOwnershipLossAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "status-ownership"),
            CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var status = await backend.GetStatusAsync(CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 30,
            GpuSetpoint = 30
        };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend status exposes active ownership mismatch",
            status.CustomModeActive &&
            !status.OwnershipValid &&
            status.Detail.Contains("OWNERSHIP-MISMATCH", StringComparison.Ordinal));
    }



    private static async Task<int> TestStatusDetectsRuntimeTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "runtime-tach-health"),
            CancellationToken.None);

        hardware.FreezeCpuTach = true;
        hardware.State = hardware.State with { CpuRpm = 0 };

        var status = await backend.GetStatusAsync(CancellationToken.None);

        hardware.FreezeCpuTach = false;
        hardware.State = hardware.State with { CpuRpm = 3000 };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend status exposes post-ack tachometer failure",
            status.CustomModeActive &&
            status.OwnershipValid &&
            !status.FeedbackHealthy &&
            status.Detail.Contains("FEEDBACK/CONTROL-STATE-INVALID", StringComparison.Ordinal));
    }

    private static async Task<int> TestStatusToleratesSingleGuardTransientAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "single-guard-transient"),
            CancellationToken.None);

        var readsBefore = hardware.EcReadCalls;
        hardware.GuardReadOverrides.Enqueue((0x00, 0x90));
        hardware.GuardReadOverrides.Enqueue((0x00, 0x00));

        var status = await backend.GetStatusAsync(CancellationToken.None);
        var readsAfter = hardware.EcReadCalls;
        var heartbeatCount =
            lease.Calls.Count(call => call == "heartbeat");

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "single unexpected guard sample is read-only confirmed before runtime handoff",
            status.CustomModeActive &&
            status.OwnershipValid &&
            status.FeedbackHealthy &&
            status.Detail.Contains(
                "recovered to 00/00 on bounded read-only confirmation",
                StringComparison.Ordinal) &&
            readsAfter == readsBefore + 2 &&
            heartbeatCount == 1);
    }

    private static async Task<int> TestStatusRejectsRepeatedGuardConflictAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "persistent-guard-conflict"),
            CancellationToken.None);

        var readsBefore = hardware.EcReadCalls;
        hardware.GuardReadOverrides.Enqueue((0x00, 0x90));
        hardware.GuardReadOverrides.Enqueue((0x00, 0x90));

        var status = await backend.GetStatusAsync(CancellationToken.None);
        var readsAfter = hardware.EcReadCalls;
        var heartbeatCount =
            lease.Calls.Count(call => call == "heartbeat");

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "repeated unexpected guard state remains fail-closed",
            status.CustomModeActive &&
            status.OwnershipValid &&
            !status.FeedbackHealthy &&
            status.Detail.Contains(
                "switch=0x90",
                StringComparison.Ordinal) &&
            readsAfter == readsBefore + 2 &&
            heartbeatCount == 0);
    }
    private static async Task<int> TestTransientEcReadDuringTachAckRecoversAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware
        {
            TransientEcReadFailuresDuringTachAck = 1
        };
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);

        var passed = true;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "transient-ec-during-tach-ack"),
                CancellationToken.None);
        }
        catch
        {
            passed = false;
        }

        var commitCalls =
            lease.Calls.Count(call => call == "commit");

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "one exhausted EC tach snapshot is bounded-retried before watchdog Commit",
            passed &&
            hardware.SetCalls == 1 &&
            hardware.TransientEcReadFailuresDuringTachAck == 0 &&
            commitCalls == 1);
    }

    private static async Task<int> TestRepeatedEcReadDuringTachAckFailsClosedAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware
        {
            TransientEcReadFailuresDuringTachAck = 3
        };
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failedClosed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "persistent-ec-during-tach-ack"),
                CancellationToken.None);
        }
        catch (IOException ex)
            when (ex.Message.Contains(
                "lost control-state observability after 3 failed control-state snapshots",
                StringComparison.Ordinal))
        {
            failedClosed = true;
        }

        var commitCalls =
            lease.Calls.Count(call => call == "commit");

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "three exhausted EC tach snapshots remain fail-closed before watchdog Commit",
            failedClosed &&
            hardware.SetCalls == 1 &&
            commitCalls == 0);
    }

    private static async Task<int> TestOneSampleDirectionalSpikeIsRejectedAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            PulseCpuTachOnceThenReturnBaseline = true
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var rejected = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "single-sample-spike"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            rejected = true;
        }

        hardware.PulseCpuTachOnceThenReturnBaseline = false;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "one-sample directional RPM spike cannot satisfy dual-tach acknowledgement",
            rejected);
    }

    private static async Task<int> TestStoppedFansCanSpinUpWithinAckWindowAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            State = FakeHardware.AutoState with
            {
                CpuRpm = 0,
                GpuRpm = 0
            }
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var passed = true;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "spin-up-from-zero"),
                CancellationToken.None);
        }
        catch
        {
            passed = false;
        }

        var state = hardware.State;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "zero RPM is treated as bounded spin-up transient, not immediate tach failure",
            passed &&
            state.CpuRpm > 0 &&
            state.GpuRpm > 0);
    }


    private static async Task<int> TestCancellationAtPreDispatchPreventsWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();

        // EnterCustomMode performs EC read #1. Apply performs its baseline read
        // (#2), then the final pre-dispatch ownership read (#3). Cancel exactly
        // on #3 to verify that no WMI fan-level write can escape the boundary.
        hardware.OnEcRead = count =>
        {
            if (count == 3)
            {
                cts.Cancel();
            }
        };

        var cancelledAsNoWrite = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "cancel-before-wmi"),
                cts.Token);
        }
        catch (FanControlAdmissionException ex)
        {
            cancelledAsNoWrite = ex.InnerException is OperationCanceledException;
        }

        hardware.OnEcRead = null;

        return Report(
            output,
            "cancellation at pre-dispatch boundary prevents WMI fan write",
            cancelledAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestWatchdogPrepareFailureIsNoWriteAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient { FailPrepare = true };
        await using var backend = NewProtectedBackend(hardware, lease);

        var refused = false;
        try
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
        }
        catch (FanControlAdmissionException)
        {
            refused = true;
        }

        return Report(
            output,
            "watchdog Prepare failure blocks Custom before any hardware write",
            refused &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            lease.Calls.SequenceEqual(["prepare"]));
    }

    private static async Task<int> TestWatchdogWriteIntentOrderingAsync(
        TextWriter output)
    {
        var sequence = new List<string>();
        var hardware = new FakeHardware
        {
            OnSetFanLevel = () => sequence.Add("set")
        };
        var lease = new FakeWatchdogLeaseClient
        {
            OnCall = call => sequence.Add(call)
        };

        await using var backend = NewProtectedBackend(hardware, lease);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "watchdog-ordering"),
            CancellationToken.None);

        var intentIndex = sequence.IndexOf("intent");
        var setIndex = sequence.IndexOf("set");
        var commitIndex = sequence.IndexOf("commit");

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "watchdog WriteIntent precedes WMI and Commit follows hardware acknowledgement",
            intentIndex >= 0 &&
            setIndex > intentIndex &&
            commitIndex > setIndex &&
            hardware.SetCalls == 1 &&
            lease.Calls.Contains("prepare") &&
            lease.Calls.Contains("intent") &&
            lease.Calls.Contains("commit"));
    }

    private static async Task<int> TestQualificationHookRunsAfterHardwareAckBeforeCommitAsync(
        TextWriter output)
    {
        var sequence = new List<string>();
        var hardware = new FakeHardware
        {
            OnSetFanLevel = () => sequence.Add("set")
        };
        var lease = new FakeWatchdogLeaseClient
        {
            OnCall = call => sequence.Add(call)
        };
        var hook =
            new RecordingQualificationHook(
                () => sequence.Add("hook"));

        await using var backend =
            NewProtectedBackend(
                hardware,
                lease,
                hook);

        await backend.EnterCustomModeAsync(
            CancellationToken.None);

        await backend.ApplyAsync(
            new FanCommand(
                30,
                30,
                "qualification-hook-ordering"),
            CancellationToken.None);

        var intentIndex = sequence.IndexOf("intent");
        var setIndex = sequence.IndexOf("set");
        var hookIndex = sequence.IndexOf("hook");
        var commitIndex = sequence.IndexOf("commit");

        await backend.RestoreFirmwareAutoAsync(
            CancellationToken.None);

        var lastTachAck = hook.LastTachAck;

        return Report(
            output,
            "qualification hook runs after real hardware acknowledgement and before watchdog Commit",
            intentIndex >= 0 &&
            setIndex > intentIndex &&
            hookIndex > setIndex &&
            commitIndex > hookIndex &&
            hook.Calls == 1 &&
            hook.LastCpuTarget == 30 &&
            hook.LastGpuTarget == 30 &&
            hook.LastSetpointAck.CpuSetpoint == 30 &&
            hook.LastSetpointAck.GpuSetpoint == 30 &&
            lastTachAck is not null &&
            lastTachAck.CpuSetpoint == 30 &&
            lastTachAck.GpuSetpoint == 30 &&
            lastTachAck.CpuRpm > 0 &&
            lastTachAck.GpuRpm > 0);
    }

    private static async Task<int> TestWatchdogPostIntentExternalRaceAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient
        {
            FailAbort = true
        };

        lease.OnWriteIntent = (_, _) =>
        {
            hardware.State = hardware.State with
            {
                CpuSetpoint = 31,
                GpuSetpoint = 31
            };
        };

        await using var backend = NewProtectedBackend(hardware, lease);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var refused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "watchdog-post-intent-race"),
                CancellationToken.None);
        }
        catch (FanControlAdmissionException)
        {
            refused = true;
        }

        return Report(
            output,
            "post-WriteIntent ownership recheck preserves an external override",
            refused &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == 31 &&
            hardware.State.GpuSetpoint == 31 &&
            lease.Calls.Contains("intent") &&
            lease.Calls.Contains("abort"));
    }

    private static async Task<int> TestWatchdogHeartbeatCouplingAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "watchdog-heartbeat"),
            CancellationToken.None);

        var healthy = await backend.GetStatusAsync(CancellationToken.None);
        var probesAfterHealthy = lease.Calls.Count(call => call == "probe");
        var afterHealthy = lease.Calls.Count(call => call == "heartbeat");

        hardware.FreezeCpuTach = true;
        hardware.State = hardware.State with { CpuRpm = 0 };

        var unhealthy = await backend.GetStatusAsync(CancellationToken.None);
        var probesAfterUnhealthy = lease.Calls.Count(call => call == "probe");
        var afterUnhealthy = lease.Calls.Count(call => call == "heartbeat");

        hardware.FreezeCpuTach = false;
        hardware.State = hardware.State with { CpuRpm = 3000 };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "watchdog heartbeat is emitted only after healthy ownership/feedback validation",
            healthy.OwnershipValid &&
            healthy.FeedbackHealthy &&
            !unhealthy.FeedbackHealthy &&
            probesAfterHealthy == 1 &&
            probesAfterUnhealthy == 2 &&
            afterHealthy == 1 &&
            afterUnhealthy == 1);
    }

    private static async Task<int> TestWatchdogProbePreemptsEcReadAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "watchdog-probe-preempts-ec"),
            CancellationToken.None);

        var ecReadsBeforeStatus = hardware.EcReadCalls;
        var heartbeatBeforeStatus =
            lease.Calls.Count(call => call == "heartbeat");

        lease.FailProbe = true;

        var failedAtProbe = false;
        try
        {
            await backend.GetStatusAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains(
                "synthetic Probe failure",
                StringComparison.Ordinal))
        {
            failedAtProbe = true;
        }

        var ecReadsAfterStatus = hardware.EcReadCalls;
        var heartbeatAfterStatus =
            lease.Calls.Count(call => call == "heartbeat");

        lease.FailProbe = false;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "watchdog probe failure is detected before any EC health read",
            failedAtProbe &&
            ecReadsAfterStatus == ecReadsBeforeStatus &&
            heartbeatAfterStatus == heartbeatBeforeStatus &&
            lease.Calls.Contains("probe"));
    }

    private static async Task<int> TestWatchdogLossWinsConcurrentEcFailureAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient
        {
            FailProbeAfterSuccessfulCalls = 1
        };

        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "watchdog-vs-ec-race"),
            CancellationToken.None);

        var ecReadsBeforeStatus = hardware.EcReadCalls;
        hardware.ReadEcStateException =
            new IOException("synthetic EC OBF failure");

        var watchdogFailureWon = false;
        try
        {
            await backend.GetStatusAsync(CancellationToken.None);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.Contains(
                "synthetic Probe failure",
                StringComparison.Ordinal))
        {
            watchdogFailureWon = true;
        }

        var ecReadsAfterStatus = hardware.EcReadCalls;
        var probeCalls =
            lease.Calls.Count(call => call == "probe");
        var heartbeatCalls =
            lease.Calls.Count(call => call == "heartbeat");

        hardware.ReadEcStateException = null;
        lease.FailProbeAfterSuccessfulCalls = null;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "watchdog loss wins causality if EC fails after the first liveness probe",
            watchdogFailureWon &&
            ecReadsAfterStatus == ecReadsBeforeStatus + 1 &&
            probeCalls == 2 &&
            heartbeatCalls == 0);
    }

    private static async Task<int> TestWatchdogCommitFailureRestoresAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient { FailCommit = true };
        await using var backend = NewProtectedBackend(hardware, lease);

        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "watchdog-commit-failure"),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "post-write watchdog Commit failure leaves command for fail-safe restore",
            failed &&
            hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            lease.Calls.Contains("restore-begin") &&
            lease.Calls.Contains("release"));
    }

    private static async Task<int> TestWatchdogRestoreIpcFailureDoesNotBlockLocalRestoreAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient
        {
            FailRestoreBegin = true,
            FailRelease = true
        };

        await using var backend = NewProtectedBackend(hardware, lease);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "watchdog-restore-ipc-failure"),
            CancellationToken.None);

        var localRestoreSucceeded = true;
        try
        {
            await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        }
        catch
        {
            localRestoreSucceeded = false;
        }

        return Report(
            output,
            "watchdog IPC loss never blocks the live controller's local HP restore",
            localRestoreSucceeded &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            lease.Calls.Contains("restore-begin") &&
            lease.Calls.Contains("release"));
    }

    private static async Task<int> TestWatchdogCancellationAfterIntentAbortsAsync(
        TextWriter output)
    {
        var hardware = new FakeHardware();
        var lease = new FakeWatchdogLeaseClient();
        using var cts = new CancellationTokenSource();

        lease.OnWriteIntent = (_, _) => cts.Cancel();

        await using var backend = NewProtectedBackend(hardware, lease);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var refusedAsNoWrite = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "cancel-after-intent"),
                cts.Token);
        }
        catch (FanControlAdmissionException ex)
        {
            refusedAsNoWrite =
                ex.InnerException is OperationCanceledException ||
                ex.InnerException is AggregateException;
        }

        return Report(
            output,
            "cancellation after durable WriteIntent rolls back lease before any WMI write",
            refusedAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            lease.Calls.Contains("intent") &&
            lease.Calls.Contains("abort") &&
            lease.Calls.Contains("cancel-prepared"));
    }

    private static int TestSetpointSnapshotStabilizer(TextWriter output)
    {
        var failures = 0;

        var observedAttempt3 = new Queue<FanSetpointSnapshotStabilizer.Snapshot>(
            new[]
            {
                new FanSetpointSnapshotStabilizer.Snapshot(144, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(30, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(164, 17),
                new FanSetpointSnapshotStabilizer.Snapshot(30, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(30, 30)
            });
        var observedCalls = 0;
        var recovered = FanSetpointSnapshotStabilizer.ReadStable(
            () =>
            {
                observedCalls++;
                return observedAttempt3.Dequeue();
            });

        failures += Report(
            output,
            "setpoint coherence filters attempt-3 one-off 144/30 and 164/17 samples",
            recovered.CpuSetpoint == 30 &&
            recovered.GpuSetpoint == 30 &&
            observedCalls == 5);

        var stableExternalOverwrite =
            new Queue<FanSetpointSnapshotStabilizer.Snapshot>(
                new[]
                {
                    new FanSetpointSnapshotStabilizer.Snapshot(30, 30),
                    new FanSetpointSnapshotStabilizer.Snapshot(40, 30),
                    new FanSetpointSnapshotStabilizer.Snapshot(40, 30)
                });
        var overwrite = FanSetpointSnapshotStabilizer.ReadStable(
            () => stableExternalOverwrite.Dequeue());

        failures += Report(
            output,
            "setpoint coherence preserves a stable asymmetric/external overwrite",
            overwrite.CpuSetpoint == 40 &&
            overwrite.GpuSetpoint == 30);

        var unstable = new Queue<FanSetpointSnapshotStabilizer.Snapshot>(
            new[]
            {
                new FanSetpointSnapshotStabilizer.Snapshot(30, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(31, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(30, 31),
                new FanSetpointSnapshotStabilizer.Snapshot(32, 30),
                new FanSetpointSnapshotStabilizer.Snapshot(30, 32),
                new FanSetpointSnapshotStabilizer.Snapshot(33, 30)
            });
        var unstableRejected = false;
        try
        {
            _ = FanSetpointSnapshotStabilizer.ReadStable(
                () => unstable.Dequeue());
        }
        catch (InvalidDataException ex)
            when (ex.Message.Contains(
                "did not stabilize",
                StringComparison.Ordinal))
        {
            unstableRejected = true;
        }

        failures += Report(
            output,
            "setpoint coherence fails closed when no pair stabilizes within six snapshots",
            unstableRejected &&
            unstable.Count == 0);

        return failures;
    }

    private static async Task<int> TestWmiControlProofAsync(TextWriter output)
    {
        var failures = 0;
        foreach (var variant in new[] { "fresh", "duplicate", "pre-command", "timeout", "coarse increase 200", "coarse increase 300", "coarse decrease 200", "coarse decrease 300" })
        {
            var hardware = new FakeHardware { UseWmiTachometerBins = true };
            hardware.RepeatWmiQueryIdentityAfterWrite = variant == "duplicate";
            hardware.PreCommandWmiQueryAfterWrite = variant == "pre-command";
            hardware.WmiProofFailureAfterWrite = variant == "timeout";
            var target = 30;
            if (variant.StartsWith("coarse increase", StringComparison.Ordinal))
            {
                hardware.FreezeCpuTach = true;
                hardware.OnSetFanLevel = () => hardware.State = hardware.State with
                    { CpuRpm = (ushort)(FakeHardware.AutoState.CpuRpm + (variant.EndsWith("300", StringComparison.Ordinal) ? 300 : 200)) };
            }
            if (variant.StartsWith("coarse decrease", StringComparison.Ordinal))
            {
                target = 20; hardware.FreezeCpuTach = true;
                hardware.State = hardware.State with { CpuRpm = 3200, GpuRpm = 3400 };
                hardware.OnSetFanLevel = () => hardware.State = hardware.State with
                    { CpuRpm = (ushort)(variant.EndsWith("300", StringComparison.Ordinal) ? 2900 : 3000) };
            }
            await using var backend = NewBackend(hardware);
            var proofs = new List<string>();
            backend.WmiCommandAcknowledged += (_, proof) => proofs.Add(proof);
            backend.WmiCommandAcknowledged += (_, _) => throw new IOException("Synthetic diagnostic sink failure.");
            await backend.EnterCustomModeAsync(CancellationToken.None);
            var completed = false;
            try { await backend.ApplyAsync(new FanCommand(target, target, "WMI proof self-test"), CancellationToken.None); completed = true; }
            catch (TimeoutException)
            {
                // The coordinator owns failure restoration; exercise that
                // separate backend release path without requiring RPM proof.
                await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
            }
            var expectedPass = variant is "fresh" or "coarse increase 300" or "coarse decrease 300";
            failures += Report(output, $"WMI backend proof {variant}: {(expectedPass ? "acknowledges" : "restores without accepting")}",
                completed == expectedPass && proofs.Count == (expectedPass ? 1 : 0) && hardware.SetCalls == 1 &&
                (expectedPass || (hardware.RestoreCalls == 1 && hardware.State.CpuSetpoint == 255)));
        }
        return failures;
    }

    private static async Task<int> TestWindowCannotReplaceCommandProofAsync(TextWriter output)
    {
        var failures = 0;
        foreach (var variant in new[] { "favorable history native failure", "one new sample then native failure", "two raw confirmations with lagging median" })
        {
            using var slot = new SemaphoreSlim(1, 1);
            var hardware = new FakeHardware { UseWmiTachometerBins = true };
            var seed = false;
            var postCommandQueries = 0;
            var allQueries = 0;
            var expectedPass = variant == "two raw confirmations with lagging median";
            HpBiosResponse Send(HpBiosRequest _)
            {
                Interlocked.Increment(ref allQueries);
                if (hardware.SetCalls == 0)
                    return seed && !expectedPass ? new(0, [40, 40]) : new(0, [22, 24]);
                var index = Interlocked.Increment(ref postCommandQueries);
                if (!expectedPass && (variant == "favorable history native failure" || index > 1))
                    throw new TimeoutException("synthetic native WMI failure despite populated history");
                return new(0, [40, 40]);
            }
            using var telemetry = new HpWmiFanTelemetryReader(Send,
                () => Environment.TickCount64, () => DateTimeOffset.UtcNow, slot);
            var reader = new HpWmiFanProofReader(Send, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(2));
            hardware.AsyncControlRead = async token =>
            {
                var sample = await reader.ReadFreshAsync(token);
                var state = hardware.ReadEcState();
                return state with
                {
                    CpuRpm = checked((ushort)sample.Speeds.CpuNominalRpm),
                    GpuRpm = checked((ushort)sample.Speeds.GpuNominalRpm),
                    TachometerResolutionRpm = 100,
                    FanQuerySequence = sample.Sequence,
                    FanQueryStartedAtMilliseconds = sample.Speeds.StartedAtMilliseconds
                };
            };
            await using var backend = NewBackend(hardware);
            var proofs = new List<string>();
            backend.WmiCommandAcknowledged += (_, proof) => proofs.Add(proof);
            await backend.EnterCustomModeAsync(CancellationToken.None);
            seed = true;
            for (var i = 0; i < 5; i++) await reader.ReadFreshAsync(CancellationToken.None);
            seed = false;
            var preWriteWindowCorrect = false;
            hardware.OnSetFanLevel = () =>
            {
                var view = telemetry.ReadWindowCached();
                preWriteWindowCorrect = view is { WindowCount: 5 } &&
                    view.StableCpuRpm == (expectedPass ? 2200 : 4000) && view.RawLatest.CpuNominalRpm == 2200;
            };
            var beforeApply = allQueries;
            var completed = false;
            var failedWindowCorrect = false;
            try
            {
                await backend.ApplyAsync(new FanCommand(40, 40, "Step 5 history/proof separation"), CancellationToken.None);
                completed = true;
            }
            catch (TimeoutException)
            {
                // Capture before independent FF/FF restore. The fresh historical
                // median is favorable but must not mask native proof failure.
                failedWindowCorrect = telemetry.ReadWindowCached() is { WindowCount: 5, StableCpuRpm: 4000 };
                await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
            }
            var viewAfter = telemetry.ReadWindowCached();
            failures += Report(output, $"WMI history cannot replace command proof: {variant}",
                preWriteWindowCorrect && completed == expectedPass && hardware.SetCalls == 1 &&
                proofs.Count == (expectedPass ? 1 : 0) && allQueries > beforeApply &&
                postCommandQueries == (variant == "favorable history native failure" ? 1 : 2) &&
                (expectedPass
                    ? viewAfter is { WindowCount: 5, StableCpuRpm: 2200, RawLatest.CpuNominalRpm: 4000 } && proofs[0].Contains("samples=2", StringComparison.Ordinal)
                    : failedWindowCorrect && hardware.RestoreCalls == 1 && hardware.State.CpuSetpoint == 255 && hardware.State.GpuSetpoint == 255));
        }
        return failures;
    }

    private static async Task<int> TestHungWmiProofRestoresThroughCoordinatorAsync(TextWriter output)
    {
        using var slot = new SemaphoreSlim(1, 1);
        using var releaseNative = new ManualResetEventSlim();
        var reader = new HpWmiFanProofReader(_ => { releaseNative.Wait(); return new(0, [30, 30]); },
            slot, () => Environment.TickCount64, TimeSpan.FromMilliseconds(60));
        var hardware = new FakeHardware { UseWmiTachometerBins = true };
        hardware.AsyncControlRead = async token =>
        {
            if (hardware.SetCalls > 0 && hardware.State.CpuSetpoint != 255)
                await reader.ReadFreshAsync(token);
            return hardware.ReadEcState();
        };
        var backend = NewBackend(hardware);
        var refreshes = 0;
        backend.RefreshActuationTelemetryAsync = ct => { refreshes++; return ValueTask.CompletedTask; };
        await using var coordinator = new FanControlCoordinator(backend);
        var now = DateTimeOffset.UtcNow;
        var safety = new SafetyGateResult(true, true, true, true, true, true, false,
            true, true, true, now, now, 1, Array.Empty<string>());
        var failed = false;
        try
        {
            await coordinator.TryEnterCustomAsync(safety, CancellationToken.None);
            try { await coordinator.ApplyAsync(new FanCommand(30, 30, "hung WMI proof"), safety, CancellationToken.None); }
            catch (TimeoutException) { failed = true; }
            return Report(output, "real coordinator restores FF/FF while timed-out native RPM read still owns its slot",
                failed && hardware.SetCalls == 1 && hardware.RestoreCalls == 1 &&
                hardware.State.CpuSetpoint == 255 && coordinator.Authority == FanAuthority.Firmware && slot.CurrentCount == 0 &&
                refreshes == 1); // Dispatch refresh only; a hung native read never manufactures liveness.
        }
        finally
        {
            releaseNative.Set();
            if (!await slot.WaitAsync(TimeSpan.FromSeconds(1))) throw new InvalidOperationException("Synthetic native read did not drain.");
            slot.Release();
        }
    }

    private static async Task<int> TestExpiredAdmissionBaselineIsNoWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware { UseWmiTachometerBins = true, ExpireAdmissionBaseline = true };
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        var refused = false;
        try { await backend.ApplyAsync(new FanCommand(30,30,"expired WMI admission"), CancellationToken.None); }
        catch (FanControlAdmissionException ex) { refused = ex.InnerException is InvalidDataException; }
        return Report(output, "expired WMI admission baseline refuses dispatch without issuing FF/FF",
            refused && hardware.SetCalls == 0 && hardware.RestoreCalls == 0 && hardware.State.CpuSetpoint == 255);
    }

    private static async Task<int> TestAcknowledgementTelemetryContinuationAsync(TextWriter output)
    {
        var hardware = new FakeHardware { UseWmiTachometerBins = true };
        var lease = new FakeWatchdogLeaseClient();
        await using var backend = NewProtectedBackend(hardware, lease);
        var refreshes = 0;
        var nativeReadInProgress = false;
        var refreshOutsideNativeRead = true;
        var refreshAfterDispatchOnly = true;
        hardware.AsyncControlRead = async ct =>
        {
            nativeReadInProgress = true;
            try
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                return hardware.ReadEcState();
            }
            finally { nativeReadInProgress = false; }
        };
        backend.RefreshActuationTelemetryAsync = ct =>
        {
            ct.ThrowIfCancellationRequested();
            refreshes++;
            refreshOutsideNativeRead &= !nativeReadInProgress;
            refreshAfterDispatchOnly &= hardware.SetCalls == 1;
            return ValueTask.CompletedTask;
        };

        await backend.EnterCustomModeAsync(CancellationToken.None);
        var admissionDoesNotRefresh = refreshes == 0;
        await backend.ApplyAsync(new FanCommand(30, 30, "ack telemetry continuation"), CancellationToken.None);
        var completedRefreshes = refreshes;
        await backend.ApplyAsync(new FanCommand(30, 30, "redundant target"), CancellationToken.None);
        var refreshesBeforeRestore = refreshes;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        return Report(output, "ACK refresh runs outside native proofs, adds no redundant writes and never runs during admission/restore",
            admissionDoesNotRefresh && completedRefreshes >= 3 && refreshes == refreshesBeforeRestore &&
            refreshOutsideNativeRead && refreshAfterDispatchOnly && hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 && lease.Calls.Contains("commit"));
    }

    private static async Task<int> TestSlowAcknowledgementKeepsRefreshingAsync(TextWriter output)
    {
        var hardware = new FakeHardware { FreezeCpuTach = true, FreezeGpuTach = true };
        await using var backend = new Hp8C40FanControlBackend(hardware, true, "slow synthetic response",
            timing: FastTiming with { TachometerAckTimeout = TimeSpan.FromSeconds(5), PollInterval = TimeSpan.FromMilliseconds(100) },
            activeTimeClock: new SyntheticActiveTimeClock());
        var started = Environment.TickCount64;
        var lastRefresh = started;
        long maximumGap = 0;
        var refreshes = 0;
        backend.RefreshActuationTelemetryAsync = ct =>
        {
            ct.ThrowIfCancellationRequested();
            var now = Environment.TickCount64;
            maximumGap = Math.Max(maximumGap, now - lastRefresh);
            lastRefresh = now;
            refreshes++;
            if (now - started > SafetyGate.MaximumTelemetryAge.TotalMilliseconds + 100)
                hardware.FreezeCpuTach = hardware.FreezeGpuTach = false;
            return ValueTask.CompletedTask;
        };
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(new FanCommand(30, 30, "mechanical ACK exceeds telemetry age"), CancellationToken.None);
        var elapsed = Environment.TickCount64 - started;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        return Report(output, "mechanical ACK longer than 3 s continues thermal acquisitions without another fan writer",
            elapsed > SafetyGate.MaximumTelemetryAge.TotalMilliseconds && refreshes > 3 &&
            maximumGap < SafetyGate.MaximumTelemetryAge.TotalMilliseconds && hardware.SetCalls == 1);
    }

    private static async Task<int> TestAcknowledgementTelemetryFailureRestoresAsync(TextWriter output)
    {
        var failures = 0;
        foreach (var cancel in new[] { false, true })
        {
            var hardware = new FakeHardware { UseWmiTachometerBins = true };
            var lease = new FakeWatchdogLeaseClient();
            var backend = NewProtectedBackend(hardware, lease);
            await using var coordinator = new FanControlCoordinator(backend);
            using var cts = new CancellationTokenSource();
            var refreshes = 0;
            backend.RefreshActuationTelemetryAsync = ct =>
            {
                if (++refreshes == 2)
                {
                    if (cancel) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
                    throw new InvalidOperationException("Synthetic thermal/lifecycle admission lost during ACK.");
                }
                return ValueTask.CompletedTask;
            };
            var safety = new SafetyGateResult(true, true, true, true, true, true, false,
                true, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, Array.Empty<string>());
            await coordinator.TryEnterCustomAsync(safety, CancellationToken.None);
            Exception? failure = null;
            try { await coordinator.ApplyAsync(new FanCommand(30, 30, "unsafe ACK refresh"), safety, cts.Token); }
            catch (Exception ex) { failure = ex; }
            failures += Report(output, cancel
                    ? "ACK refresh cancellation restores Firmware without committing the dispatched command"
                    : "ACK thermal/lifecycle refresh failure restores Firmware without committing the dispatched command",
                (cancel ? failure is OperationCanceledException : failure is InvalidOperationException) &&
                refreshes == 2 && hardware.SetCalls == 1 && hardware.RestoreCalls == 1 &&
                hardware.State.CpuSetpoint == 255 && hardware.State.GpuSetpoint == 255 &&
                coordinator.Authority == FanAuthority.Firmware && !lease.Calls.Contains("commit"));
        }
        return failures;
    }

    private static async Task<int> TestAcknowledgementTelemetryCannotExtendDeadlineAsync(TextWriter output)
    {
        var hardware = new FakeHardware { UseWmiTachometerBins = true };
        var clock = new AdvancingActiveTimeClock();
        var backend = NewBackend(hardware, clock);
        await using var coordinator = new FanControlCoordinator(backend);
        var refreshes = 0;
        backend.RefreshActuationTelemetryAsync = ct =>
        {
            if (++refreshes == 2) clock.Milliseconds += 251;
            return ValueTask.CompletedTask;
        };
        var safety = new SafetyGateResult(true, true, true, true, true, true, false,
                true, true, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, Array.Empty<string>());
        await coordinator.TryEnterCustomAsync(safety, CancellationToken.None);
        var timedOut = false;
        try { await coordinator.ApplyAsync(new FanCommand(30, 30, "ACK refresh deadline"), safety, CancellationToken.None); }
        catch (TimeoutException) { timedOut = true; }
        return Report(output, "thermal refresh cannot renew the independent tachometer ACK deadline",
            timedOut && refreshes == 2 && hardware.SetCalls == 1 && hardware.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestAutomaticRefreshPreservesAdmissionAsync(TextWriter output)
    {
        var hardware = new FakeHardware { UseWmiTachometerBins = true };
        var backend = NewBackend(hardware);
        await using var coordinator = new FanControlCoordinator(backend);
        var identity = new HardwareIdentity(Hp8C40TargetProfile.BoardManufacturer, Hp8C40TargetProfile.BoardProduct,
            Hp8C40TargetProfile.BoardVersion, Hp8C40TargetProfile.SystemManufacturer, Hp8C40TargetProfile.SystemProductName,
            Hp8C40TargetProfile.SystemSkuPrefix + "#AKH", Hp8C40TargetProfile.ValidatedBiosVersion);
        var origin = DateTimeOffset.UtcNow;
        long clock = 0;
        DateTimeOffset Now() => origin.AddMilliseconds(clock);
        TelemetrySnapshot Sample() => new(Now(), "Intel Core i7-13700H", 45, 5, 17,
            Hp8C40TargetProfile.ExpectedGpuName, 47, 14, 0, 3000, 3000)
        {
            CpuExpectedPhysicalCoreCount = Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount,
            CpuCoreTemperatures = Enumerable.Range(0, Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount)
                .Select(i => new CpuCoreTemperatureSample(i, i, i < 6 ? "Performance" : "Efficiency", 45)).ToArray()
        };
        var controller = new AdaptiveFanProductionController(coordinator, Hp8C40AdaptiveCandidateV1.Create(), false, true,
            automaticHardware: identity, automaticMilliseconds: () => clock, utcNow: Now);
        await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
        var original = Sample();
        var latest = original;
        var supervision = new List<Task>();
        backend.RefreshActuationTelemetryAsync = ct =>
        {
            clock += 100;
            latest = Sample();
            var raw = SafetyGate.Evaluate(identity, SystemState.Healthy, latest, Now(), true);
            var effective = controller.EvaluateAutomaticSafety(latest, raw, observe: true);
            // Matches the GUI event: accept current safety/cancel immediately,
            // but never await a coordinator gate owned by this actuation.
            supervision.Add(coordinator.EnforceSafetyAsync(effective, "ACK thermal acquisition", CancellationToken.None).AsTask());
            ct.ThrowIfCancellationRequested();
            if (!controller.EvaluateAutomaticSafety(latest,
                    SafetyGate.EvaluateForDisplay(identity, SystemState.Healthy, latest, Now(), true), observe: false).CustomControlPermitted)
                throw new InvalidOperationException("ACK acquisition lost Automatic admission.");
            return ValueTask.CompletedTask;
        };
        var result = await controller.ProcessAutomaticAsync(original,
            SafetyGate.Evaluate(identity, SystemState.Healthy, original, Now(), true), CancellationToken.None,
            refreshRawSafetyProvider: () => ReferenceEquals(latest, original)
                ? SafetyGate.EvaluateForDisplay(identity, SystemState.Healthy, original, Now(), true)
                : null);
        await Task.WhenAll(supervision);
        var sessionStillOpen = controller.AutomaticFreshAcquisitionRequired && coordinator.Authority == FanAuthority.Custom;
        clock += 100;
        var next = Sample();
        var hold = await controller.ProcessAutomaticAsync(next,
            SafetyGate.Evaluate(identity, SystemState.Healthy, next, Now(), true), CancellationToken.None);
        await controller.SetModeAsync(AdaptiveFanProductionMode.Firmware, CancellationToken.None);
        return Report(output, "real Automatic controller accepts newer post-dispatch safety without relaxing native epoch admission or deadlocking",
            result.Action == AdaptiveFanProductionActionKind.EnterCustomAndApply && sessionStillOpen &&
            !ReferenceEquals(latest, original) && hold.Action == AdaptiveFanProductionActionKind.HoldCustom &&
            hardware.SetCalls == 1 && hardware.RestoreCalls == 1 && coordinator.Authority == FanAuthority.Firmware);
    }

    private sealed class AdvancingActiveTimeClock : IActiveTimeClock
    {
        public ulong Milliseconds { get; set; }
    }

    private static Hp8C40FanControlBackend NewBackend(
        FakeHardware hardware,
        IActiveTimeClock? activeTimeClock = null) =>
        new(
            hardware,
            targetSupported: true,
            supportDetail: "synthetic validated target",
            timing: FastTiming,
            activeTimeClock: activeTimeClock ?? new SyntheticActiveTimeClock());

    private static Hp8C40FanControlBackend NewProtectedBackend(
        FakeHardware hardware,
        FakeWatchdogLeaseClient lease,
        IHp8C40FanWriteQualificationHook? qualificationHook = null) =>
        new(
            hardware,
            targetSupported: true,
            supportDetail: "synthetic validated target",
            timing: FastTiming,
            watchdogLease: lease,
            activeTimeClock: new SyntheticActiveTimeClock(),
            qualificationHook: qualificationHook);

    private static int Report(TextWriter output, string name, bool pass)
    {
        output.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }

    private sealed class SyntheticActiveTimeClock : IActiveTimeClock
    {
        public ulong Milliseconds => checked((ulong)Environment.TickCount64);
    }

    private sealed class FrozenActiveTimeClock : IActiveTimeClock
    {
        public ulong Milliseconds => 0;
    }

    private sealed class RecordingQualificationHook :
        IHp8C40FanWriteQualificationHook
    {
        private readonly Action _onCall;

        public RecordingQualificationHook(
            Action onCall)
        {
            _onCall = onCall;
        }

        public int Calls { get; private set; }
        public byte LastCpuTarget { get; private set; }
        public byte LastGpuTarget { get; private set; }
        public (byte CpuSetpoint, byte GpuSetpoint) LastSetpointAck { get; private set; }
        public Hp8C40EcControlState? LastTachAck { get; private set; }

        public ValueTask AfterHardwareAcknowledgedBeforeWatchdogCommitAsync(
            byte cpuTarget,
            byte gpuTarget,
            (byte CpuSetpoint, byte GpuSetpoint) setpointAck,
            Hp8C40EcControlState tachAck,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Calls++;
            LastCpuTarget = cpuTarget;
            LastGpuTarget = gpuTarget;
            LastSetpointAck = setpointAck;
            LastTachAck = tachAck;
            _onCall();

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWatchdogLeaseClient :
        IFanControlWatchdogLeaseClient
    {
        public List<string> Calls { get; } = new();
        public Action<string>? OnCall { get; set; }
        public Action<int, int>? OnWriteIntent { get; set; }

        public bool FailPrepare { get; set; }
        public bool FailAbort { get; set; }
        public bool FailCommit { get; set; }
        public bool FailProbe { get; set; }
        public int? FailProbeAfterSuccessfulCalls { get; set; }
        public bool FailHeartbeat { get; set; }
        public bool FailRestoreBegin { get; set; }
        public bool FailRelease { get; set; }

        public ValueTask PrepareAsync(
            CancellationToken cancellationToken)
        {
            Record("prepare");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailPrepare, "synthetic Prepare failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelPreparedAsync(
            CancellationToken cancellationToken)
        {
            Record("cancel-prepared");
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteIntentAsync(
            int cpuLevel,
            int gpuLevel,
            CancellationToken cancellationToken)
        {
            Record("intent");
            cancellationToken.ThrowIfCancellationRequested();
            OnWriteIntent?.Invoke(cpuLevel, gpuLevel);
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortWriteIntentAsync(
            CancellationToken cancellationToken)
        {
            Record("abort");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailAbort, "synthetic AbortWriteIntent failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(
            int cpuLevel,
            int gpuLevel,
            CancellationToken cancellationToken)
        {
            Record("commit");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailCommit, "synthetic Commit failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask ProbeAsync(
            CancellationToken cancellationToken)
        {
            Record("probe");
            cancellationToken.ThrowIfCancellationRequested();

            var probeCalls =
                Calls.Count(call => call == "probe");

            ThrowIf(
                FailProbe ||
                (FailProbeAfterSuccessfulCalls.HasValue &&
                 probeCalls > FailProbeAfterSuccessfulCalls.Value),
                "synthetic Probe failure");

            return ValueTask.CompletedTask;
        }

        public ValueTask HeartbeatAsync(
            CancellationToken cancellationToken)
        {
            Record("heartbeat");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailHeartbeat, "synthetic Heartbeat failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask RestoreBeginAsync(
            CancellationToken cancellationToken)
        {
            Record("restore-begin");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailRestoreBegin, "synthetic RestoreBegin failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask ReleaseAsync(
            CancellationToken cancellationToken)
        {
            Record("release");
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIf(FailRelease, "synthetic Release failure");
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Record("dispose");
            return ValueTask.CompletedTask;
        }

        private void Record(string call)
        {
            Calls.Add(call);
            OnCall?.Invoke(call);
        }

        private static void ThrowIf(
            bool shouldThrow,
            string message)
        {
            if (shouldThrow)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    internal sealed class FakeHardware : IHp8C40FanHardware
    {
        public static readonly Hp8C40EcControlState AutoState = new(
            CpuRateTarget: 255,
            GpuRateTarget: 255,
            CpuRate: 54,
            GpuRate: 54,
            CpuSetpoint: 255,
            GpuSetpoint: 255,
            Diagnostic62: 0x06,
            Diagnostic63: 230,
            Mode: 0x00,
            MaxFan: 0x00,
            FanSwitch: 0x00,
            CpuRpm: 2200,
            GpuRpm: 2400);

        private byte? _targetCpu;
        private byte? _targetGpu;
        private ushort _cpuRpmAtCommand;
        private int _postSetReadCount;

        public Hp8C40EcControlState State { get; set; } = AutoState;
        public int SetCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public bool IgnoreRestore { get; set; }
        public bool FreezeCpuTach { get; set; }
        public bool FreezeGpuTach { get; set; }
        public bool PulseCpuTachOnceThenReturnBaseline { get; set; }
        public int TransientEcReadFailuresDuringTachAck { get; set; }
        public int TransientSetpointAckReadFailures { get; set; }
        public Action<int>? OnEcRead { get; set; }
        public Action? OnSetFanLevel { get; set; }
        public Queue<(byte MaxFan, byte FanSwitch)> GuardReadOverrides { get; } = new();
        public Exception? ReadEcStateException { get; set; }
        public Exception? GetCurrentFanLevelsException { get; set; }
        public int EcReadCalls { get; private set; }
        public Func<CancellationToken, ValueTask<Hp8C40EcControlState>>? AsyncControlRead { get; set; }
        public ValueTask<Hp8C40EcControlState> ReadEcStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return AsyncControlRead is not null ? AsyncControlRead(cancellationToken) : ValueTask.FromResult(ReadEcState());
        }
        public bool ExpireAdmissionBaseline { get; set; }
        public async ValueTask<Hp8C40EcControlState> ReadAdmissionStateAsync(CancellationToken cancellationToken)
        {
            var state = await ReadEcStateAsync(cancellationToken);
            return ExpireAdmissionBaseline ? state with { FanQueryStartedAtMilliseconds = Environment.TickCount64 - 3000 } : state;
        }
        public bool UseWmiTachometerBins { get; set; }
        public bool RepeatWmiQueryIdentityAfterWrite { get; set; }
        public bool PreCommandWmiQueryAfterWrite { get; set; }
        public bool WmiProofFailureAfterWrite { get; set; }

        public Hp8C40EcControlState ReadEcState()
        {
            EcReadCalls++;
            OnEcRead?.Invoke(EcReadCalls);

            if (ReadEcStateException is not null)
            {
                throw ReadEcStateException;
            }

            if (_targetCpu.HasValue &&
                _targetGpu.HasValue &&
                _postSetReadCount == 0 &&
                TransientSetpointAckReadFailures > 0)
            {
                TransientSetpointAckReadFailures--;
                throw new TimeoutException(
                    "Timed out waiting for Global\\Access_EC.");
            }

            // The first post-SetFanLevel ReadEcState is consumed by the fake's
            // default ReadSetpoint path. Begin transient injection only after
            // that setpoint acknowledgement has advanced _postSetReadCount, so
            // the failure deterministically lands in tachometer acknowledgement.
            if (_targetCpu.HasValue &&
                _targetGpu.HasValue &&
                _postSetReadCount >= 1 &&
                TransientEcReadFailuresDuringTachAck > 0)
            {
                TransientEcReadFailuresDuringTachAck--;
                throw new IOException(
                    "synthetic exhausted EC tach snapshot: OBF did not become full");
            }

            if (_targetCpu.HasValue && _targetGpu.HasValue)
            {
                var cpuDesired = DesiredCpuRpm(_targetCpu.Value);
                var gpuDesired = DesiredGpuRpm(_targetGpu.Value);

                ushort cpuRpm;
                if (PulseCpuTachOnceThenReturnBaseline)
                {
                    cpuRpm = _postSetReadCount == 1
                        ? (ushort)Math.Min(10_000, _cpuRpmAtCommand + 200)
                        : _cpuRpmAtCommand;
                }
                else
                {
                    cpuRpm = FreezeCpuTach
                        ? State.CpuRpm
                        : MoveToward(State.CpuRpm, cpuDesired, 200);
                }

                State = State with
                {
                    CpuRpm = cpuRpm,
                    GpuRpm = FreezeGpuTach
                        ? State.GpuRpm
                        : MoveToward(State.GpuRpm, gpuDesired, 200)
                };

                _postSetReadCount++;
            }

            if (GuardReadOverrides.Count > 0)
            {
                var guard = GuardReadOverrides.Dequeue();
                return State with
                {
                    MaxFan = guard.MaxFan,
                    FanSwitch = guard.FanSwitch
                };
            }

            if (UseWmiTachometerBins)
            {
                if (_targetCpu.HasValue && _postSetReadCount > 1 && WmiProofFailureAfterWrite)
                    throw new TimeoutException("synthetic native WMI proof timeout");
                return State with
                {
                    CpuRpm = (ushort)(State.CpuRpm / 100 * 100), GpuRpm = (ushort)(State.GpuRpm / 100 * 100),
                    TachometerResolutionRpm = 100,
                    FanQuerySequence = RepeatWmiQueryIdentityAfterWrite ? 1 : EcReadCalls,
                    FanQueryStartedAtMilliseconds = Environment.TickCount64 -
                        (_targetCpu.HasValue && PreCommandWmiQueryAfterWrite ? 5000 : 0)
                };
            }
            return State;
        }

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels()
        {
            if (GetCurrentFanLevelsException is not null)
            {
                throw GetCurrentFanLevelsException;
            }

            return (
                (byte)Math.Clamp(State.CpuRpm / 100, 0, 255),
                (byte)Math.Clamp(State.GpuRpm / 100, 0, 255));
        }

        public void SetFanLevel(byte cpuLevel, byte gpuLevel)
        {
            OnSetFanLevel?.Invoke();
            SetCalls++;
            _cpuRpmAtCommand = State.CpuRpm;
            _postSetReadCount = 0;
            _targetCpu = cpuLevel;
            _targetGpu = gpuLevel;
            State = State with
            {
                CpuSetpoint = cpuLevel,
                GpuSetpoint = gpuLevel
            };
        }

        public void RestoreFirmwareAuto()
        {
            RestoreCalls++;
            if (!IgnoreRestore)
            {
                _targetCpu = null;
                _targetGpu = null;
                State = State with
                {
                    CpuSetpoint = byte.MaxValue,
                    GpuSetpoint = byte.MaxValue
                };
            }
        }

        public void Dispose()
        {
        }

        private static ushort DesiredCpuRpm(byte level) =>
            (ushort)Math.Min(level * 100, 10_000);

        private static ushort DesiredGpuRpm(byte level) =>
            (ushort)Math.Min(level * 100, 10_000);

        private static ushort MoveToward(ushort current, ushort target, int step)
        {
            if (current < target)
            {
                return (ushort)Math.Min(target, current + step);
            }

            if (current > target)
            {
                return (ushort)Math.Max(target, current - step);
            }

            return current;
        }
    }
}
