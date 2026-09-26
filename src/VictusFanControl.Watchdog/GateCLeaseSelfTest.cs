using VictusFanControl.Control;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace VictusFanControl.Watchdog;

internal static class GateCLeaseSelfTest
{
    private static readonly ControllerIdentity Controller =
        new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 638000000000000000);

    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await CaseAsync(
            output,
            "happy state machine persists each critical transition",
            HappyStateMachineAsync);

        failures += await CaseAsync(
            output,
            "owner death in PREPARED clears journal without restore",
            PreparedOwnerLossAsync);

        failures += await CaseAsync(
            output,
            "explicit PREPARED cancel clears lease without hardware write",
            CancelPreparedAsync);

        failures += await CaseAsync(
            output,
            "service restart in PREPARED clears journal without restore",
            PreparedRestartAsync);

        failures += await CaseAsync(
            output,
            "service stop in PREPARED clears journal without restore",
            ServiceStopPreparedAsync);

        failures += await CaseAsync(
            output,
            "service stop while OWNED restores firmware",
            ServiceStopOwnedAsync);

        failures += await CaseAsync(
            output,
            "first WRITE_ARMED abort safely returns to PREPARED",
            AbortFirstWriteIntentAsync);

        failures += await CaseAsync(
            output,
            "target-transition abort safely returns to previous OWNED target",
            AbortTransitionWriteIntentAsync);

        failures += await CaseAsync(
            output,
            "unsafe WRITE_ARMED abort retains durable evidence",
            UnsafeAbortWriteIntentAsync);

        failures += await CaseAsync(
            output,
            "restart after WRITE_ARMED before WMI normalizes firmware restore",
            RestartWriteArmedBeforeWriteAsync);

        failures += await CaseAsync(
            output,
            "restart after WMI before Commit restores pending target",
            RestartAfterWriteBeforeCommitAsync);

        failures += await CaseAsync(
            output,
            "restart after Commit restores OWNED target",
            RestartAfterCommitAsync);

        failures += await CaseAsync(
            output,
            "restart during RESTORING completes firmware handoff",
            RestartDuringRestoringAsync);

        failures += await CaseAsync(
            output,
            "WRITE_ARMED transition accepts previous or pending target",
            TransitionAllowsPreviousOrPendingAsync);

        failures += await CaseAsync(
            output,
            "stale generation is rejected without journal mutation",
            StaleGenerationAsync);

        failures += await CaseAsync(
            output,
            "lease mutations reject a different verified controller identity",
            ControllerIdentityMismatchAsync);

        failures += await CaseAsync(
            output,
            "out-of-range WriteIntent is rejected without journal mutation",
            OutOfRangeWriteIntentAsync);

        failures += await CaseAsync(
            output,
            "Release normalizes full firmware restore before clearing lease",
            ReleaseNormalizesRestoreAsync);

        failures += await CaseAsync(
            output,
            "Release waits for delayed FF/FF publication before clearing lease",
            ReleaseWaitsForDelayedFirmwareAckAsync);

        failures += await CaseAsync(
            output,
            "Release restore failure retains durable lease",
            ReleaseFailureRetainsLeaseAsync);

        failures += await CaseAsync(
            output,
            "Release takes over when controller left owned setpoint active",
            ReleaseTakesOverOwnedTargetAsync);

        failures += await CaseAsync(
            output,
            "Release never clears an unknown external override",
            ReleasePreservesUnknownExternalOverrideAsync);

        failures += await CaseAsync(
            output,
            "duplicate Release is rejected without a second hardware restore",
            DuplicateReleaseAsync);

        failures += await CaseAsync(
            output,
            "probe validates OWNED without renewing heartbeat",
            ProbeDoesNotRenewHeartbeatAsync);

        failures += await CaseAsync(
            output,
            "heartbeat renews liveness without rewriting durable journal",
            HeartbeatDoesNotPersistAsync);

        failures += await CaseAsync(
            output,
            "late heartbeat cannot revive an expired OWNED lease",
            LateHeartbeatCannotReviveAsync);

        failures += await CaseAsync(
            output,
            "late WriteIntent cannot revive an expired OWNED lease",
            LateWriteIntentCannotReviveAsync);

        failures += await CaseAsync(
            output,
            "OWNED heartbeat timeout restores",
            HeartbeatTimeoutAsync);

        failures += await CaseAsync(
            output,
            "late Commit cannot revive an expired WRITE_ARMED lease",
            LateCommitCannotReviveAsync);

        failures += await CaseAsync(
            output,
            "WRITE_ARMED deadline restores",
            WriteArmedTimeoutAsync);

        failures += await CaseAsync(
            output,
            "RESTORING deadline takes over restore",
            RestoringTimeoutAsync);

        failures += await CaseAsync(
            output,
            "unknown setpoint with active lease is not cleared",
            AmbiguousOwnershipAsync);

        failures += await CaseAsync(
            output,
            "Prepare refuses an external fixed override",
            PrepareRefusesExternalOverrideAsync);

        failures += await CaseAsync(
            output,
            "PREPARED restart never clears an external fixed override",
            PreparedRestartPreservesExternalOverrideAsync);

        failures += await CaseAsync(
            output,
            "fixed setpoint without journal is treated as external",
            ExternalOverrideWithoutLeaseAsync);

        failures += await CaseAsync(
            output,
            "restore failure retains durable ownership evidence",
            RestoreFailureRetainsJournalAsync);

        failures += await CaseAsync(
            output,
            "corrupt journal never triggers restore",
            CorruptJournalAsync);

        failures += await CaseAsync(
            output,
            "malformed protocol frame is rejected",
            MalformedFrameAsync);

        failures += await CaseAsync(
            output,
            "named-pipe identity mismatch is rejected",
            NamedPipeIdentityMismatchAsync);

        failures += await CaseAsync(
            output,
            "live-controller pipe loss before WriteIntent ACK retains WRITE_ARMED until deadline",
            NamedPipeLossBeforeWriteIntentAckAsync);

        failures += await CaseAsync(
            output,
            "proven controller death while OWNED restores immediately",
            ProvenOwnerDeathWhileOwnedRestoresAsync);

        failures += await CaseAsync(
            output,
            "Gate D live-controller pipe loss retains lease and permits reconnect",
            GateDLiveControllerPipeLossRetainsLeaseAsync);

        if (failures == 0)
        {
            output.WriteLine(
                "Watchdog Gate C lease/journal/named-pipe self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"Watchdog Gate C lease/journal/named-pipe self-test: FAIL ({failures} case(s))");
        return 1;
    }

    private static async Task<int> CaseAsync(
        TextWriter output,
        string name,
        Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            output.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                $"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task HappyStateMachineAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            Assert(prepared.Phase == WatchdogLeasePhase.Prepared);
            Assert((await env.Journal.LoadAsync(CancellationToken.None))?.Phase ==
                   WatchdogLeasePhase.Prepared);

            var armed =
                await env.Manager.WriteIntentAsync(
                    prepared.SessionId,
                    prepared.Generation,
                    new FanSetpoint(30, 30),
                    CancellationToken.None);

            Assert(armed.Phase == WatchdogLeasePhase.WriteArmed);
            Assert((await env.Journal.LoadAsync(CancellationToken.None))?.Phase ==
                   WatchdogLeasePhase.WriteArmed);

            env.Hardware.Set(new FanSetpoint(30, 30));

            var owned =
                await env.Manager.CommitAsync(
                    armed.SessionId,
                    armed.Generation,
                    new FanSetpoint(30, 30),
                    CancellationToken.None);

            Assert(owned.Phase == WatchdogLeasePhase.Owned);
            Assert(owned.Generation == 3);

            await env.Manager.HeartbeatAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            Assert(restoring.Phase == WatchdogLeasePhase.Restoring);

            env.Hardware.Set(new FanSetpoint(255, 255));

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task PreparedOwnerLossAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            var recovery =
                await env.Manager.HandleOwnerLossAsync(
                    Controller,
                    "synthetic process death",
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.ClearedPrepared);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task CancelPreparedAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(31, 31));

            await env.Manager.CancelPreparedAsync(
                prepared.SessionId,
                prepared.Generation,
                CancellationToken.None);

            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task AbortFirstWriteIntentAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var armed = await PrepareAndArmAsync(env, 30);

            var rolledBack =
                await env.Manager.AbortWriteIntentAsync(
                    armed.SessionId,
                    armed.Generation,
                    CancellationToken.None);

            Assert(rolledBack.Phase ==
                   WatchdogLeasePhase.Prepared);
            Assert(env.Hardware.RestoreCalls == 0);

            var journal =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(journal?.Phase ==
                   WatchdogLeasePhase.Prepared);
        });
    }

    private static async Task AbortTransitionWriteIntentAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var armed =
                await env.Manager.WriteIntentAsync(
                    owned.SessionId,
                    owned.Generation,
                    new FanSetpoint(40, 40),
                    CancellationToken.None);

            var rolledBack =
                await env.Manager.AbortWriteIntentAsync(
                    armed.SessionId,
                    armed.Generation,
                    CancellationToken.None);

            Assert(rolledBack.Phase ==
                   WatchdogLeasePhase.Owned);

            var journal =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(journal?.Owned ==
                   new FanSetpoint(30, 30));
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task UnsafeAbortWriteIntentAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var armed = await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(31, 31));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.AbortWriteIntentAsync(
                        armed.SessionId,
                        armed.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "ABORT_UNSAFE");

            var journal =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(journal?.Phase ==
                   WatchdogLeasePhase.WriteArmed);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
        });
    }

    private static async Task PreparedRestartAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ClearedPrepared);
            Assert(!recovery.RestoreAttempted);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task ServiceStopPreparedAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            var recovery =
                await env.Manager.RecoverForServiceStopAsync(
                    "synthetic service stop",
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.ClearedPrepared);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task ServiceStopOwnedAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareArmCommitAsync(env, 30);

            var recovery =
                await env.Manager.RecoverForServiceStopAsync(
                    "synthetic service stop",
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task RestartWriteArmedBeforeWriteAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var armed = await PrepareAndArmAsync(env, 30);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(armed.Phase == WatchdogLeasePhase.WriteArmed);
            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(recovery.RestoreAttempted);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task RestartAfterWriteBeforeCommitAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(30, 30));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
        });
    }

    private static async Task RestartAfterCommitAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            Assert(owned.Phase == WatchdogLeasePhase.Owned);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task RestartDuringRestoringAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            Assert(restoring.Phase == WatchdogLeasePhase.Restoring);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task TransitionAllowsPreviousOrPendingAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned30 = await PrepareArmCommitAsync(env, 30);

            var armed40 =
                await env.Manager.WriteIntentAsync(
                    owned30.SessionId,
                    owned30.Generation,
                    new FanSetpoint(40, 40),
                    CancellationToken.None);

            Assert(armed40.Phase == WatchdogLeasePhase.WriteArmed);

            env.Hardware.Set(new FanSetpoint(30, 30));
            var oldRecovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(oldRecovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);

            await env.ResetAsync();

            var ownedAgain = await PrepareArmCommitAsync(env, 30);
            var armedAgain =
                await env.Manager.WriteIntentAsync(
                    ownedAgain.SessionId,
                    ownedAgain.Generation,
                    new FanSetpoint(40, 40),
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(40, 40));

            var newRecovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(armedAgain.Phase == WatchdogLeasePhase.WriteArmed);
            Assert(newRecovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task StaleGenerationAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            var before =
                await env.Journal.LoadAsync(CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.WriteIntentAsync(
                        prepared.SessionId,
                        prepared.Generation - 1,
                        new FanSetpoint(30, 30),
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "STALE_GENERATION");

            var after =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(before == after);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task ReleaseNormalizesRestoreAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            // Simulate the controller reaching FF/FF but dying/being wrong
            // before LegacyDefault can be proven. Release must still execute
            // the watchdog restore primitive before deleting the journal.
            env.Hardware.Set(new FanSetpoint(255, 255));

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task ReleaseWaitsForDelayedFirmwareAckAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            // Model the physical HP behavior seen in Gate G2: the WMI restore
            // call has returned, but the EC still publishes the old owned
            // setpoint briefly before converging to FF/FF.
            env.Hardware.RestoreVisibilityDelayReads = 2;

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task ReleaseFailureRetainsLeaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(255, 255));
            env.Hardware.FailRestore = true;

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.ReleaseAsync(
                        restoring.SessionId,
                        restoring.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "RESTORE_NOT_VERIFIED");
            Assert(env.Hardware.RestoreCalls == 1);

            var journal =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(journal?.Phase ==
                   WatchdogLeasePhase.Restoring);
        });
    }

    private static async Task ControllerIdentityMismatchAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            var other =
                new ControllerIdentity(
                    Controller.ProcessId + 1,
                    Controller.ProcessStartUtcTicks + 1);

            var before =
                await env.Journal.LoadAsync(
                    CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.WriteIntentAsync(
                        prepared.SessionId,
                        prepared.Generation,
                        new FanSetpoint(30, 30),
                        CancellationToken.None,
                        other).AsTask());

            Assert(ex.Code == "CONTROLLER_MISMATCH");

            var after =
                await env.Journal.LoadAsync(
                    CancellationToken.None);

            Assert(before == after);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task OutOfRangeWriteIntentAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            var before =
                await env.Journal.LoadAsync(CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.WriteIntentAsync(
                        prepared.SessionId,
                        prepared.Generation,
                        new FanSetpoint(13, 51),
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "TARGET_OUT_OF_RANGE");

            var after =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(before == after);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task ReleaseTakesOverOwnedTargetAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            // Controller declared restore-begin but never changed EC.
            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task ReleasePreservesUnknownExternalOverrideAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(31, 31));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.ReleaseAsync(
                        restoring.SessionId,
                        restoring.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "RESTORE_NOT_VERIFIED");
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));

            var journal =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(journal?.Phase ==
                   WatchdogLeasePhase.Restoring);
        });
    }

    private static async Task DuplicateReleaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(255, 255));

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.ReleaseAsync(
                        restoring.SessionId,
                        restoring.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "NO_ACTIVE_LEASE");
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task ProbeDoesNotRenewHeartbeatAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            var before =
                await env.Journal.LoadAsync(CancellationToken.None);

            env.Clock.Advance(TimeSpan.FromSeconds(4));

            var probed = await env.Manager.ProbeAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            var afterProbe =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(probed.Phase == WatchdogLeasePhase.Owned);
            Assert(before == afterProbe);
            Assert(env.Hardware.RestoreCalls == 0);

            // Probe must not renew _lastHeartbeatMs. Two more seconds should
            // therefore cross the original 5 s OWNED deadline and restore.
            env.Clock.Advance(TimeSpan.FromSeconds(2));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery is not null);
            Assert(recovery!.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
        });
    }

    private static async Task HeartbeatDoesNotPersistAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            var before =
                await env.Journal.LoadAsync(CancellationToken.None);

            env.Clock.Advance(TimeSpan.FromSeconds(1));

            await env.Manager.HeartbeatAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            var after =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(before == after);
        });
    }

    private static async Task LateHeartbeatCannotReviveAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            env.Clock.Advance(TimeSpan.FromSeconds(6));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.HeartbeatAsync(
                        owned.SessionId,
                        owned.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "LEASE_EXPIRED");
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task LateWriteIntentCannotReviveAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            env.Clock.Advance(TimeSpan.FromSeconds(6));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.WriteIntentAsync(
                        owned.SessionId,
                        owned.Generation,
                        new FanSetpoint(40, 40),
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "LEASE_EXPIRED");
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task LateCommitCannotReviveAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var armed = await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(30, 30));
            env.Clock.Advance(TimeSpan.FromSeconds(13));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.CommitAsync(
                        armed.SessionId,
                        armed.Generation,
                        new FanSetpoint(30, 30),
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "LEASE_EXPIRED");
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task HeartbeatTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareArmCommitAsync(env, 30);
            env.Clock.Advance(TimeSpan.FromSeconds(6));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task WriteArmedTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(30, 30));
            env.Clock.Advance(TimeSpan.FromSeconds(13));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task RestoringTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            await env.Manager.RestoreBeginAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            env.Clock.Advance(TimeSpan.FromSeconds(9));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task AmbiguousOwnershipAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.OwnershipAmbiguous);
            Assert(recovery.JournalRetained);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is not null);
        });
    }

    private static async Task PrepareRefusesExternalOverrideAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            env.Hardware.Set(new FanSetpoint(31, 31));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.PrepareAsync(
                        Controller,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "EXTERNAL_OVERRIDE");
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
        });
    }

    private static async Task PreparedRestartPreservesExternalOverrideAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ExternalOverrideBlocked);
            Assert(!recovery.RestoreAttempted);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
        });
    }

    private static async Task ExternalOverrideWithoutLeaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.Manager.RecoverOnStartupAsync(
                    CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ExternalOverrideBlocked);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task RestoreFailureRetainsJournalAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareArmCommitAsync(env, 30);
            env.Hardware.FailRestore = true;

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoreFailed);
            Assert(recovery.RestoreAttempted);
            Assert(recovery.JournalRetained);
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is not null);
            Assert(env.Hardware.Current == new FanSetpoint(30, 30));
        });
    }

    private static async Task CorruptJournalAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(env.Journal.Path)!);

            await File.WriteAllTextAsync(
                env.Journal.Path,
                "{ definitely not valid JSON");

            env.Hardware.Set(new FanSetpoint(30, 30));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.JournalInvalid);
            Assert(recovery.JournalRetained);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task MalformedFrameAsync()
    {
        var payload =
            Encoding.UTF8.GetBytes("{broken");

        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, 4),
            payload.Length);
        payload.CopyTo(bytes.AsSpan(4));

        await using var stream =
            new MemoryStream(bytes, writable: false);

        var ex =
            await ThrowsAsync<FanControlWatchdogProtocolException>(
                () => FanControlWatchdogLeaseCodec
                    .ReadRequestAsync(
                        stream,
                        CancellationToken.None)
                    .AsTask());

        Assert(ex.Code == "MALFORMED_MESSAGE");
    }

    private static async Task NamedPipeIdentityMismatchAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var name =
                "VictusFanControl-GateC-" +
                Guid.NewGuid().ToString("N");

            await using var server =
                NewServer(name);

            var serverTask =
                GateCPipeServerSession.RunAsync(
                    server,
                    env.Manager,
                    CancellationToken.None);

            await using var client =
                new NamedPipeClientStream(
                    ".",
                    name,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

            await client.ConnectAsync(5000);

            var actual =
                WindowsNamedPipeIdentity.CurrentProcessIdentity();

            var hello = new FanControlWatchdogLeaseRequest(
                GateCProtocol.Version,
                Guid.NewGuid(),
                GateCProtocol.Hello,
                ControllerPid: actual.ProcessId + 1,
                ControllerStartUtcTicks:
                    actual.ProcessStartUtcTicks);

            await FanControlWatchdogLeaseCodec.WriteRequestAsync(
                client,
                hello,
                CancellationToken.None);

            var response =
                await FanControlWatchdogLeaseCodec.ReadResponseAsync(
                    client,
                    CancellationToken.None);

            Assert(response is not null);
            Assert(!response!.Ok);
            Assert(response.Code == "IDENTITY_MISMATCH");

            client.Dispose();
            await serverTask.ConfigureAwait(false);

            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task NamedPipeLossBeforeWriteIntentAckAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var name =
                "VictusFanControl-GateD-AckLoss-" +
                Guid.NewGuid().ToString("N");

            var writeIntentDispatched =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            var releaseResponse =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            await using var server =
                NewServer(name);

            var serverTask =
                GateCPipeServerSession.RunAsync(
                    server,
                    env.Manager,
                    CancellationToken.None,
                    log: null,
                    monitorControllerProcess: true,
                    beforeResponseAsync:
                        async (request, response, cancellationToken) =>
                        {
                            if (!string.Equals(
                                    request.Type,
                                    GateCProtocol.WriteIntent,
                                    StringComparison.Ordinal))
                            {
                                return;
                            }

                            Assert(
                                response.Ok,
                                $"Synthetic WriteIntent dispatch failed before response hook: {response.Code}: {response.Message}");

                            // Dispatch has completed here. Therefore the
                            // manager already performed the durable
                            // WRITE_ARMED StoreAsync, while the response has
                            // deliberately not yet been written to the pipe.
                            writeIntentDispatched.TrySetResult(true);

                            await releaseResponse.Task
                                .WaitAsync(cancellationToken)
                                .ConfigureAwait(false);
                        });

            var client =
                new NamedPipeClientStream(
                    ".",
                    name,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

            try
            {
                await client.ConnectAsync(5000);

                var actual =
                    WindowsNamedPipeIdentity.CurrentProcessIdentity();

                var hello =
                    await RoundTripAsync(
                        client,
                        new FanControlWatchdogLeaseRequest(
                            GateCProtocol.Version,
                            Guid.NewGuid(),
                            GateCProtocol.Hello,
                            ControllerPid: actual.ProcessId,
                            ControllerStartUtcTicks:
                                actual.ProcessStartUtcTicks));

                Assert(hello.Ok, "Hello was rejected.");

                var prepared =
                    await RoundTripAsync(
                        client,
                        Request(GateCProtocol.Prepare));

                Assert(prepared.Ok, "Prepare was rejected.");

                await FanControlWatchdogLeaseCodec.WriteRequestAsync(
                    client,
                    Request(
                        GateCProtocol.WriteIntent,
                        prepared.SessionId,
                        prepared.Generation,
                        30,
                        30),
                    CancellationToken.None);

                // Deterministic boundary: the server has completed DispatchAsync
                // (including durable WRITE_ARMED) but is intentionally blocked
                // before writing the ACK to the transport.
                await writeIntentDispatched.Task
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .ConfigureAwait(false);

                var armed =
                    await env.Journal.LoadAsync(
                        CancellationToken.None);

                Assert(
                    armed?.Phase == WatchdogLeasePhase.WriteArmed,
                    $"WRITE_ARMED was not durable at the pre-response boundary; phase={armed?.Phase.ToString() ?? "none"}.");

                // Drop only the transport while the exact controller process
                // remains alive. The server is then allowed to attempt the ACK,
                // which must fail as a transport loss rather than as owner death.
                client.Dispose();
                releaseResponse.TrySetResult(true);
            }
            finally
            {
                releaseResponse.TrySetResult(true);
                client.Dispose();
            }

            await serverTask.ConfigureAwait(false);

            var retained =
                await env.Journal.LoadAsync(
                    CancellationToken.None);

            Assert(
                env.Hardware.RestoreCalls == 0,
                $"Live-owner transport loss unexpectedly restored hardware {env.Hardware.RestoreCalls} time(s).");
            Assert(
                env.Hardware.Current.IsFirmwareOwned,
                $"Synthetic EC changed unexpectedly to {env.Hardware.Current}.");
            Assert(
                retained?.Phase == WatchdogLeasePhase.WriteArmed,
                $"Live-owner transport loss did not retain WRITE_ARMED; phase={retained?.Phase.ToString() ?? "none"}.");

            // If the still-live controller never reconnects/progresses, the
            // WRITE_ARMED deadline remains the fail-closed recovery boundary.
            env.Clock.Advance(
                WatchdogLeaseManager.WriteArmedDeadline +
                TimeSpan.FromMilliseconds(1));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(
                recovery?.Disposition ==
                LeaseRecoveryDisposition.RestoredFirmware,
                $"WRITE_ARMED deadline did not restore firmware; disposition={recovery?.Disposition.ToString() ?? "none"}.");
            Assert(
                env.Hardware.RestoreCalls == 1,
                $"WRITE_ARMED deadline restore count was {env.Hardware.RestoreCalls}, expected 1.");
            Assert(
                env.Hardware.Current.IsFirmwareOwned,
                $"WRITE_ARMED deadline left EC at {env.Hardware.Current}.");
            Assert(
                await env.Journal.LoadAsync(
                    CancellationToken.None) is null,
                "WRITE_ARMED deadline restored firmware but did not clear the durable journal.");
        });
    }

    private static async Task ProvenOwnerDeathWhileOwnedRestoresAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned =
                await PrepareArmCommitAsync(
                    env,
                    30);

            Assert(
                owned.Phase == WatchdogLeasePhase.Owned,
                $"Synthetic lease did not reach OWNED; phase={owned.Phase}.");
            Assert(
                env.Hardware.Current == new FanSetpoint(30, 30),
                $"Synthetic EC did not reach 30/30; observed={env.Hardware.Current}.");

            var recovery =
                await env.Manager.HandleOwnerLossAsync(
                    Controller,
                    "synthetic proven controller process exit",
                    CancellationToken.None);

            Assert(
                recovery?.Disposition ==
                LeaseRecoveryDisposition.RestoredFirmware,
                $"Proven owner death did not restore firmware; disposition={recovery?.Disposition.ToString() ?? "none"}.");
            Assert(
                env.Hardware.RestoreCalls == 1,
                $"Proven owner death restore count was {env.Hardware.RestoreCalls}, expected 1.");
            Assert(
                env.Hardware.Current.IsFirmwareOwned,
                $"Proven owner death left EC at {env.Hardware.Current}.");
            Assert(
                await env.Journal.LoadAsync(
                    CancellationToken.None) is null,
                "Proven owner death restored firmware but did not clear the durable journal.");
        });
    }

    private static async Task GateDLiveControllerPipeLossRetainsLeaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var firstPipeName =
                "VictusFanControl-GateD-Reconnect-" +
                Guid.NewGuid().ToString("N");

            FanControlWatchdogLeaseResponse owned;

            await using (var firstServer =
                NewServer(firstPipeName))
            {
                var firstServerTask =
                    GateCPipeServerSession.RunAsync(
                        firstServer,
                        env.Manager,
                        CancellationToken.None,
                        log: null,
                        monitorControllerProcess: true);

                await using (var firstClient =
                    new NamedPipeClientStream(
                        ".",
                        firstPipeName,
                        PipeDirection.InOut,
                        PipeOptions.Asynchronous))
                {
                    await firstClient.ConnectAsync(5000);

                    var actual =
                        WindowsNamedPipeIdentity.CurrentProcessIdentity();

                    var hello =
                        await RoundTripAsync(
                            firstClient,
                            new FanControlWatchdogLeaseRequest(
                                FanControlWatchdogLeaseContract.ProtocolVersion,
                                Guid.NewGuid(),
                                FanControlWatchdogLeaseContract.Hello,
                                ControllerPid: actual.ProcessId,
                                ControllerStartUtcTicks:
                                    actual.ProcessStartUtcTicks));

                    Assert(hello.Ok);

                    var prepared =
                        await RoundTripAsync(
                            firstClient,
                            Request(
                                FanControlWatchdogLeaseContract.Prepare));

                    Assert(prepared.Ok);

                    var armed =
                        await RoundTripAsync(
                            firstClient,
                            Request(
                                FanControlWatchdogLeaseContract.WriteIntent,
                                prepared.SessionId,
                                prepared.Generation,
                                30,
                                30));

                    Assert(armed.Ok);

                    env.Hardware.Set(new FanSetpoint(30, 30));

                    owned =
                        await RoundTripAsync(
                            firstClient,
                            Request(
                                FanControlWatchdogLeaseContract.Commit,
                                armed.SessionId,
                                armed.Generation,
                                30,
                                30));

                    Assert(owned.Ok);
                }

                await firstServerTask.ConfigureAwait(false);
            }

            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(30, 30));

            var retained =
                await env.Journal.LoadAsync(
                    CancellationToken.None);

            Assert(retained is not null);
            Assert(retained!.Phase ==
                   WatchdogLeasePhase.Owned);
            Assert(retained.SessionId == owned.SessionId);
            Assert(retained.Generation == owned.Generation);

            var secondPipeName =
                "VictusFanControl-GateD-Reconnect-" +
                Guid.NewGuid().ToString("N");

            await using var secondServer =
                NewServer(secondPipeName);

            var secondServerTask =
                GateCPipeServerSession.RunAsync(
                    secondServer,
                    env.Manager,
                    CancellationToken.None,
                    log: null,
                    monitorControllerProcess: true);

            await using (var secondClient =
                new NamedPipeClientStream(
                    ".",
                    secondPipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous))
            {
                await secondClient.ConnectAsync(5000);

                var actual =
                    WindowsNamedPipeIdentity.CurrentProcessIdentity();

                var hello =
                    await RoundTripAsync(
                        secondClient,
                        new FanControlWatchdogLeaseRequest(
                            FanControlWatchdogLeaseContract.ProtocolVersion,
                            Guid.NewGuid(),
                            FanControlWatchdogLeaseContract.Hello,
                            ControllerPid: actual.ProcessId,
                            ControllerStartUtcTicks:
                                actual.ProcessStartUtcTicks));

                Assert(hello.Ok);

                var probe =
                    await RoundTripAsync(
                        secondClient,
                        Request(
                            FanControlWatchdogLeaseContract.Probe,
                            owned.SessionId,
                            owned.Generation));

                Assert(probe.Ok);
                Assert(probe.SessionId == owned.SessionId);
                Assert(probe.Generation == owned.Generation);

                var heartbeat =
                    await RoundTripAsync(
                        secondClient,
                        Request(
                            FanControlWatchdogLeaseContract.Heartbeat,
                            owned.SessionId,
                            owned.Generation));

                Assert(heartbeat.Ok);
                Assert(heartbeat.SessionId == owned.SessionId);
                Assert(heartbeat.Generation == owned.Generation);

                var restoring =
                    await RoundTripAsync(
                        secondClient,
                        Request(
                            FanControlWatchdogLeaseContract.RestoreBegin,
                            owned.SessionId,
                            owned.Generation));

                Assert(restoring.Ok);

                var released =
                    await RoundTripAsync(
                        secondClient,
                        Request(
                            FanControlWatchdogLeaseContract.Release,
                            restoring.SessionId,
                            restoring.Generation));

                Assert(released.Ok);
            }

            await secondServerTask.ConfigureAwait(false);

            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(
                    CancellationToken.None) is null);
        });
    }

    private static NamedPipeServerStream NewServer(
        string name) =>
        new(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    private static FanControlWatchdogLeaseRequest Request(
        string type,
        Guid? sessionId = null,
        long? generation = null,
        int? cpu = null,
        int? gpu = null) =>
        new(
            GateCProtocol.Version,
            Guid.NewGuid(),
            type,
            SessionId: sessionId,
            Generation: generation,
            CpuLevel: cpu,
            GpuLevel: gpu);

    private static async Task<FanControlWatchdogLeaseResponse> RoundTripAsync(
        Stream stream,
        FanControlWatchdogLeaseRequest request)
    {
        await FanControlWatchdogLeaseCodec.WriteRequestAsync(
            stream,
            request,
            CancellationToken.None);

        return
            await FanControlWatchdogLeaseCodec.ReadResponseAsync(
                stream,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "Named-pipe server closed before sending a response.");
    }

    private static async Task<LeaseOperationResult> PrepareAndArmAsync(
        TestEnvironment env,
        byte level)
    {
        var prepared =
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

        return await env.Manager.WriteIntentAsync(
            prepared.SessionId,
            prepared.Generation,
            new FanSetpoint(level, level),
            CancellationToken.None);
    }

    private static async Task<LeaseOperationResult> PrepareArmCommitAsync(
        TestEnvironment env,
        byte level)
    {
        var armed =
            await PrepareAndArmAsync(env, level);

        env.Hardware.Set(new FanSetpoint(level, level));

        return await env.Manager.CommitAsync(
            armed.SessionId,
            armed.Generation,
            new FanSetpoint(level, level),
            CancellationToken.None);
    }

    private static async Task WithEnvironmentAsync(
        Func<TestEnvironment, Task> action)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-GateC-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var env = new TestEnvironment(root);
            await action(env).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(
        bool condition,
        string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message ?? "Assertion failed.");
        }
    }

    private sealed class TestEnvironment
    {
        public TestEnvironment(string root)
        {
            Journal =
                new JsonLeaseJournal(
                    Path.Combine(root, "lease.json"));

            Hardware = new FakeHardware();
            Clock = new FakeClock();
            Manager = NewManager();
        }

        public JsonLeaseJournal Journal { get; }
        public FakeHardware Hardware { get; }
        public FakeClock Clock { get; }
        public WatchdogLeaseManager Manager { get; private set; }

        public WatchdogLeaseManager RestartManager()
        {
            Manager = NewManager();
            return Manager;
        }

        public async Task ResetAsync()
        {
            await Journal.DeleteAsync(CancellationToken.None);
            Hardware.Reset();
            Clock.Reset();
            Manager = NewManager();
        }

        private WatchdogLeaseManager NewManager() =>
            new(
                Journal,
                Hardware,
                Clock);
    }

    private sealed class FakeHardware : ILeaseRecoveryHardware
    {
        public FanSetpoint Current { get; private set; } =
            new(255, 255);

        public int RestoreCalls { get; private set; }

        public bool FailRestore { get; set; }

        public int RestoreVisibilityDelayReads { get; set; }

        private int _remainingRestoreVisibilityReads;

        public ValueTask<FanSetpoint> ReadSetpointAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_remainingRestoreVisibilityReads > 0)
            {
                _remainingRestoreVisibilityReads--;
                if (_remainingRestoreVisibilityReads == 0)
                {
                    Current = new FanSetpoint(255, 255);
                }
            }

            return ValueTask.FromResult(Current);
        }

        public ValueTask RestoreFirmwareAutoAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCalls++;

            if (FailRestore)
            {
                throw new InvalidOperationException(
                    "synthetic restore failure");
            }

            if (RestoreVisibilityDelayReads > 0)
            {
                _remainingRestoreVisibilityReads =
                    RestoreVisibilityDelayReads;
            }
            else
            {
                Current = new FanSetpoint(255, 255);
            }

            return ValueTask.CompletedTask;
        }

        public void Set(FanSetpoint setpoint) =>
            Current = setpoint;

        public void Reset()
        {
            Current = new FanSetpoint(255, 255);
            RestoreCalls = 0;
            FailRestore = false;
            RestoreVisibilityDelayReads = 0;
            _remainingRestoreVisibilityReads = 0;
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public ulong Milliseconds { get; private set; }

        public void Advance(TimeSpan duration) =>
            Milliseconds += checked((ulong)duration.TotalMilliseconds);

        public void Reset() =>
            Milliseconds = 0;
    }
}
