using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>
/// CPU-only Guardian adapter used by the Step 6G qualification.
///
/// Initial Apply, AC/Battery switches and release all flow through the existing
/// CpuPowerLimiter journal/ownership machinery. GPU=true is rejected before
/// any CPU write.
/// </summary>
internal sealed class QualifiedCpuGuardianDomainLifecycle :
    IGuardianDomainLifecycle,
    ICpuPowerSourceTransitionSink,
    IDisposable
{
    private readonly CountingCpuPowerLimitBackend _backend;
    private readonly CpuPowerLimiter _limiter;
    private readonly CpuPowerPresetPolicy _policy;
    private readonly CpuPowerPresetTransitionController _transition;
    private readonly PerformancePowerSourceKind? _requiredInitialSource;

    private int _enableCalls;
    private int _releaseCalls;
    private bool _lastCpuEnabled;
    private bool _lastGpuEnabled;
    private PerformancePowerSourceKind? _lastInitialSource;
    private string? _lastReleaseReason;
    private string? _lastStatus;
    private bool _disposed;

    internal QualifiedCpuGuardianDomainLifecycle(
        ICpuPowerLimitBackend backend,
        ICpuPowerSessionJournal journal,
        CpuPowerPresetSet presets,
        PerformancePowerSourceKind? requiredInitialSource = null)
    {
        _backend =
            new CountingCpuPowerLimitBackend(
                backend ??
                throw new ArgumentNullException(
                    nameof(backend)));

        _limiter =
            new CpuPowerLimiter(
                _backend,
                journal ??
                throw new ArgumentNullException(
                    nameof(journal)));

        _policy =
            new CpuPowerPresetPolicy(
                presets);

        _transition =
            new CpuPowerPresetTransitionController(
                _policy,
                _limiter);

        _requiredInitialSource =
            requiredInitialSource;
    }

    public GuardianDomainLifecycleSnapshot Snapshot =>
        new(
            EnableCalls:
                _enableCalls,
            ReleaseCalls:
                _releaseCalls,
            LastCpuEnabled:
                _lastCpuEnabled,
            LastGpuEnabled:
                _lastGpuEnabled,
            LastInitialSource:
                _lastInitialSource,
            LastReleaseReason:
                _lastReleaseReason,
            CpuHardwareWriteAttempts:
                _backend.WriteAttempts,
            GpuHardwareWriteAttempts:
                0,
            CpuState:
                _limiter.State.ToString(),
            GpuState:
                null,
            CpuStatus:
                _lastStatus ??
                _limiter.LastError,
            GpuStatus:
                null);

    public ValueTask EnableAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        PerformancePowerSourceKind initialSource,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _lastCpuEnabled =
            cpuEnabled;

        _lastGpuEnabled =
            gpuEnabled;

        _lastInitialSource =
            initialSource;

        if (!cpuEnabled ||
            gpuEnabled)
        {
            throw new InvalidOperationException(
                "Step 6G CPU qualification accepts exactly CPU=true and GPU=false.");
        }

        if (_limiter.State !=
            CpuPowerLimiterState.Disabled)
        {
            throw new InvalidOperationException(
                "CPU Guardian session is not Disabled before explicit enable: " +
                _limiter.State);
        }

        if (_requiredInitialSource.HasValue &&
            initialSource !=
                _requiredInitialSource.Value)
        {
            throw new InvalidOperationException(
                "Step 6G qualification initial source must be " +
                _requiredInitialSource.Value +
                "; observed " +
                initialSource +
                ".");
        }

        var selection =
            _policy.Resolve(
                initialSource);

        if (!selection.SourceKnown ||
            !selection.Enabled ||
            !selection.Request.HasValue)
        {
            throw new InvalidOperationException(
                "Initial confirmed source does not authorize an enabled CPU preset: " +
                selection.Status);
        }

        if (!_limiter.Apply(
                selection.Request.Value))
        {
            _lastStatus =
                _limiter.LastError;

            throw new InvalidOperationException(
                "Initial Guardian CPU Apply failed: " +
                (_limiter.LastError ??
                 "unknown CPU limiter failure"));
        }

        _enableCalls++;

        _lastStatus =
            "GUARDIAN_CPU_INITIAL_PRESET_APPLIED__" +
            initialSource.ToString().ToUpperInvariant();

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        string reason,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        _lastCpuEnabled =
            cpuEnabled;

        _lastGpuEnabled =
            gpuEnabled;

        _lastReleaseReason =
            reason;

        if (gpuEnabled)
        {
            throw new InvalidOperationException(
                "Step 6G CPU qualification cannot release an unqualified GPU domain.");
        }

        if (!cpuEnabled)
        {
            _releaseCalls++;
            _lastStatus =
                "GUARDIAN_CPU_RELEASE_NO_CPU_DOMAIN";

            return ValueTask.CompletedTask;
        }

        if (_limiter.State ==
            CpuPowerLimiterState.Disabled)
        {
            _releaseCalls++;
            _lastStatus =
                "GUARDIAN_CPU_ALREADY_DISABLED";

            return ValueTask.CompletedTask;
        }

        if (_limiter.State is
            CpuPowerLimiterState.Unsupported or
            CpuPowerLimiterState.Applying or
            CpuPowerLimiterState.Recovering or
            CpuPowerLimiterState.Failed)
        {
            throw new InvalidOperationException(
                "Guardian CPU normal release is blocked from state " +
                _limiter.State +
                "; durable recovery evidence must be preserved.");
        }

        if (!_limiter.Release())
        {
            _lastStatus =
                _limiter.LastError;

            throw new InvalidOperationException(
                "Guardian CPU normal release failed: " +
                (_limiter.LastError ??
                 "unknown CPU release failure"));
        }

        _releaseCalls++;

        _lastStatus =
            "GUARDIAN_CPU_RELEASED_TO_PRESESSION_TARGET";

        return ValueTask.CompletedTask;
    }

    public CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source)
    {
        ThrowIfDisposed();

        var result =
            _transition.HandleConfirmedSourceChange(
                source);

        _lastStatus =
            result.Status;

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Deliberately do not call CpuPowerLimiter.Dispose here. That method
        // contains legacy best-effort recovery semantics. Guardian cleanup must
        // happen explicitly through ReleaseAsync so one host cleanup request
        // maps to one auditable release attempt and failures leave the journal.
        if (_backend.Inner is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _disposed =
            true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    private sealed class CountingCpuPowerLimitBackend :
        ICpuPowerLimitBackend
    {
        internal CountingCpuPowerLimitBackend(
            ICpuPowerLimitBackend inner)
        {
            Inner =
                inner;
        }

        internal ICpuPowerLimitBackend Inner { get; }

        internal int WriteAttempts { get; private set; }

        public bool IsSupported =>
            Inner.IsSupported;

        public CpuPowerLimitSnapshot Read() =>
            Inner.Read();

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request) =>
            Inner.BuildApplyPlan(
                baseline,
                request);

        public CpuPowerLimitApplyPlan BuildReacquirePlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            Inner.BuildReacquirePlan(
                originalBaseline,
                request,
                current);

        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            Inner.BuildOwnedTransitionPlan(
                originalBaseline,
                request,
                current);

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current) =>
            Inner.PlanRestore(
                restoreTarget,
                appliedRaw,
                current);

        public bool OwnedFieldsMatch(
            ulong expectedRaw,
            CpuPowerLimitSnapshot current) =>
            Inner.OwnedFieldsMatch(
                expectedRaw,
                current);

        public void Write(
            ulong raw)
        {
            WriteAttempts++;

            Inner.Write(
                raw);
        }
    }
}

