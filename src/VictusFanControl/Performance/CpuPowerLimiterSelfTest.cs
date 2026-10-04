using VictusFanControl.Runtime;

namespace VictusFanControl.Performance;

internal static class CpuPowerLimiterSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        try
        {
            Require(
                CpuPowerConflictPolicySelfTest.Run(output) == 0,
                "bounded external-writer conflict policy");

            Require(
                CpuPowerSessionJournalSelfTest.Run(output) == 0,
                "durable CPU power session journal");

            Require(
                CpuPowerRecoveryPlannerSelfTest.Run(output) == 0,
                "CPU power recovery-only planner");

            Require(
                CpuPowerRecoveryExecutorSelfTest.Run(output) == 0,
                "CPU power recovery executor");

            Require(
                CpuPowerPresetPolicySelfTest.Run(output) == 0,
                "CPU power AC/battery preset selector");

            Require(
                PerformancePowerSourceQuerySelfTest.Run(output) == 0,
                "Windows performance power-source query mapping");

            Require(
                GpuClockPresetPolicySelfTest.Run(output) == 0,
                "GPU clock AC/battery preset selector");

            Require(
                GpuClockLimitBackendSelfTest.Run(output) == 0,
                "GPU NVML locked-clock backend contract");

            Require(
                GpuClockSessionJournalSelfTest.Run(output) == 0,
                "GPU clock durable session journal");

            Require(
                GpuClockSessionControllerSelfTest.Run(output) == 0,
                "GPU clock ActiveUnverified session controller");

            Require(
                GpuClockPresetTransitionControllerSelfTest.Run(output) == 0,
                "GPU clock confirmed-source transition controller");

            Require(
                GpuPowerLimitQualificationSelfTest.Run(output) == 0,
                "GPU NVML power-limit read qualification");

            Require(
                GpuPowerFieldQualificationSelfTest.Run(output) == 0,
                "GPU NVML requested power-limit field qualification");

            NormalApplyVerifyRelease(output);
            OwnedPresetSwitchIsOneJournaledWrite(output);
            ConfirmedSourceChangeUsesOwnedSwitchOnly(output);
            DisabledTargetConditionallyReleases(output);
            UnknownSourceReleasesPresetAuthority(output);
            SourceChangeCannotBypassContested(output);
            EnabledSourceCannotApplyFromDisabled(output);
            PresetSwitchPreservesExternalHandoff(output);
            PresetSwitchJournalArmFailurePreventsWrite(output);
            PresetSwitchReadbackMismatchRetainsArmedJournal(output);
            NonOwnedMutationIsPreservedWithoutConflict(output);
            SuccessfulReacquireRestoresExternalHandoff(output);
            EvolvingExternalValueBecomesLatestHandoff(output);
            FiveRejectedReacquiresYieldWithoutBaselineOverwrite(output);
            ExistingJournalBlocksNewApply(output);
            InitialJournalArmFailurePreventsWrite(output);
            OwnedJournalFailureLeavesWriteArmedAndNoSecondWrite(output);
            ReacquireJournalArmFailurePreventsWrite(output);
            RestoreJournalArmFailurePreventsRestoreWrite(output);
            LockWhileOwnedFailsWithoutAnotherWrite(output);
            InitialRejectedApplyRestoresBaseline(output);
            UnsupportedBackendNeverWrites(output);

            output.WriteLine(
                "CPU power limiter P2B journaled ownership self-test: PASS (no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power limiter P2B journaled ownership self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void NormalApplyVerifyRelease(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.State ==
            CpuPowerLimiterState.Disabled,
            "starts disabled");

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "apply 20/40");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Owned,
            "exact apply leaves durable Owned");

        Require(
            backend.WriteCount == 1,
            "single apply write");

        Require(
            limiter.VerifyActive(),
            "verify exact active raw");

        Require(
            backend.WriteCount == 1,
            "normal verify never rewrites");

        Require(
            limiter.Release(),
            "normal release");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Disabled,
            "disabled after release");

        Require(
            backend.WriteCount == 2,
            "one apply plus one restore");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "original baseline restored");

        Require(
            journal.Current is null,
            "resolved release deletes durable journal");

        Require(
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.WriteArmed) &&
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.Owned) &&
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.Restoring),
            "apply and restore were durably armed");

        output.WriteLine(
            "PASS journal precedes normal apply and restore writes");
    }


    private static void OwnedPresetSwitchIsOneJournaledWrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "preset switch fixture apply");

        var baseline =
            limiter.Baseline;

        Require(
            limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(15, 30)),
            "AC -> Battery owned switch");

        Require(
            backend.WriteCount == 2 &&
            backend.Raw == FakeBackend.BatteryRaw,
            "AC -> Battery adds exactly one write");

        Require(
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.PresetSwitchWriteArmed),
            "preset transition was durably armed");

        Require(
            journal.Current?.Phase ==
                CpuPowerJournalPhase.Owned &&
            journal.Current.Request ==
                new CpuPowerLimitRequest(15, 30) &&
            journal.Current.AppliedRaw ==
                FakeBackend.BatteryRaw,
            "new Battery preset becomes durable Owned only after readback");

        Require(
            limiter.Baseline == baseline,
            "OriginalBaseline remains immutable across switch");

        Require(
            limiter.ReacquireAttemptsUsed == 0,
            "successful source switch starts a fresh conflict budget");

        Require(
            limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(20, 40)),
            "Battery -> AC owned switch");

        Require(
            backend.WriteCount == 3 &&
            backend.Raw == FakeBackend.AppliedRaw,
            "Battery -> AC also adds exactly one write");

        Require(
            limiter.Release(),
            "release after two preset switches");

        Require(
            backend.WriteCount == 4 &&
            backend.Raw == FakeBackend.BaselineRaw,
            "release occurs only after switches, never between them");

        output.WriteLine(
            "PASS AC<->Battery owned preset switch is journaled and uses one write per transition");
    }


    private static void ConfirmedSourceChangeUsesOwnedSwitchOnly(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "source switch fixture starts with explicit AC ownership");

        var controller =
            new CpuPowerPresetTransitionController(
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(true, 20, 40),
                        new CpuPowerPreset(true, 15, 30))),
                limiter);

        var result =
            controller.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            result.Succeeded &&
            result.Disposition ==
                CpuPowerPresetTransitionDisposition.EnabledPresetSwitched,
            "confirmed Battery source performs owned switch");

        Require(
            backend.WriteCount == 2 &&
            backend.Raw == FakeBackend.BatteryRaw,
            "source transition performs exactly one additional write");

        Require(
            journal.Current?.Phase ==
                CpuPowerJournalPhase.Owned &&
            journal.Current.Request ==
                new CpuPowerLimitRequest(15, 30),
            "Battery request is durable Owned after exact readback");

        output.WriteLine(
            "PASS confirmed AC->Battery source change maps to one owned-to-owned write");
    }

    private static void DisabledTargetConditionallyReleases(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "disabled target fixture starts owned");

        var controller =
            new CpuPowerPresetTransitionController(
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(true, 20, 40),
                        new CpuPowerPreset(false, 15, 30))),
                limiter);

        var result =
            controller.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            result.Succeeded &&
            result.Disposition ==
                CpuPowerPresetTransitionDisposition.DisabledPresetReleased,
            "disabled Battery preset requests conditional release");

        Require(
            limiter.State ==
                CpuPowerLimiterState.Disabled &&
            backend.WriteCount == 2 &&
            backend.Raw == FakeBackend.BaselineRaw &&
            journal.Current is null,
            "disabled target releases once to baseline and closes session");

        output.WriteLine(
            "PASS disabled destination preset performs conditional release instead of inventing a cap");
    }

    private static void UnknownSourceReleasesPresetAuthority(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "unknown source fixture starts owned");

        var controller =
            new CpuPowerPresetTransitionController(
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(true, 20, 40),
                        new CpuPowerPreset(true, 15, 30))),
                limiter);

        var result =
            controller.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Unknown);

        Require(
            result.Succeeded &&
            result.Disposition ==
                CpuPowerPresetTransitionDisposition.SourceUnknownReleased,
            "unknown source gives up preset authority");

        Require(
            backend.WriteCount == 2 &&
            backend.Raw == FakeBackend.BaselineRaw &&
            limiter.State == CpuPowerLimiterState.Disabled,
            "unknown source uses release, never an AC/Battery preset switch");

        output.WriteLine(
            "PASS unknown power source fails closed by releasing existing CPU preset authority");
    }

    private static void SourceChangeCannotBypassContested(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "contested source fixture starts owned");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive() &&
            limiter.State ==
                CpuPowerLimiterState.Contested,
            "external writer establishes Contested");

        var controller =
            new CpuPowerPresetTransitionController(
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(true, 20, 40),
                        new CpuPowerPreset(true, 15, 30))),
                limiter);

        var writesBefore =
            backend.WriteCount;

        var result =
            controller.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            !result.Succeeded &&
            result.Disposition ==
                CpuPowerPresetTransitionDisposition.BlockedByLimiterState &&
            limiter.State ==
                CpuPowerLimiterState.Contested,
            "Battery source cannot bypass Contested");

        Require(
            backend.WriteCount ==
                writesBefore &&
            backend.Raw ==
                FakeBackend.ExternalRaw,
            "blocked source change preserves external owner with zero writes");

        output.WriteLine(
            "PASS enabled source change cannot bypass Contested external ownership");
    }

    private static void EnabledSourceCannotApplyFromDisabled(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        var controller =
            new CpuPowerPresetTransitionController(
                new CpuPowerPresetPolicy(
                    new CpuPowerPresetSet(
                        new CpuPowerPreset(true, 20, 40),
                        new CpuPowerPreset(true, 15, 30))),
                limiter);

        var result =
            controller.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Ac);

        Require(
            !result.Succeeded &&
            result.Disposition ==
                CpuPowerPresetTransitionDisposition.NoActiveOwnership,
            "source detection alone cannot start a limiter session");

        Require(
            limiter.State ==
                CpuPowerLimiterState.Disabled &&
            backend.WriteCount == 0 &&
            journal.Current is null,
            "startup/source detection remains no-write without explicit authority");

        output.WriteLine(
            "PASS enabled AC preset cannot self-apply from Disabled/startup state");
    }

    private static void PresetSwitchPreservesExternalHandoff(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "handoff switch fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "external handoff detected");

        clock.Advance(
            TimeSpan.FromSeconds(30));

        Require(
            limiter.VerifyActive(),
            "bounded reacquire succeeds");

        clock.Advance(
            TimeSpan.FromSeconds(60));

        Require(
            limiter.VerifyActive() &&
            limiter.State == CpuPowerLimiterState.Active,
            "reacquire stabilizes before preset switch");

        Require(
            limiter.ExternalHandoff?.Raw ==
            FakeBackend.ExternalRaw,
            "external handoff captured before switch");

        Require(
            limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(15, 30)),
            "switch after stable reacquire");

        Require(
            limiter.ExternalHandoff?.Raw ==
                FakeBackend.ExternalRaw &&
            journal.Current?.ExternalHandoff?.Raw ==
                FakeBackend.ExternalRaw,
            "ExternalHandoff survives owned-to-owned switch");

        Require(
            limiter.Release(),
            "release after handoff-preserving switch");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "final release returns to external handoff, not stale baseline");

        output.WriteLine(
            "PASS preset switch preserves ExternalHandoff as final release target");
    }

    private static void PresetSwitchJournalArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "switch arm failure fixture apply");

        journal.FailStoreFromAttempt =
            journal.StoreAttempts + 1;

        Require(
            !limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(15, 30)),
            "failed PresetSwitchWriteArmed is surfaced");

        Require(
            backend.WriteCount == 1 &&
            backend.Raw == FakeBackend.AppliedRaw,
            "failed switch journal arm causes zero transition writes");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose cannot bypass persistent journal failure");

        output.WriteLine(
            "PASS failed PresetSwitchWriteArmed persistence blocks the switch write");
    }

    private static void PresetSwitchReadbackMismatchRetainsArmedJournal(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                RejectPresetSwitch = true
            };

        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "switch mismatch fixture apply");

        Require(
            !limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(15, 30)),
            "switch readback mismatch fails closed");

        Require(
            backend.WriteCount == 2,
            "failed switch makes only its single armed write attempt");

        Require(
            backend.Raw == FakeBackend.AppliedRaw,
            "old VFC-owned preset remains physically present");

        Require(
            journal.Current?.Phase ==
                CpuPowerJournalPhase.PresetSwitchWriteArmed &&
            journal.Current.AppliedRaw ==
                FakeBackend.AppliedRaw &&
            journal.Current.PendingRaw ==
                FakeBackend.BatteryRaw,
            "ambiguous switch preserves old+new raw in durable journal");

        output.WriteLine(
            "PASS switch mismatch retains PresetSwitchWriteArmed for recovery-only classification");
    }

    private static void NonOwnedMutationIsPreservedWithoutConflict(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "metadata fixture apply");

        backend.Raw =
            FakeBackend.AppliedMetadataRaw;

        Require(
            limiter.VerifyActive(),
            "non-owned mutation keeps requested power owned");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Active,
            "non-owned mutation stays Active");

        Require(
            limiter.AppliedRaw ==
            FakeBackend.AppliedMetadataRaw,
            "limiter adopts exact raw containing external metadata");

        Require(
            journal.Current?.AppliedRaw ==
            FakeBackend.AppliedMetadataRaw,
            "durable Owned tracks adopted raw");

        Require(
            backend.WriteCount == 1,
            "non-owned mutation causes no write");

        Require(
            limiter.Release(),
            "metadata fixture release");

        output.WriteLine(
            "PASS non-owned changes are adopted durably without false contention");
    }

    private static void SuccessfulReacquireRestoresExternalHandoff(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "reacquire fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "external power change detected");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Contested,
            "external change enters Contested");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Contested,
            "conflict is durable before any retry");

        Require(
            backend.WriteCount == 1,
            "no immediate write on conflict");

        clock.Advance(
            TimeSpan.FromSeconds(30));

        Require(
            limiter.VerifyActive(),
            "30 s allows bounded reacquire");

        Require(
            limiter.State ==
            CpuPowerLimiterState.ReacquiredPendingStability,
            "exact reacquire is provisional");

        Require(
            limiter.ReacquireAttemptsUsed == 1,
            "attempt 1 is recorded");

        Require(
            backend.Raw ==
            FakeBackend.ReacquiredRaw,
            "reacquire raw is applied");

        Require(
            journal.StoredPhases.Contains(
                CpuPowerJournalPhase.ReacquireWriteArmed),
            "reacquire write was durably armed");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Stability,
            "exact readback transitions durable journal to Stability");

        clock.Advance(
            TimeSpan.FromSeconds(60));

        Require(
            limiter.VerifyActive(),
            "60 s closes stability");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Active,
            "stable reacquire returns Active");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Owned,
            "stable reacquire returns durable journal to Owned");

        Require(
            limiter.Release(),
            "release after successful reacquire");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "release returns control to external handoff");

        Require(
            journal.Current is null,
            "handoff release deletes journal after exact readback");

        output.WriteLine(
            "PASS reacquire is armed durably and later restores external handoff");
    }

    private static void EvolvingExternalValueBecomesLatestHandoff(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "evolving fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "first external value detected");

        clock.Advance(
            TimeSpan.FromSeconds(20));

        backend.Raw =
            FakeBackend.External2Raw;

        Require(
            !limiter.VerifyActive(),
            "second external value observed");

        Require(
            journal.Current?.ExternalHandoff?.Raw ==
            FakeBackend.External2Raw,
            "latest external handoff is persisted");

        clock.Advance(
            TimeSpan.FromSeconds(10));

        Require(
            limiter.VerifyActive(),
            "original 30 s deadline still permits attempt");

        Require(
            backend.Raw ==
            FakeBackend.Reacquired2Raw,
            "reacquire plan uses latest external snapshot");

        Require(
            limiter.Release(),
            "release evolving fixture");

        Require(
            backend.Raw ==
            FakeBackend.External2Raw,
            "latest external value is restored");

        output.WriteLine(
            "PASS evolving external handoff is durable without postponing retry");
    }

    private static void FiveRejectedReacquiresYieldWithoutBaselineOverwrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                RejectReacquire = true
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "yield fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "yield fixture conflict detected");

        for (var attempt = 1;
             attempt <= 5;
             attempt++)
        {
            clock.Advance(
                TimeSpan.FromSeconds(30));

            Require(
                !limiter.VerifyActive(),
                $"reacquire attempt {attempt} rejected");

            Require(
                limiter.ReacquireAttemptsUsed ==
                attempt,
                $"attempt {attempt} counted");
        }

        Require(
            limiter.State ==
            CpuPowerLimiterState.Yielded,
            "fifth rejection yields");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Yielded,
            "Yielded state is durable");

        Require(
            backend.WriteCount == 6,
            "initial apply plus exactly five reacquire writes");

        clock.Advance(
            TimeSpan.FromMinutes(10));

        Require(
            !limiter.VerifyActive(),
            "Yielded remains read-only");

        Require(
            backend.WriteCount == 6,
            "there is no automatic sixth write");

        Require(
            !limiter.SwitchOwnedPreset(
                new CpuPowerLimitRequest(15, 30)),
            "Yielded source switch cannot reacquire authority");

        Require(
            limiter.State ==
                CpuPowerLimiterState.Yielded &&
            backend.WriteCount == 6,
            "Yielded source switch preserves state and performs zero writes");

        Require(
            limiter.Release(),
            "yielded session releases");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "yield release preserves external owner");

        Require(
            backend.WriteCount == 6,
            "yield release performs no stale baseline write");

        output.WriteLine(
            "PASS exactly five durable retries then Yielded with no sixth write");
    }

    private static void ExistingJournalBlocksNewApply(
        TextWriter output)
    {
        var journal = new FakeJournal();
        journal.Seed(
            MakeWriteArmedRecord());

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "unresolved journal blocks new Apply");

        Require(
            backend.WriteCount == 0,
            "unresolved journal causes zero writes");

        Require(
            limiter.LastError?.Contains(
                "UNRESOLVED_JOURNAL",
                StringComparison.Ordinal) == true,
            "unresolved journal is explicit");

        output.WriteLine(
            "PASS unresolved durable session blocks a new authority episode");
    }

    private static void InitialJournalArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 1
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "write-arm journal failure rejects Apply");

        Require(
            backend.WriteCount == 0,
            "journal failure before Apply produces zero writes");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "baseline remains unchanged");

        output.WriteLine(
            "PASS failed WriteArmed persistence blocks the hardware write");
    }

    private static void OwnedJournalFailureLeavesWriteArmedAndNoSecondWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 2
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "Owned journal failure is surfaced");

        Require(
            backend.WriteCount == 1,
            "initial write happened only after WriteArmed");

        Require(
            backend.Raw ==
            FakeBackend.AppliedRaw,
            "hardware result remains observable");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.WriteArmed,
            "last durable state remains conservative WriteArmed");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose cannot issue restore when Restoring cannot be persisted");

        output.WriteLine(
            "PASS post-write journal failure leaves WriteArmed and forbids an unjournaled restore");
    }

    private static void ReacquireJournalArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 4
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "reacquire-arm fixture apply");

        backend.Raw =
            FakeBackend.ExternalRaw;

        Require(
            !limiter.VerifyActive(),
            "conflict journal succeeds");

        clock.Advance(
            TimeSpan.FromSeconds(30));

        Require(
            !limiter.VerifyActive(),
            "reacquire arm failure is surfaced");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Failed,
            "lost durability fails closed");

        Require(
            backend.WriteCount == 1,
            "failed ReacquireWriteArmed persistence causes no retry write");

        Require(
            backend.Raw ==
            FakeBackend.ExternalRaw,
            "external owner is preserved");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose also cannot write while journal store remains unavailable");

        output.WriteLine(
            "PASS ReacquireWriteArmed failure prevents the reacquisition write");
    }

    private static void RestoreJournalArmFailurePreventsRestoreWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailStoreFromAttempt = 3
            };

        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();
        var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "restore-arm fixture apply");

        Require(
            !limiter.Release(),
            "restore journal failure is surfaced");

        Require(
            backend.WriteCount == 1,
            "Restoring must persist before restore write");

        Require(
            backend.Raw ==
            FakeBackend.AppliedRaw,
            "unarmed restore leaves current hardware untouched");

        limiter.Dispose();

        Require(
            backend.WriteCount == 1,
            "Dispose never bypasses failed Restoring persistence");

        output.WriteLine(
            "PASS failed Restoring persistence blocks the restore write");
    }

    private static void LockWhileOwnedFailsWithoutAnotherWrite(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend = new FakeBackend(journal);
        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "lock fixture apply");

        backend.Raw =
            FakeBackend.LockedAppliedRaw;

        Require(
            !limiter.VerifyActive(),
            "lock appearance detected");

        Require(
            limiter.State ==
            CpuPowerLimiterState.Failed,
            "lock while owned is unresolved Failed");

        Require(
            journal.Current?.Phase ==
            CpuPowerJournalPhase.Unresolved,
            "lock while owned is durable Unresolved");

        Require(
            backend.WriteCount == 1,
            "lock path never writes");

        Require(
            !limiter.Release(),
            "locked owned value cannot be falsely released");

        Require(
            backend.WriteCount == 1,
            "release does not clear lock or rewrite RAPL");

        output.WriteLine(
            "PASS lock while owned becomes durable Unresolved with no bypass write");
    }

    private static void InitialRejectedApplyRestoresBaseline(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                RejectApply = true
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "rejected readback returns false");

        Require(
            backend.Raw ==
            FakeBackend.BaselineRaw,
            "rejected apply baseline retained");

        Require(
            backend.WriteCount == 1,
            "rejected hardware saw only attempted apply");

        Require(
            journal.Current is null,
            "no unresolved journal remains when baseline is confirmed");

        output.WriteLine(
            "PASS rejected initial write resolves journal when baseline remains exact");
    }

    private static void UnsupportedBackendNeverWrites(
        TextWriter output)
    {
        var journal = new FakeJournal();
        var backend =
            new FakeBackend(journal)
            {
                IsSupported = false
            };

        var clock = new FakeActiveTimeClock();

        using var limiter =
            new CpuPowerLimiter(
                backend,
                journal,
                clock);

        Require(
            limiter.State ==
            CpuPowerLimiterState.Unsupported,
            "unsupported state");

        Require(
            !limiter.Apply(
                new CpuPowerLimitRequest(20, 40)),
            "unsupported apply denied");

        Require(
            backend.WriteCount == 0,
            "unsupported path never writes");

        Require(
            journal.StoreAttempts == 0,
            "unsupported path never arms a journal");

        output.WriteLine(
            "PASS unsupported backend remains fully write-closed");
    }

    private static CpuPowerSessionJournalRecord
        MakeWriteArmedRecord()
    {
        var now = DateTimeOffset.UtcNow;

        return new CpuPowerSessionJournalRecord(
            CpuPowerSessionJournalRecord.CurrentSchemaVersion,
            TargetProfileId,
            Guid.NewGuid(),
            1,
            CpuPowerJournalPhase.WriteArmed,
            new CpuPowerLimitSnapshot(
                FakeBackend.BaselineRaw,
                45,
                115,
                false),
            new CpuPowerLimitRequest(
                20,
                40),
            FakeBackend.AppliedRaw,
            null,
            new CpuPowerConflictSnapshot(
                CpuPowerConflictState.Inactive,
                0,
                CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
                false,
                null,
                null,
                0,
                0),
            FakeBackend.AppliedRaw,
            now,
            now);
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeActiveTimeClock :
        IActiveTimeClock
    {
        public ulong Milliseconds { get; private set; }

        internal void Advance(
            TimeSpan duration)
        {
            Milliseconds =
                checked(
                    Milliseconds +
                    ActiveTimeClock.TimeoutMilliseconds(
                        duration));
        }
    }

    private sealed class FakeJournal :
        ICpuPowerSessionJournal
    {
        internal CpuPowerSessionJournalRecord? Current;
        internal int StoreAttempts;
        internal int DeleteAttempts;
        internal int? FailStoreFromAttempt;
        internal readonly List<CpuPowerJournalPhase>
            StoredPhases = new();

        public string Path =>
            "fake://cpu-power-session";

        public string TargetProfileId =>
            CpuPowerLimiterSelfTest.TargetProfileId;

        public CpuPowerSessionJournalRecord? Load() =>
            Current;

        public void Store(
            CpuPowerSessionJournalRecord record)
        {
            StoreAttempts++;

            if (FailStoreFromAttempt.HasValue &&
                StoreAttempts >=
                FailStoreFromAttempt.Value)
            {
                throw new IOException(
                    "simulated durable journal failure");
            }

            Current = record;
            StoredPhases.Add(
                record.Phase);
        }

        public void Delete()
        {
            DeleteAttempts++;
            Current = null;
        }

        internal void Seed(
            CpuPowerSessionJournalRecord record)
        {
            Current = record;
        }
    }

    private sealed class FakeBackend :
        ICpuPowerLimitBackend
    {
        internal const ulong BaselineRaw = 0x1000;
        internal const ulong AppliedRaw = 0x2000;
        internal const ulong AppliedMetadataRaw = 0x2100;
        internal const ulong ExternalRaw = 0x3000;
        internal const ulong External2Raw = 0x3100;
        internal const ulong ReacquiredRaw = 0x4000;
        internal const ulong Reacquired2Raw = 0x4100;
        internal const ulong BatteryRaw = 0x5000;
        internal const ulong LockedAppliedRaw = 0xA000;

        private readonly FakeJournal _journal;

        internal FakeBackend(
            FakeJournal journal)
        {
            _journal = journal;
        }

        internal ulong Raw = BaselineRaw;
        internal int WriteCount;
        internal bool RejectApply;
        internal bool RejectReacquire;
        internal bool RejectPresetSwitch;

        public bool IsSupported { get; set; } = true;

        public CpuPowerLimitSnapshot Read() =>
            Snapshot(Raw);

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request)
        {
            if (baseline.Raw != BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40)
            {
                throw new InvalidOperationException(
                    "unexpected fake initial plan request");
            }

            return new CpuPowerLimitApplyPlan(
                AppliedRaw,
                20,
                40);
        }

        public bool OwnedFieldsMatch(
            ulong expectedRaw,
            CpuPowerLimitSnapshot current)
        {
            var expected =
                Snapshot(expectedRaw);

            return expected.Pl1Watts ==
                       current.Pl1Watts &&
                   expected.Pl2Watts ==
                       current.Pl2Watts;
        }

        public CpuPowerLimitApplyPlan BuildReacquirePlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current)
        {
            if (originalBaseline.Raw !=
                    BaselineRaw ||
                request.Pl1Watts != 20 ||
                request.Pl2Watts != 40 ||
                current.Locked)
            {
                throw new InvalidOperationException(
                    "unexpected fake reacquire plan request");
            }

            var raw =
                current.Raw ==
                External2Raw
                    ? Reacquired2Raw
                    : ReacquiredRaw;

            return new CpuPowerLimitApplyPlan(
                raw,
                20,
                40);
        }

        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current)
        {
            if (originalBaseline.Raw !=
                    BaselineRaw ||
                current.Locked)
            {
                throw new InvalidOperationException(
                    "unexpected fake owned transition plan request");
            }

            if (request ==
                new CpuPowerLimitRequest(15, 30))
            {
                return new CpuPowerLimitApplyPlan(
                    BatteryRaw,
                    15,
                    30);
            }

            if (request ==
                new CpuPowerLimitRequest(20, 40))
            {
                return new CpuPowerLimitApplyPlan(
                    AppliedRaw,
                    20,
                    40);
            }

            throw new InvalidOperationException(
                "unsupported fake preset transition request");
        }

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (current.Raw ==
                restoreTarget.Raw)
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "ALREADY_TARGET");
            }

            if (current.Locked)
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "RESTORE_BLOCKED_LOCK");
            }

            if (OwnedFieldsMatch(
                    appliedRaw,
                    current))
            {
                return new CpuPowerLimitRestorePlan(
                    restoreTarget.Raw,
                    "RESTORE_PLANNED");
            }

            return new CpuPowerLimitRestorePlan(
                current.Raw,
                "EXTERNAL_CHANGE_PRESERVED");
        }

        public void Write(
            ulong raw)
        {
            var expectedPhase =
                raw switch
                {
                    BatteryRaw =>
                        CpuPowerJournalPhase.PresetSwitchWriteArmed,

                    AppliedRaw
                        when _journal.Current?.Phase ==
                             CpuPowerJournalPhase.PresetSwitchWriteArmed =>
                        CpuPowerJournalPhase.PresetSwitchWriteArmed,

                    AppliedRaw =>
                        CpuPowerJournalPhase.WriteArmed,

                    ReacquiredRaw or
                    Reacquired2Raw =>
                        CpuPowerJournalPhase.ReacquireWriteArmed,

                    _ =>
                        CpuPowerJournalPhase.Restoring
                };

            if (_journal.Current?.Phase !=
                expectedPhase)
            {
                throw new InvalidOperationException(
                    $"hardware write 0x{raw:X} occurred without durable {expectedPhase}");
            }

            if (_journal.Current?.PendingRaw !=
                raw)
            {
                throw new InvalidOperationException(
                    $"hardware write 0x{raw:X} does not match durable PendingRaw");
            }

            WriteCount++;

            if (RejectApply &&
                raw == AppliedRaw)
            {
                return;
            }

            if (RejectReacquire &&
                raw is
                    ReacquiredRaw or
                    Reacquired2Raw)
            {
                return;
            }

            if (RejectPresetSwitch &&
                raw == BatteryRaw)
            {
                return;
            }

            Raw = raw;
        }

        private static CpuPowerLimitSnapshot
            Snapshot(
                ulong raw) =>
            raw switch
            {
                BaselineRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        45,
                        115,
                        false),

                AppliedRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                AppliedMetadataRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                ExternalRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        30,
                        60,
                        false),

                External2Raw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        35,
                        70,
                        false),

                ReacquiredRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                Reacquired2Raw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        false),

                BatteryRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        15,
                        30,
                        false),

                LockedAppliedRaw =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        20,
                        40,
                        true),

                _ =>
                    new CpuPowerLimitSnapshot(
                        raw,
                        30,
                        60,
                        false)
            };
    }
}
