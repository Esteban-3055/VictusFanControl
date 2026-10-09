namespace VictusFanControl.Performance;

internal static class GpuClockSessionControllerSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        try
        {
            ApplyArmsJournalBeforeSingleSet(output);
            ApplyArmFailurePreventsWrite(output);
            PresetSwitchUsesOneSetAndNoReset(output);
            SamePresetSwitchIsNoop(output);
            ReleaseArmsBeforeSingleReset(output);
            ReleaseArmFailurePreventsReset(output);
            StaleJournalRequiresRecoveryWithoutWrite(output);
            AuthorityLossPersistsRecoveryWithoutWrite(output);
            DisposeNeverResets(output);

            output.WriteLine(
                "GPU clock session controller self-test: PASS (ActiveUnverified, exclusive-controller contract, no polling/reacquire, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock session controller self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void ApplyArmsJournalBeforeSingleSet(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        var request =
            new GpuClockLimitRequest(
                210,
                1850);

        Require(
            controller.Apply(request),
            "GPU apply succeeds");

        Require(
            backend.SetCalls == 1 &&
            backend.ResetCalls == 0,
            "GPU apply uses exactly one set and no reset");

        Require(
            backend.PhaseObservedAtSet ==
                GpuClockJournalPhase.ApplyWriteArmed,
            "apply journal is durable before set");

        Require(
            controller.State ==
                GpuClockSessionState.ActiveUnverified &&
            controller.CurrentRequest ==
                request,
            "successful set enters ActiveUnverified");

        Require(
            journal.Record?.Phase ==
                GpuClockJournalPhase.ActiveUnverified,
            "active-unverified state is durable");

        output.WriteLine(
            "PASS GPU apply journals first, issues one Set and enters ActiveUnverified");
    }

    private static void ApplyArmFailurePreventsWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal
            {
                FailNextStore = true
            };

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            !controller.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "apply arm failure reported");

        Require(
            backend.SetCalls == 0 &&
            backend.ResetCalls == 0,
            "journal arm failure performs zero writes");

        output.WriteLine(
            "PASS GPU apply journal failure blocks the native Set");
    }

    private static void PresetSwitchUsesOneSetAndNoReset(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        var ac =
            new GpuClockLimitRequest(
                210,
                1850);

        var battery =
            new GpuClockLimitRequest(
                210,
                1200);

        Require(
            controller.Apply(ac),
            "AC apply");

        backend.ClearObservedPhase();

        Require(
            controller.SwitchPreset(
                battery),
            "AC to Battery switch");

        Require(
            backend.SetCalls == 2 &&
            backend.ResetCalls == 0,
            "switch adds one Set and never resets baseline");

        Require(
            backend.PhaseObservedAtSet ==
                GpuClockJournalPhase.PresetSwitchWriteArmed,
            "preset switch durable before Set");

        Require(
            controller.CurrentRequest ==
                battery &&
            controller.State ==
                GpuClockSessionState.ActiveUnverified,
            "Battery becomes active-unverified intent");

        output.WriteLine(
            "PASS AC->Battery GPU preset switch is one direct journaled Set with no reset");
    }

    private static void SamePresetSwitchIsNoop(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        var request =
            new GpuClockLimitRequest(
                210,
                1850);

        Require(
            controller.Apply(request),
            "apply for no-op test");

        Require(
            controller.SwitchPreset(request),
            "same preset returns success");

        Require(
            backend.SetCalls == 1 &&
            backend.ResetCalls == 0,
            "same preset performs no additional write");

        output.WriteLine(
            "PASS same GPU preset switch is a zero-write no-op");
    }

    private static void ReleaseArmsBeforeSingleReset(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            controller.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "apply before release");

        Require(
            controller.Release(),
            "normal release succeeds");

        Require(
            backend.ResetCalls == 1,
            "normal release performs exactly one reset");

        Require(
            backend.PhaseObservedAtReset ==
                GpuClockJournalPhase.ReleaseWriteArmed,
            "release journal is durable before reset");

        Require(
            journal.Record is null &&
            controller.State ==
                GpuClockSessionState.Disabled,
            "normal release deletes journal and returns Disabled");

        output.WriteLine(
            "PASS normal in-process GPU release journals first and issues one Reset");
    }

    private static void ReleaseArmFailurePreventsReset(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            controller.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "apply before release journal fault");

        journal.FailNextStore =
            true;

        Require(
            !controller.Release(),
            "release arm failure reported");

        Require(
            backend.ResetCalls == 0,
            "release arm failure performs zero resets");

        Require(
            controller.State ==
                GpuClockSessionState.ActiveUnverified,
            "failed release arm preserves active session");

        output.WriteLine(
            "PASS GPU release journal failure prevents blind reset");
    }

    private static void StaleJournalRequiresRecoveryWithoutWrite(
        TextWriter output)
    {
        var now =
            DateTimeOffset.UtcNow;

        var journal =
            new FakeJournal
            {
                Record =
                    new GpuClockSessionJournalRecord(
                        GpuClockSessionJournalRecord.CurrentSchemaVersion,
                        TargetProfileId,
                        Guid.NewGuid(),
                        2,
                        GpuClockJournalPhase.ActiveUnverified,
                        new GpuClockLimitRequest(
                            210,
                            1850),
                        PendingRequest: null,
                        RecoveryReason: null,
                        CreatedAtUtc: now,
                        UpdatedAtUtc: now)
            };

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            controller.State ==
                GpuClockSessionState.RecoveryRequired,
            "stale journal enters recovery-required");

        Require(
            backend.SetCalls == 0 &&
            backend.ResetCalls == 0,
            "restart recovery performs zero automatic writes");

        output.WriteLine(
            "PASS stale GPU journal is evidence only; restart never auto-resets or reapplies");
    }

    private static void AuthorityLossPersistsRecoveryWithoutWrite(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        using var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            controller.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "apply before authority invalidation");

        var setCalls =
            backend.SetCalls;

        Require(
            !controller.MarkAuthorityUnknown(
                "SUSPEND_OR_DRIVER_RESET"),
            "authority invalidation is not a successful active operation");

        Require(
            controller.State ==
                GpuClockSessionState.RecoveryRequired &&
            journal.Record?.Phase ==
                GpuClockJournalPhase.RecoveryRequired,
            "authority invalidation persists recovery-required");

        Require(
            backend.SetCalls == setCalls &&
            backend.ResetCalls == 0,
            "authority invalidation performs zero GPU writes");

        output.WriteLine(
            "PASS suspend/driver-reset invalidates GPU authority without Set/Reset");
    }

    private static void DisposeNeverResets(
        TextWriter output)
    {
        var journal =
            new FakeJournal();

        var backend =
            new FakeBackend(
                journal);

        var controller =
            new GpuClockSessionController(
                backend,
                journal);

        Require(
            controller.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "apply before dispose");

        controller.Dispose();

        Require(
            backend.ResetCalls == 0,
            "Dispose must never become a hidden recovery reset");

        output.WriteLine(
            "PASS disposing GPU controller performs zero implicit reset writes");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class FakeBackend :
        IGpuClockLimitBackend
    {
        private readonly FakeJournal _journal;

        internal FakeBackend(
            FakeJournal journal)
        {
            _journal =
                journal;
        }

        public GpuClockBackendCapabilities Capabilities =>
            new(
                SetLockedGraphicsClocksExportAvailable: true,
                ResetLockedGraphicsClocksExportAvailable: true,
                CurrentGraphicsClockExportAvailable: true,
                ApplicationGraphicsClockTargetExportAvailable: false,
                CurrentClocksEventReasonsExportAvailable: false,
                ExactLockedRangeReadbackAvailable: false,
                HardwareWritesAuthorized: true);

        internal int SetCalls;
        internal int ResetCalls;

        internal GpuClockJournalPhase?
            PhaseObservedAtSet;

        internal GpuClockJournalPhase?
            PhaseObservedAtReset;

        public GpuClockBackendWriteResult SetLockedGraphicsClocks(
            GpuClockLimitRequest request)
        {
            SetCalls++;

            PhaseObservedAtSet =
                _journal.Record?.Phase;

            return new GpuClockBackendWriteResult(
                true,
                GpuClockBackendFailureKind.None,
                0,
                "FAKE_SET_ACCEPTED");
        }

        public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
        {
            ResetCalls++;

            PhaseObservedAtReset =
                _journal.Record?.Phase;

            return new GpuClockBackendWriteResult(
                true,
                GpuClockBackendFailureKind.None,
                0,
                "FAKE_RESET_ACCEPTED");
        }

        public GpuClockBackendObservation ReadObservation() =>
            new(
                true,
                210,
                null,
                null,
                null,
                false,
                GpuClockBackendFailureKind.None,
                0,
                "FAKE");

        internal void ClearObservedPhase()
        {
            PhaseObservedAtSet =
                null;

            PhaseObservedAtReset =
                null;
        }
    }

    private sealed class FakeJournal :
        IGpuClockSessionJournal
    {
        public string Path =>
            "memory://gpu-clock-journal";

        public string TargetProfileId =>
            GpuClockSessionControllerSelfTest
                .TargetProfileId;

        internal GpuClockSessionJournalRecord?
            Record;

        internal bool FailNextStore;

        public GpuClockSessionJournalRecord? Load() =>
            Record;

        public void Store(
            GpuClockSessionJournalRecord record)
        {
            if (FailNextStore)
            {
                FailNextStore = false;

                throw new IOException(
                    "synthetic GPU journal store failure");
            }

            Record =
                record;
        }

        public void Delete()
        {
            Record =
                null;
        }
    }
}
