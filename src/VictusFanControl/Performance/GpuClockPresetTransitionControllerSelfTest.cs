namespace VictusFanControl.Performance;

internal static class GpuClockPresetTransitionControllerSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        try
        {
            ConfirmedAcToBatteryIsOneDirectSet(output);
            SameEnabledSourceIsZeroWrite(output);
            DisabledDestinationReleasesNormally(output);
            UnknownSourceReleasesNormally(output);
            SourceDetectionCannotApplyFromDisabled(output);
            RecoveryRequiredBlocksSourceTransition(output);

            output.WriteLine(
                "GPU clock confirmed-source transition self-test: PASS (no startup Apply, direct AC/Battery switch, normal release only, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock confirmed-source transition self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void ConfirmedAcToBatteryIsOneDirectSet(
        TextWriter output)
    {
        using var fixture =
            Fixture.Create(
                GpuClockPresetSet.UserRequestedVictus);

        Require(
            fixture.Session.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "explicit AC session apply");

        var beforeSets =
            fixture.Backend.SetCalls;

        var result =
            fixture.Transitions
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Battery);

        Require(
            result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.EnabledPresetSwitched,
            "AC to Battery transition result");

        Require(
            fixture.Backend.SetCalls ==
                beforeSets + 1 &&
            fixture.Backend.ResetCalls == 0,
            "AC to Battery uses one direct Set and no Reset");

        Require(
            fixture.Session.CurrentRequest ==
                new GpuClockLimitRequest(
                    210,
                    1200),
            "Battery request becomes active intent");

        output.WriteLine(
            "PASS confirmed AC->Battery GPU transition is one direct 210..1200 Set");
    }

    private static void SameEnabledSourceIsZeroWrite(
        TextWriter output)
    {
        using var fixture =
            Fixture.Create(
                GpuClockPresetSet.UserRequestedVictus);

        Require(
            fixture.Session.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "explicit AC session apply");

        var beforeSets =
            fixture.Backend.SetCalls;

        var result =
            fixture.Transitions
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Ac);

        Require(
            result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.EnabledPresetUnchanged &&
            fixture.Backend.SetCalls ==
                beforeSets &&
            fixture.Backend.ResetCalls == 0,
            "same AC preset is zero-write");

        output.WriteLine(
            "PASS confirmed source matching active GPU preset performs zero writes");
    }

    private static void DisabledDestinationReleasesNormally(
        TextWriter output)
    {
        using var fixture =
            Fixture.Create(
                new GpuClockPresetSet(
                    Ac:
                        new GpuClockPreset(
                            true,
                            210,
                            1850),
                    Battery:
                        new GpuClockPreset(
                            false,
                            210,
                            1200)));

        Require(
            fixture.Session.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "explicit AC session apply");

        var result =
            fixture.Transitions
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Battery);

        Require(
            result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.DisabledPresetReleased,
            "disabled Battery releases");

        Require(
            fixture.Backend.ResetCalls == 1 &&
            fixture.Session.State ==
                GpuClockSessionState.Disabled,
            "disabled destination performs one normal Reset");

        output.WriteLine(
            "PASS disabled GPU destination performs the journaled normal release");
    }

    private static void UnknownSourceReleasesNormally(
        TextWriter output)
    {
        using var fixture =
            Fixture.Create(
                GpuClockPresetSet.UserRequestedVictus);

        Require(
            fixture.Session.Apply(
                new GpuClockLimitRequest(
                    210,
                    1850)),
            "explicit AC session apply");

        var result =
            fixture.Transitions
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Unknown);

        Require(
            result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.UnknownSourceReleased &&
            fixture.Backend.ResetCalls == 1,
            "Unknown source releases active exclusive session");

        output.WriteLine(
            "PASS Unknown source gives up GPU clock authority through normal release");
    }

    private static void SourceDetectionCannotApplyFromDisabled(
        TextWriter output)
    {
        using var fixture =
            Fixture.Create(
                GpuClockPresetSet.UserRequestedVictus);

        var result =
            fixture.Transitions
                .HandleConfirmedSourceChange(
                    PerformancePowerSourceKind.Ac);

        Require(
            result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.NoActiveSession,
            "Disabled source detection is a no-op");

        Require(
            fixture.Backend.SetCalls == 0 &&
            fixture.Backend.ResetCalls == 0 &&
            fixture.Session.State ==
                GpuClockSessionState.Disabled,
            "source detection has zero startup authority");

        output.WriteLine(
            "PASS confirmed AC detection cannot create a GPU clock session from Disabled");
    }

    private static void RecoveryRequiredBlocksSourceTransition(
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

        using var session =
            new GpuClockSessionController(
                backend,
                journal);

        var transitions =
            new GpuClockPresetTransitionController(
                new GpuClockPresetPolicy(
                    GpuClockPresetSet.UserRequestedVictus),
                session);

        var result =
            transitions.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            !result.Succeeded &&
            result.Disposition ==
                GpuClockPresetTransitionDisposition.BlockedBySessionState &&
            session.State ==
                GpuClockSessionState.RecoveryRequired,
            "RecoveryRequired source transition blocked");

        Require(
            backend.SetCalls == 0 &&
            backend.ResetCalls == 0,
            "RecoveryRequired cannot use source change to write");

        output.WriteLine(
            "PASS stale/crash recovery state blocks AC/Battery GPU writes");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private sealed class Fixture :
        IDisposable
    {
        private Fixture(
            FakeJournal journal,
            FakeBackend backend,
            GpuClockSessionController session,
            GpuClockPresetTransitionController transitions)
        {
            Journal = journal;
            Backend = backend;
            Session = session;
            Transitions = transitions;
        }

        internal FakeJournal Journal { get; }

        internal FakeBackend Backend { get; }

        internal GpuClockSessionController Session { get; }

        internal GpuClockPresetTransitionController Transitions { get; }

        internal static Fixture Create(
            GpuClockPresetSet presets)
        {
            var journal =
                new FakeJournal();

            var backend =
                new FakeBackend(
                    journal);

            var session =
                new GpuClockSessionController(
                    backend,
                    journal);

            var transitions =
                new GpuClockPresetTransitionController(
                    new GpuClockPresetPolicy(
                        presets),
                    session);

            return new Fixture(
                journal,
                backend,
                session,
                transitions);
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }

    private sealed class FakeBackend :
        IGpuClockLimitBackend
    {
        private readonly FakeJournal _journal;

        internal FakeBackend(
            FakeJournal journal)
        {
            _journal = journal;
        }

        public GpuClockBackendCapabilities Capabilities =>
            new(
                true,
                true,
                true,
                false,
                false,
                false,
                true);

        internal int SetCalls;
        internal int ResetCalls;

        public GpuClockBackendWriteResult SetLockedGraphicsClocks(
            GpuClockLimitRequest request)
        {
            SetCalls++;

            if (_journal.Record?.Phase is not
                    GpuClockJournalPhase.ApplyWriteArmed and not
                    GpuClockJournalPhase.PresetSwitchWriteArmed)
            {
                throw new InvalidOperationException(
                    "GPU Set occurred without durable write-armed journal.");
            }

            return new GpuClockBackendWriteResult(
                true,
                GpuClockBackendFailureKind.None,
                0,
                "FAKE_SET_ACCEPTED");
        }

        public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
        {
            ResetCalls++;

            if (_journal.Record?.Phase !=
                GpuClockJournalPhase.ReleaseWriteArmed)
            {
                throw new InvalidOperationException(
                    "GPU Reset occurred without durable release-armed journal.");
            }

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
    }

    private sealed class FakeJournal :
        IGpuClockSessionJournal
    {
        public string Path =>
            "memory://gpu-clock-transition-journal";

        public string TargetProfileId =>
            GpuClockPresetTransitionControllerSelfTest
                .TargetProfileId;

        internal GpuClockSessionJournalRecord?
            Record;

        public GpuClockSessionJournalRecord? Load() =>
            Record;

        public void Store(
            GpuClockSessionJournalRecord record)
        {
            Record = record;
        }

        public void Delete()
        {
            Record = null;
        }
    }
}