internal static class QualifiedCpuGuardianDomainLifecycleSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-6G-CpuDomain-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(
            root);

        try
        {
            NormalAcBatteryAcRelease(
                root,
                output);

            GpuSelectionIsRejectedBeforeWrite(
                root,
                output);

            UnknownInitialSourceIsRejectedBeforeWrite(
                root,
                output);

            RequiredInitialAcRejectsBatteryBeforeWrite(
                root,
                output);

            output.WriteLine(
                "Step 6G CPU Guardian domain self-test: PASS (journaled AC 35/60 -> Battery 8/15 -> AC 35/60 -> baseline restore; fake backend only).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6G CPU Guardian domain self-test: FAIL - " +
                ex);

            return 1;
        }
        finally
        {
            try
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private static void NormalAcBatteryAcRelease(
        string root,
        TextWriter output)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                Path.Combine(
                    root,
                    "normal-cpu-journal.json"),
                TargetProfileId);

        var backend =
            new FakeCpuBackend(
                journal);

        using var lifecycle =
            new QualifiedCpuGuardianDomainLifecycle(
                backend,
                journal,
                CpuPowerProductDefaults.CreateDefaultPresetSet());

        lifecycle.EnableAsync(
                cpuEnabled: true,
                gpuEnabled: false,
                PerformancePowerSourceKind.Ac,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Require(
            backend.Raw ==
                FakeCpuBackend.AcRaw &&
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                1,
            "explicit CPU-only enable applies AC 35/60 exactly once");

        var battery =
            lifecycle.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            battery.Succeeded &&
            backend.Raw ==
                FakeCpuBackend.BatteryRaw &&
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                2,
            "AC to Battery uses one journaled 8/15 write");

        var ac =
            lifecycle.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Ac);

        Require(
            ac.Succeeded &&
            backend.Raw ==
                FakeCpuBackend.AcRaw &&
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                3,
            "Battery to AC uses one journaled 35/60 write");

        lifecycle.ReleaseAsync(
                cpuEnabled: true,
                gpuEnabled: false,
                "SELF_TEST",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var snapshot =
            lifecycle.Snapshot;

        Require(
            backend.Raw ==
                FakeCpuBackend.BaselineRaw &&
            snapshot.CpuHardwareWriteAttempts ==
                4 &&
            snapshot.CpuState ==
                CpuPowerLimiterState.Disabled.ToString() &&
            !File.Exists(
                journal.Path),
            "normal Guardian release performs one baseline restore and clears journal");

        Require(
            backend.PhasesSeenAtWrite.SequenceEqual(
                new[]
                {
                    CpuPowerJournalPhase.WriteArmed,
                    CpuPowerJournalPhase.PresetSwitchWriteArmed,
                    CpuPowerJournalPhase.PresetSwitchWriteArmed,
                    CpuPowerJournalPhase.Restoring
                }),
            "every CPU mutation observes the required durable journal phase first");

        output.WriteLine(
            "PASS Step 6G CPU domain AC 35/60 -> Battery 8/15 -> AC 35/60 -> one final restore");
    }

    private static void GpuSelectionIsRejectedBeforeWrite(
        string root,
        TextWriter output)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                Path.Combine(
                    root,
                    "gpu-reject-cpu-journal.json"),
                TargetProfileId);

        var backend =
            new FakeCpuBackend(
                journal);

        using var lifecycle =
            new QualifiedCpuGuardianDomainLifecycle(
                backend,
                journal,
                CpuPowerProductDefaults.CreateDefaultPresetSet());

        RequireThrows<InvalidOperationException>(
            () =>
                lifecycle.EnableAsync(
                        cpuEnabled: true,
                        gpuEnabled: true,
                        PerformancePowerSourceKind.Ac,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "CPU+GPU selection must be rejected by CPU-only gate");

        Require(
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                0 &&
            !File.Exists(
                journal.Path),
            "combined selection rejection performs zero writes");

        output.WriteLine(
            "PASS Step 6G first CPU gate refuses combined CPU+GPU authority");
    }

    private static void UnknownInitialSourceIsRejectedBeforeWrite(
        string root,
        TextWriter output)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                Path.Combine(
                    root,
                    "unknown-cpu-journal.json"),
                TargetProfileId);

        var backend =
            new FakeCpuBackend(
                journal);

        using var lifecycle =
            new QualifiedCpuGuardianDomainLifecycle(
                backend,
                journal,
                CpuPowerProductDefaults.CreateDefaultPresetSet());

        RequireThrows<InvalidOperationException>(
            () =>
                lifecycle.EnableAsync(
                        cpuEnabled: true,
                        gpuEnabled: false,
                        PerformancePowerSourceKind.Unknown,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "Unknown source must be rejected");

        Require(
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                0 &&
            !File.Exists(
                journal.Path),
            "Unknown source performs zero CPU writes");

        output.WriteLine(
            "PASS Step 6G CPU initial Unknown is fail-closed with zero writes");
    }

    private static void RequiredInitialAcRejectsBatteryBeforeWrite(
        string root,
        TextWriter output)
    {
        var journal =
            new JsonCpuPowerSessionJournal(
                Path.Combine(
                    root,
                    "initial-source-constraint-cpu-journal.json"),
                TargetProfileId);

        var backend =
            new FakeCpuBackend(
                journal);

        using var lifecycle =
            new QualifiedCpuGuardianDomainLifecycle(
                backend,
                journal,
                CpuPowerProductDefaults.CreateDefaultPresetSet(),
                requiredInitialSource:
                    PerformancePowerSourceKind.Ac);

        RequireThrows<InvalidOperationException>(
            () =>
                lifecycle.EnableAsync(
                        cpuEnabled: true,
                        gpuEnabled: false,
                        PerformancePowerSourceKind.Battery,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
            "qualification-only AC startup constraint must reject Battery");

        Require(
            lifecycle.Snapshot.CpuHardwareWriteAttempts ==
                0 &&
            !File.Exists(
                journal.Path),
            "initial-source constraint rejects before CPU writes/journal");

        output.WriteLine(
            "PASS Step 6G qualification-only initial AC constraint rejects Battery before writes");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private static void RequireThrows<T>(
        Action action,
        string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(
            label);
    }

    private sealed class FakeCpuBackend :
        ICpuPowerLimitBackend
    {
        internal const ulong BaselineRaw =
            0x1000;

        internal const ulong AcRaw =
            0x2000;

        internal const ulong BatteryRaw =
            0x3000;

        private readonly ICpuPowerSessionJournal _journal;

        internal FakeCpuBackend(
            ICpuPowerSessionJournal journal)
        {
            _journal =
                journal;
        }

        internal ulong Raw =
            BaselineRaw;

        internal List<CpuPowerJournalPhase> PhasesSeenAtWrite { get; } =
            new();

        public bool IsSupported =>
            true;

        public CpuPowerLimitSnapshot Read() =>
            Raw switch
            {
                BaselineRaw =>
                    new CpuPowerLimitSnapshot(
                        BaselineRaw,
                        45,
                        115,
                        false),

                AcRaw =>
                    new CpuPowerLimitSnapshot(
                        AcRaw,
                        35,
                        60,
                        false),

                BatteryRaw =>
                    new CpuPowerLimitSnapshot(
                        BatteryRaw,
                        8,
                        15,
                        false),

                _ =>
                    throw new InvalidOperationException(
                        "unexpected fake CPU raw")
            };

        public CpuPowerLimitApplyPlan BuildApplyPlan(
            CpuPowerLimitSnapshot baseline,
            CpuPowerLimitRequest request)
        {
            Require(
                baseline.Raw ==
                    BaselineRaw,
                "fake initial baseline");

            return Plan(
                request);
        }

        public CpuPowerLimitApplyPlan BuildReacquirePlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            Plan(
                request);

        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(
            CpuPowerLimitSnapshot originalBaseline,
            CpuPowerLimitRequest request,
            CpuPowerLimitSnapshot current) =>
            Plan(
                request);

        public CpuPowerLimitRestorePlan PlanRestore(
            CpuPowerLimitSnapshot restoreTarget,
            ulong appliedRaw,
            CpuPowerLimitSnapshot current)
        {
            if (!OwnedFieldsMatch(
                    appliedRaw,
                    current))
            {
                return new CpuPowerLimitRestorePlan(
                    current.Raw,
                    "EXTERNAL");
            }

            return new CpuPowerLimitRestorePlan(
                restoreTarget.Raw,
                "RESTORE");
        }

        public bool OwnedFieldsMatch(
            ulong expectedRaw,
            CpuPowerLimitSnapshot current) =>
            SnapshotForRaw(
                expectedRaw).Pl1Watts ==
                    current.Pl1Watts &&
            SnapshotForRaw(
                expectedRaw).Pl2Watts ==
                    current.Pl2Watts;

        public void Write(
            ulong raw)
        {
            var record =
                _journal.Load() ??
                throw new InvalidOperationException(
                    "CPU write occurred without a durable journal");

            PhasesSeenAtWrite.Add(
                record.Phase);

            if (record.PendingRaw !=
                raw)
            {
                throw new InvalidOperationException(
                    "CPU write does not match durable PendingRaw");
            }

            Raw =
                raw;
        }

        private static CpuPowerLimitApplyPlan Plan(
            CpuPowerLimitRequest request) =>
            request switch
            {
                { Pl1Watts: 35, Pl2Watts: 60 } =>
                    new CpuPowerLimitApplyPlan(
                        AcRaw,
                        35,
                        60),

                { Pl1Watts: 8, Pl2Watts: 15 } =>
                    new CpuPowerLimitApplyPlan(
                        BatteryRaw,
                        8,
                        15),

                _ =>
                    throw new InvalidOperationException(
                        "unexpected fake CPU request")
            };

        private static CpuPowerLimitSnapshot SnapshotForRaw(
            ulong raw) =>
            raw switch
            {
                BaselineRaw =>
                    new CpuPowerLimitSnapshot(
                        BaselineRaw,
                        45,
                        115,
                        false),

                AcRaw =>
                    new CpuPowerLimitSnapshot(
                        AcRaw,
                        35,
                        60,
                        false),

                BatteryRaw =>
                    new CpuPowerLimitSnapshot(
                        BatteryRaw,
                        8,
                        15,
                        false),

                _ =>
                    throw new InvalidOperationException(
                        "unexpected fake CPU raw")
            };
    }
}
