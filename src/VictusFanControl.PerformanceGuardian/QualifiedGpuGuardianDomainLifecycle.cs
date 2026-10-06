using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>
/// First Step 6F real-domain adapter.
///
/// This class is deliberately GPU-only. It exists to qualify one hardware
/// domain through the detached Guardian before CPU and GPU are composed
/// together. CPU=true is rejected before any GPU write.
///
/// All Set/Reset operations still flow through GpuClockSessionController, so
/// ApplyWriteArmed / PresetSwitchWriteArmed / ReleaseWriteArmed are durable
/// before the corresponding NVML mutation.
/// </summary>
internal sealed class QualifiedGpuGuardianDomainLifecycle :
    IGuardianDomainLifecycle,
    IGpuClockSourceTransitionSink,
    IDisposable
{
    private readonly CountingGpuClockLimitBackend _backend;
    private readonly GpuClockSessionController _session;
    private GpuClockPresetPolicy _policy;
    private GpuClockPresetTransitionController _transition;

    private int _enableCalls;
    private int _releaseCalls;
    private bool _lastCpuEnabled;
    private bool _lastGpuEnabled;
    private PerformancePowerSourceKind? _lastInitialSource;
    private string? _lastReleaseReason;
    private string? _lastStatus;
    private bool _disposed;

    internal QualifiedGpuGuardianDomainLifecycle(
        IGpuClockLimitBackend backend,
        IGpuClockSessionJournal journal,
        GpuClockPresetSet presets)
    {
        _backend =
            new CountingGpuClockLimitBackend(
                backend ??
                throw new ArgumentNullException(
                    nameof(backend)));

        _session =
            new GpuClockSessionController(
                _backend,
                journal ??
                throw new ArgumentNullException(
                    nameof(journal)));

        _policy =
            new GpuClockPresetPolicy(
                presets);

        _transition =
            new GpuClockPresetTransitionController(
                _policy,
                _session);
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
                0,
            GpuHardwareWriteAttempts:
                _backend.SetAttempts +
                _backend.ResetAttempts,
            CpuState:
                null,
            GpuState:
                _session.State.ToString(),
            CpuStatus:
                null,
            GpuStatus:
                _lastStatus ??
                _session.LastStatus);

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

        if (cpuEnabled ||
            !gpuEnabled)
        {
            throw new InvalidOperationException(
                "Step 6F GPU qualification accepts exactly GPU=true and CPU=false.");
        }

        if (_session.State !=
            GpuClockSessionState.Disabled)
        {
            throw new InvalidOperationException(
                "GPU Guardian session is not Disabled before explicit enable: " +
                _session.State);
        }

        var selection =
            _policy.Resolve(
                initialSource);

        if (!selection.SourceKnown ||
            !selection.Enabled ||
            !selection.Request.HasValue)
        {
            throw new InvalidOperationException(
                "Initial confirmed source does not authorize an enabled GPU preset: " +
                selection.Status);
        }

        if (!_session.Apply(
                selection.Request.Value))
        {
            _lastStatus =
                _session.LastStatus;

            throw new InvalidOperationException(
                "Initial Guardian GPU Apply failed: " +
                (_session.LastStatus ??
                 "unknown GPU session failure"));
        }

        _enableCalls++;

        _lastStatus =
            "GUARDIAN_GPU_INITIAL_PRESET_APPLIED__" +
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

        if (cpuEnabled)
        {
            throw new InvalidOperationException(
                "Step 6F GPU qualification cannot release an unqualified CPU domain.");
        }

        if (!gpuEnabled)
        {
            _releaseCalls++;
            _lastStatus =
                "GUARDIAN_GPU_RELEASE_NO_GPU_DOMAIN";

            return ValueTask.CompletedTask;
        }

        if (_session.State ==
            GpuClockSessionState.Disabled)
        {
            _releaseCalls++;
            _lastStatus =
                "GUARDIAN_GPU_ALREADY_DISABLED";

            return ValueTask.CompletedTask;
        }

        if (_session.State !=
            GpuClockSessionState.ActiveUnverified)
        {
            throw new InvalidOperationException(
                "Guardian GPU release requires ActiveUnverified; current state is " +
                _session.State +
                ". Automatic recovery Reset is intentionally forbidden.");
        }

        if (!_session.Release())
        {
            _lastStatus =
                _session.LastStatus;

            throw new InvalidOperationException(
                "Guardian GPU normal release failed: " +
                (_session.LastStatus ??
                 "unknown GPU release failure"));
        }

        _releaseCalls++;

        _lastStatus =
            "GUARDIAN_GPU_RELEASED_TO_NVIDIA_DEFAULT";

        return ValueTask.CompletedTask;
    }

    internal void UpdatePresets(GpuClockPresetSet presets, PerformancePowerSourceKind source)
    {
        ThrowIfDisposed();
        var policy = new GpuClockPresetPolicy(presets);
        var selection = policy.Resolve(source);
        if (!selection.SourceKnown || !selection.Enabled || !selection.Request.HasValue ||
            !_session.SwitchPreset(selection.Request.Value))
        {
            _lastStatus = _session.LastStatus ?? "GPU_UPDATE_REJECTED";
            throw new InvalidOperationException(_lastStatus);
        }
        _policy = policy;
        _transition = new GpuClockPresetTransitionController(policy, _session);
        _lastStatus = "GUARDIAN_GPU_PRESETS_UPDATED__" + source.ToString().ToUpperInvariant();
    }

    public GpuClockPresetTransitionResult HandleConfirmedSourceChange(
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Deliberately no implicit Reset. The host must call ReleaseAsync while
        // it still owns a valid live session. Unexpected teardown leaves the
        // durable journal as evidence and never performs blind recovery.
        _session.Dispose();
        _disposed =
            true;
    }

    private sealed class CountingGpuClockLimitBackend :
        IGpuClockLimitBackend
    {
        private readonly IGpuClockLimitBackend _inner;

        internal CountingGpuClockLimitBackend(
            IGpuClockLimitBackend inner)
        {
            _inner =
                inner;
        }

        internal int SetAttempts { get; private set; }

        internal int ResetAttempts { get; private set; }

        public GpuClockBackendCapabilities Capabilities =>
            _inner.Capabilities;

        public GpuClockBackendWriteResult SetLockedGraphicsClocks(
            GpuClockLimitRequest request)
        {
            SetAttempts++;

            return _inner.SetLockedGraphicsClocks(
                request);
        }

        public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
        {
            ResetAttempts++;

            return _inner.ResetLockedGraphicsClocks();
        }

        public GpuClockBackendObservation ReadObservation() =>
            _inner.ReadObservation();
    }
}

