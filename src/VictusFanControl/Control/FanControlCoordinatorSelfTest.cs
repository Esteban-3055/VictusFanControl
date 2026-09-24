using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control;

public static class FanControlCoordinatorSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;
        var now = DateTimeOffset.UtcNow;
        var safety = BuildReadySafety(now);

        failures += await TestDisabledBackendAsync(output, safety);
        failures += await TestSafetyPermissionBlockAsync(output);
        failures += await TestEnterFailureRestoresAsync(output, safety);
        failures += await TestNormalRestoreAsync(output, safety);
        failures += await TestSafetyLossRestoresAsync(output, safety, now);
        failures += await TestInvalidCommandRestoresAsync(output, safety);
        failures += await TestBackendFailureRestoresAsync(output, safety);
        failures += await TestRealHpBackendIntegrationAsync(output, safety);
        failures += await TestLifecycleBoundaryRestoresAndRejectsStaleSafetyAsync(output, now);
        failures += await TestOwnershipConflictDoesNotClearExternalOverrideAsync(output, safety);
        failures += await TestSafetyPreemptsInFlightCommandAsync(output, safety, now);
        failures += await TestUnsafeReentryRestoresAsync(output, safety, now);
        failures += await TestRuntimeOwnershipMismatchRestoresAsync(output, safety);
        failures += await TestLifecycleFenceClosesBeforeCoordinatorGateAsync(output, safety, now);
        failures += await TestStaleSafetyEvaluationCannotTearDownNewerSessionAsync(output, safety, now);

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "FanControlCoordinator self-test: PASS"
            : $"FanControlCoordinator self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 7;
    }

    private static async Task<int> TestDisabledBackendAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        await using var coordinator =
            new FanControlCoordinator(new DisabledFanControlBackend());

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        return Report(
            output,
            "disabled backend refuses custom authority",
            !entered && coordinator.Authority == FanAuthority.Firmware);
    }


    private static async Task<int> TestSafetyPermissionBlockAsync(TextWriter output)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var now = DateTimeOffset.UtcNow;
        var hardware = new HardwareIdentity(
            "HP",
            "88F8",
            "88.58",
            "HP",
            "Victus by HP Laptop 16-d0xxx",
            "62C37LA#AKH",
            Hp88F8TargetProfile.ValidatedBiosVersion);

        var snapshot = new TelemetrySnapshot(
            now,
            "Intel test CPU",
            50,
            15,
            10,
            Hp88F8TargetProfile.ExpectedGpuName,
            45,
            25,
            5,
            2200,
            2400);

        var blockedSafety = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent: false);

        var entered = await coordinator.TryEnterCustomAsync(
            blockedSafety,
            CancellationToken.None);

        return Report(
            output,
            "coordinator obeys SafetyGate custom-control permission",
            !entered &&
            backend.EnterCalls == 0 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestEnterFailureRestoresAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend { ThrowOnEnterAfterActivate = true };
        await using var coordinator = new FanControlCoordinator(backend);

        var threw = false;
        try
        {
            await coordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);
        }
        catch (IOException)
        {
            threw = true;
        }

        return Report(
            output,
            "partial custom-entry failure forces firmware restore",
            threw &&
            backend.EnterCalls == 1 &&
            backend.RestoreCalls == 1 &&
            !backend.Active &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestNormalRestoreAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        if (entered)
        {
            await coordinator.ApplyAsync(
                new FanCommand(30, 30, "self-test"),
                safety,
                CancellationToken.None);

            await coordinator.RestoreFirmwareAsync(
                "self-test complete",
                CancellationToken.None);
        }

        return Report(
            output,
            "normal session restores firmware authority",
            entered &&
            backend.EnterCalls == 1 &&
            backend.ApplyCalls == 1 &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestSafetyLossRestoresAsync(
        TextWriter output,
        SafetyGateResult readySafety,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            readySafety,
            CancellationToken.None);

        var staleSafety = BuildReadySafety(now - TimeSpan.FromSeconds(10), now);

        var threw = false;
        try
        {
            await coordinator.ApplyAsync(
                new FanCommand(30, 30, "should fail"),
                staleSafety,
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        return Report(
            output,
            "safety loss restores before refusing command",
            entered &&
            threw &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }


    private static async Task<int> TestInvalidCommandRestoresAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        var threw = false;
        try
        {
            await coordinator.ApplyAsync(
                new FanCommand(13, 30, "out of range"),
                safety,
                CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        return Report(
            output,
            "out-of-range command restores firmware authority",
            entered &&
            threw &&
            backend.ApplyCalls == 0 &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestBackendFailureRestoresAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend { ThrowOnApply = true };
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        var threw = false;
        try
        {
            await coordinator.ApplyAsync(
                new FanCommand(30, 30, "backend failure"),
                safety,
                CancellationToken.None);
        }
        catch (IOException)
        {
            threw = true;
        }

        return Report(
            output,
            "backend command failure triggers restore",
            entered &&
            threw &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }








    private static async Task<int> TestStaleSafetyEvaluationCannotTearDownNewerSessionAsync(
        TextWriter output,
        SafetyGateResult initialSafety,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            initialSafety,
            CancellationToken.None);

        var newerSafety = BuildReadySafety(
            now + TimeSpan.FromSeconds(2),
            now + TimeSpan.FromSeconds(2));

        var newerAccepted = await coordinator.EnforceSafetyAsync(
            newerSafety,
            "newer healthy sample",
            CancellationToken.None);

        var staleUnsafe = BuildReadySafety(
            now - TimeSpan.FromSeconds(10),
            now);

        var staleIgnored = await coordinator.EnforceSafetyAsync(
            staleUnsafe,
            "delayed stale unsafe result",
            CancellationToken.None);

        var restoreCallsBeforeCleanup = backend.RestoreCalls;
        var authorityBeforeCleanup = coordinator.Authority;

        await coordinator.RestoreFirmwareAsync(
            "stale-safety test cleanup",
            CancellationToken.None);

        return Report(
            output,
            "stale async safety result cannot tear down a newer validated custom session",
            entered &&
            newerAccepted &&
            staleIgnored &&
            restoreCallsBeforeCleanup == 0 &&
            authorityBeforeCleanup == FanAuthority.Custom);
    }

    private static async Task<int> TestLifecycleFenceClosesBeforeCoordinatorGateAsync(
        TextWriter output,
        SafetyGateResult safety,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend { BlockApplyUntilReleased = true };
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        var applyTask = coordinator.ApplyAsync(
            new FanCommand(30, 30, "hold coordinator gate"),
            safety,
            CancellationToken.None).AsTask();

        await backend.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var boundary = now + TimeSpan.FromSeconds(1);
        var blockTask = coordinator.BlockCustomAdmissionAndRestoreAsync(
            "synthetic suspend race",
            boundary,
            CancellationToken.None).AsTask();

        // Queue a stale re-entry while the original Apply still owns _gate.
        // It must fail even if SemaphoreSlim later schedules it before the
        // lifecycle Block waiter.
        var staleReentryTask = coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None).AsTask();

        backend.ApplyRelease.TrySetResult(true);

        await applyTask;
        await blockTask;
        var staleReentry = await staleReentryTask;

        return Report(
            output,
            "lifecycle fence closes admission before waiting for coordinator gate",
            entered &&
            !staleReentry &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestRuntimeOwnershipMismatchRestoresAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        backend.OwnershipValid = false;

        var stillSafe = await coordinator.EnforceSafetyAsync(
            safety,
            "synthetic external overwrite",
            CancellationToken.None);

        return Report(
            output,
            "continuous backend ownership mismatch forces firmware restore",
            entered &&
            !stillSafe &&
            backend.StatusCalls == 1 &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestUnsafeReentryRestoresAsync(
        TextWriter output,
        SafetyGateResult safety,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        var staleSafety = BuildReadySafety(
            now - TimeSpan.FromSeconds(10),
            now);

        var reentered = await coordinator.TryEnterCustomAsync(
            staleSafety,
            CancellationToken.None);

        return Report(
            output,
            "already-custom reentry cannot bypass a newly unsafe SafetyGate",
            entered &&
            !reentered &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestOwnershipConflictDoesNotClearExternalOverrideAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var backend = new RecordingBackend { ThrowOwnershipConflictOnEnter = true };
        await using var coordinator = new FanControlCoordinator(backend);

        var threw = false;
        try
        {
            await coordinator.TryEnterCustomAsync(
                safety,
                CancellationToken.None);
        }
        catch (FanControlOwnershipConflictException)
        {
            threw = true;
        }

        return Report(
            output,
            "read-only admission conflict does not clear another controller",
            threw &&
            backend.EnterCalls == 1 &&
            backend.RestoreCalls == 0 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestSafetyPreemptsInFlightCommandAsync(
        TextWriter output,
        SafetyGateResult safety,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend { BlockApplyUntilCancelled = true };
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        var applyTask = coordinator.ApplyAsync(
            new FanCommand(30, 30, "blocking command"),
            safety,
            CancellationToken.None).AsTask();

        await backend.ApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var unsafeSafety = BuildReadySafety(
            now - TimeSpan.FromSeconds(10),
            now);

        var enforceTask = coordinator.EnforceSafetyAsync(
            unsafeSafety,
            "synthetic telemetry loss",
            CancellationToken.None).AsTask();

        var applyCancelled = false;
        try
        {
            await applyTask;
        }
        catch (OperationCanceledException)
        {
            applyCancelled = true;
        }

        var enforcementCompleted = await enforceTask;

        return Report(
            output,
            "safety supervisor preempts in-flight command and restores firmware",
            entered &&
            applyCancelled &&
            enforcementCompleted &&
            backend.RestoreCalls == 1 &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestLifecycleBoundaryRestoresAndRejectsStaleSafetyAsync(
        TextWriter output,
        DateTimeOffset now)
    {
        var backend = new RecordingBackend();
        await using var coordinator = new FanControlCoordinator(backend);

        var initialSafety = BuildReadySafety(now);
        var entered = await coordinator.TryEnterCustomAsync(
            initialSafety,
            CancellationToken.None);

        var boundary = now + TimeSpan.FromSeconds(1);
        await coordinator.BlockCustomAdmissionAndRestoreAsync(
            "synthetic suspend",
            boundary,
            CancellationToken.None);

        var reopened = await coordinator.AllowCustomAdmissionAfterRecoveryAsync(
            boundary + TimeSpan.FromSeconds(1),
            "synthetic resume validated",
            CancellationToken.None);

        var staleReentry = await coordinator.TryEnterCustomAsync(
            initialSafety,
            CancellationToken.None);

        var freshSafety = BuildReadySafety(
            boundary + TimeSpan.FromSeconds(2),
            boundary + TimeSpan.FromSeconds(2));

        var freshReentry = await coordinator.TryEnterCustomAsync(
            freshSafety,
            CancellationToken.None);

        if (freshReentry)
        {
            await coordinator.RestoreFirmwareAsync(
                "synthetic lifecycle test complete",
                CancellationToken.None);
        }

        return Report(
            output,
            "lifecycle boundary restores and rejects stale pre-resume safety",
            entered &&
            backend.RestoreCalls == 2 &&
            reopened &&
            !staleReentry &&
            freshReentry &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static async Task<int> TestRealHpBackendIntegrationAsync(
        TextWriter output,
        SafetyGateResult safety)
    {
        var hardware = new Hp88F8FanControlBackendSelfTest.FakeHardware();
        await using var backend = new Hp88F8FanControlBackend(
            hardware,
            timing: new Hp88F8FanBackendTiming(
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(10)));
        await using var coordinator = new FanControlCoordinator(backend);

        var entered = await coordinator.TryEnterCustomAsync(
            safety,
            CancellationToken.None);

        if (!entered)
        {
            return Report(
                output,
                "validated HP backend integrates through coordinator",
                false);
        }

        await coordinator.ApplyAsync(
            new FanCommand(30, 30, "coordinator-real-backend-test"),
            safety,
            CancellationToken.None);

        var during = hardware.State;

        await coordinator.RestoreFirmwareAsync(
            "coordinator-real-backend-test complete",
            CancellationToken.None);

        return Report(
            output,
            "validated HP backend integrates through coordinator",
            during.CpuSetpoint == 30 &&
            during.GpuSetpoint == 30 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            coordinator.Authority == FanAuthority.Firmware);
    }

    private static SafetyGateResult BuildReadySafety(DateTimeOffset timestamp) =>
        BuildReadySafety(timestamp, timestamp);

    private static SafetyGateResult BuildReadySafety(
        DateTimeOffset timestamp,
        DateTimeOffset now)
    {
        var hardware = new HardwareIdentity(
            "HP",
            "88F8",
            "88.58",
            "HP",
            "Victus by HP Laptop 16-d0xxx",
            "62C37LA#AKH",
            Hp88F8TargetProfile.ValidatedBiosVersion);

        var snapshot = new TelemetrySnapshot(
            timestamp,
            "Intel test CPU",
            50,
            15,
            10,
            Hp88F8TargetProfile.ExpectedGpuName,
            45,
            25,
            5,
            2200,
            2400);

        return SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent: true);
    }

    private static int Report(TextWriter output, string name, bool pass)
    {
        output.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }

    private sealed class RecordingBackend : IFanControlBackend
    {
        public string Name => "self-test backend";
        public bool CanWrite => true;
        public FanBackendCapabilities Capabilities =>
            new("88F8", 14, 50, SupportsIndependentLevels: true);

        public int EnterCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public bool OwnershipValid { get; set; } = true;
        public bool ThrowOnApply { get; init; }
        public bool ThrowOnEnterAfterActivate { get; init; }
        public bool ThrowOwnershipConflictOnEnter { get; init; }
        public bool BlockApplyUntilCancelled { get; init; }
        public bool BlockApplyUntilReleased { get; init; }
        public TaskCompletionSource<bool> ApplyStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ApplyRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Active { get; private set; }

        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return ValueTask.FromResult(new FanBackendStatus(
                Name,
                CanWrite,
                Active,
                OwnershipValid,
                "self-test"));
        }

        public ValueTask EnterCustomModeAsync(CancellationToken cancellationToken)
        {
            EnterCalls++;
            Active = true;

            if (ThrowOwnershipConflictOnEnter)
            {
                Active = false;
                return ValueTask.FromException(
                    new FanControlOwnershipConflictException("synthetic external owner"));
            }

            if (ThrowOnEnterAfterActivate)
            {
                return ValueTask.FromException(
                    new IOException("synthetic partial enter failure"));
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask ApplyAsync(
            FanCommand command,
            CancellationToken cancellationToken)
        {
            ApplyCalls++;
            ApplyStarted.TrySetResult(true);

            if (ThrowOnApply)
            {
                throw new IOException("synthetic backend failure");
            }

            if (BlockApplyUntilReleased)
            {
                await ApplyRelease.Task.ConfigureAwait(false);
            }

            if (BlockApplyUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask RestoreFirmwareAutoAsync(CancellationToken cancellationToken)
        {
            RestoreCalls++;
            Active = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