internal static class QualifiedGpuGuardianDomainLifecycleSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-6F-GpuDomain-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(
            root);

        try
        {
            LivePresetsRetainSession(root, output);
            NormalAcBatteryAcRelease(
                root,
                output);

            CpuSelectionIsRejectedBeforeWrite(
                root,
                output);

            UnknownInitialSourceIsRejectedBeforeWrite(
                root,
                output);

            output.WriteLine(
                "Step 6F GPU Guardian domain self-test: PASS (journaled initial Apply, AC/Battery switches, normal Reset, CPU/Unknown fail-closed, fake backend only).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Step 6F GPU Guardian domain self-test: FAIL - " +
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

    private static void LivePresetsRetainSession(string root, TextWriter output)
    {
        var journal=new JsonGpuClockSessionJournal(Path.Combine(root,"live-update-gpu.json"),TargetProfileId);
        var backend=new FakeGpuBackend();
        using var lifecycle=new QualifiedGpuGuardianDomainLifecycle(backend,journal,GpuClockPresetSet.UserRequestedVictus);
        lifecycle.EnableAsync(false,true,PerformancePowerSourceKind.Ac,CancellationToken.None).GetAwaiter().GetResult();
        var before=journal.Load()!;
        lifecycle.UpdatePresets(new(new(true,210,1801),new(true,210,1101)),PerformancePowerSourceKind.Ac);
        Require(journal.Load()!.SessionId==before.SessionId&&backend.LastRequest==new GpuClockLimitRequest(210,1801)&&backend.ResetCalls==0,
            "live GPU update reset hardware or replaced the journal session");
        Require(lifecycle.HandleConfirmedSourceChange(PerformancePowerSourceKind.Battery).Succeeded&&backend.LastRequest==new GpuClockLimitRequest(210,1101),
            "source transition lost live GPU presets");
        lifecycle.ReleaseAsync(false,true,"LIVE_UPDATE_TEST",CancellationToken.None).GetAwaiter().GetResult();
        Require(backend.ResetCalls==1&&journal.Load() is null,"live GPU update broke normal final Reset");
        output.WriteLine("PASS live GPU presets retain journal session, use direct Set and one final Reset");
    }

    private static void NormalAcBatteryAcRelease(
        string root,
        TextWriter output)
    {
        var backend =
            new FakeGpuBackend();

        var journalPath =
            Path.Combine(
                root,
                "normal-gpu-journal.json");

        var journal =
            new JsonGpuClockSessionJournal(
                journalPath,
                TargetProfileId);

        using var lifecycle =
            new QualifiedGpuGuardianDomainLifecycle(
                backend,
                journal,
                GpuClockPresetSet.UserRequestedVictus);

        lifecycle.EnableAsync(
                cpuEnabled: false,
                gpuEnabled: true,
                PerformancePowerSourceKind.Ac,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Require(
            backend.SetCalls == 1 &&
            backend.LastRequest ==
                new GpuClockLimitRequest(
                    210,
                    1850),
            "explicit GPU-only enable applies qualified AC preset exactly once");

        var battery =
            lifecycle.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Battery);

        Require(
            battery.Succeeded &&
            backend.SetCalls == 2 &&
            backend.LastRequest ==
                new GpuClockLimitRequest(
                    210,
                    1200),
            "AC to Battery uses one direct journaled Set");

        var ac =
            lifecycle.HandleConfirmedSourceChange(
                PerformancePowerSourceKind.Ac);

        Require(
            ac.Succeeded &&
            backend.SetCalls == 3 &&
            backend.LastRequest ==
                new GpuClockLimitRequest(
                    210,
                    1850),
            "Battery to AC uses one direct journaled Set");

        lifecycle.ReleaseAsync(
                cpuEnabled: false,
                gpuEnabled: true,
                "SELF_TEST",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var snapshot =
            lifecycle.Snapshot;

        Require(
            backend.ResetCalls == 1 &&
            snapshot.GpuHardwareWriteAttempts == 4 &&
            string.Equals(
                snapshot.GpuState,
                GpuClockSessionState.Disabled.ToString(),
                StringComparison.Ordinal) &&
            !File.Exists(
                journalPath),
            "normal Guardian release performs one final Reset and clears journal");

        output.WriteLine(
            "PASS Step 6F GPU domain AC -> Battery -> AC -> one final Reset");
    }

    private static void CpuSelectionIsRejectedBeforeWrite(
        string root,
        TextWriter output)
    {
        var backend =
            new FakeGpuBackend();

        using var lifecycle =
            new QualifiedGpuGuardianDomainLifecycle(
                backend,
                new JsonGpuClockSessionJournal(
                    Path.Combine(
                        root,
                        "cpu-rejected.json"),
                    TargetProfileId),
                GpuClockPresetSet.UserRequestedVictus);

        var rejected =
            false;

        try
        {
            lifecycle.EnableAsync(
                    cpuEnabled: true,
                    gpuEnabled: true,
                    PerformancePowerSourceKind.Ac,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException)
        {
            rejected =
                true;
        }

        Require(
            rejected &&
            backend.SetCalls == 0 &&
            backend.ResetCalls == 0,
            "unqualified combined CPU+GPU enable is rejected before hardware");

        output.WriteLine(
            "PASS Step 6F first hardware gate refuses combined CPU+GPU authority");
    }

    private static void UnknownInitialSourceIsRejectedBeforeWrite(
        string root,
        TextWriter output)
    {
        var backend =
            new FakeGpuBackend();

        using var lifecycle =
            new QualifiedGpuGuardianDomainLifecycle(
                backend,
                new JsonGpuClockSessionJournal(
                    Path.Combine(
                        root,
                        "unknown-rejected.json"),
                    TargetProfileId),
                GpuClockPresetSet.UserRequestedVictus);

        var rejected =
            false;

        try
        {
            lifecycle.EnableAsync(
                    cpuEnabled: false,
                    gpuEnabled: true,
                    PerformancePowerSourceKind.Unknown,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (InvalidOperationException)
        {
            rejected =
                true;
        }

        Require(
            rejected &&
            backend.SetCalls == 0 &&
            backend.ResetCalls == 0,
            "Unknown initial source must perform zero hardware writes");

        output.WriteLine(
            "PASS Step 6F GPU initial Unknown is fail-closed with zero writes");
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

    private sealed class FakeGpuBackend :
        IGpuClockLimitBackend
    {
        internal int SetCalls { get; private set; }
        internal int ResetCalls { get; private set; }
        internal GpuClockLimitRequest? LastRequest { get; private set; }

        public GpuClockBackendCapabilities Capabilities =>
            new(
                SetLockedGraphicsClocksExportAvailable: true,
                ResetLockedGraphicsClocksExportAvailable: true,
                CurrentGraphicsClockExportAvailable: true,
                ApplicationGraphicsClockTargetExportAvailable: true,
                CurrentClocksEventReasonsExportAvailable: true,
                ExactLockedRangeReadbackAvailable: false,
                HardwareWritesAuthorized: true);

        public GpuClockBackendWriteResult SetLockedGraphicsClocks(
            GpuClockLimitRequest request)
        {
            SetCalls++;
            LastRequest =
                request;

            return new GpuClockBackendWriteResult(
                Succeeded: true,
                FailureKind:
                    GpuClockBackendFailureKind.None,
                NvmlResult: 0,
                Status:
                    "FAKE_GPU_SET_ACCEPTED");
        }

        public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
        {
            ResetCalls++;

            return new GpuClockBackendWriteResult(
                Succeeded: true,
                FailureKind:
                    GpuClockBackendFailureKind.None,
                NvmlResult: 0,
                Status:
                    "FAKE_GPU_RESET_ACCEPTED");
        }

        public GpuClockBackendObservation ReadObservation() =>
            new(
                Succeeded: true,
                CurrentGraphicsClockMHz: 1000,
                ApplicationGraphicsClockTargetMHz: null,
                CurrentClocksEventReasons: null,
                ExactLockedRange: null,
                ProvesExactLockedRangeOwnership: false,
                FailureKind:
                    GpuClockBackendFailureKind.None,
                NvmlResult: 0,
                Status:
                    "FAKE_GPU_OBSERVATION");
    }
}
