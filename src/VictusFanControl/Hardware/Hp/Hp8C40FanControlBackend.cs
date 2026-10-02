using VictusFanControl.Control;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;

namespace VictusFanControl.Hardware.Hp;

internal interface IHp8C40FanHardware : IDisposable
{
    Hp8C40EcControlState ReadEcState();

    (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
    {
        var state = ReadEcState();
        return (state.CpuSetpoint, state.GpuSetpoint);
    }

    (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels();
    void SetFanLevel(byte cpuLevel, byte gpuLevel);
    void RestoreFirmwareAuto();
}

internal interface IHp8C40FanWriteQualificationHook
{
    ValueTask AfterHardwareAcknowledgedBeforeWatchdogCommitAsync(
        byte cpuTarget,
        byte gpuTarget,
        (byte CpuSetpoint, byte GpuSetpoint) setpointAck,
        Hp8C40EcControlState tachAck,
        CancellationToken cancellationToken);
}

internal sealed class Hp8C40FanHardware : IHp8C40FanHardware
{
    private readonly Hp8C40BiosFanControl _bios;
    private readonly AcpiEcReader _ec;

    public Hp8C40FanHardware(string modulesDirectory)
    {
        _bios = new Hp8C40BiosFanControl();
        _ec = new AcpiEcReader(Path.Combine(modulesDirectory, "LpcACPIEC.bin"));
    }

    public Hp8C40EcControlState ReadEcState()
    {
        // The control path consumes only ownership, MaxFan/FanSwitch and the
        // two physical tachometers. Keep those as three independently retried
        // narrow EC snapshots instead of reopening the broad diagnostic state.
        var layout = Hp8C40TargetProfile.Instance.FanEcLayout;
        var setpoint = _ec.ReadStableFanSetpoint(layout);
        var controlGuard = _ec.ReadFanControlGuard(layout);
        var tachometers = _ec.ReadFanTachometers(layout);

        return new Hp8C40EcControlState(
            CpuRateTarget: byte.MaxValue,
            GpuRateTarget: byte.MaxValue,
            CpuRate: byte.MaxValue,
            GpuRate: byte.MaxValue,
            CpuSetpoint: setpoint.CpuSetpoint,
            GpuSetpoint: setpoint.GpuSetpoint,
            Diagnostic62: byte.MaxValue,
            Diagnostic63: byte.MaxValue,
            Mode: byte.MaxValue,
            MaxFan: controlGuard.MaxFan,
            FanSwitch: controlGuard.FanSwitch,
            CpuRpm: tachometers.CpuRpm,
            GpuRpm: tachometers.GpuRpm);
    }

    public (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
    {
        var state = _ec.ReadStableFanSetpoint(
            Hp8C40TargetProfile.Instance.FanEcLayout);
        return (state.CpuSetpoint, state.GpuSetpoint);
    }

    public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
        _bios.GetCurrentFanLevels();

    public void SetFanLevel(byte cpuLevel, byte gpuLevel) =>
        _bios.SetFanLevel(cpuLevel, gpuLevel);

    public void RestoreFirmwareAuto() =>
        _bios.RestoreFirmwareAuto();

    public void Dispose() => _ec.Dispose();
}

internal readonly record struct Hp8C40FanBackendTiming(
    TimeSpan SetpointAckTimeout,
    TimeSpan RestoreAckTimeout,
    TimeSpan TachometerAckTimeout,
    TimeSpan PollInterval)
{
    public static Hp8C40FanBackendTiming Production => new(
        SetpointAckTimeout: TimeSpan.FromMilliseconds(1500),
        RestoreAckTimeout: TimeSpan.FromSeconds(5),
        TachometerAckTimeout: TimeSpan.FromSeconds(8),
        PollInterval: TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Production HP 8C40 fan-control backend.
///
/// This class is deliberately narrow:
/// - exact target fingerprint only;
/// - ordinary commands restricted to the validated equal 10-50 range;
/// - no arbitrary EC writes;
/// - fixed-level ownership is acknowledged through EC 0x34/0x35;
/// - both physical tachometers must acknowledge every new command;
/// - firmware restore uses the hardware-validated FF,FF -> LegacyDefault path;
/// - production watchdog-backed construction is separately M9-gated even
///   after the M4-M8 qualification chain; automatic policy remains outside
///   the backend.
///
/// It does not implement a fan curve. Policy remains outside the backend.
/// </summary>
public sealed class Hp8C40FanControlBackend :
    IFanControlBackend,
    IFanControlRestoreEvidenceSource
{
    private const int DirectionLevelDeadband = 2;
    private const int MinimumDirectionalRpmDelta = 150;
    private const int RequiredTachConfirmationSamples = 2;
    private const int MaximumTransientTachSnapshotReadFailures = 2;
    private const int MaximumTransientSetpointReadFailures = 2;
    private const int RequiredConsecutiveUnexpectedGuardSamples = 2;
    private const int MaximumUnexpectedGuardConfirmationReads = 3;
    private static readonly TimeSpan UnexpectedGuardConfirmationDelay = TimeSpan.FromMilliseconds(25);

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly IHp8C40FanHardware? _hardware;
    private readonly bool _targetSupported;
    private readonly string _supportDetail;
    private readonly Hp8C40FanBackendTiming _timing;
    private readonly int _minimumCommandLevel;
    private readonly int _maximumCommandLevel;
    private readonly IFanControlWatchdogLeaseClient? _watchdogLease;
    private readonly IActiveTimeClock _activeTimeClock;
    private readonly IHp8C40FanWriteQualificationHook? _qualificationHook;

    private bool _customModeActive;
    private bool _disposed;
    private string _lastDetail;
    private (byte Cpu, byte Gpu)? _ownedSetpoint;
    private FanFirmwareRestoreEvidence _lastRestoreEvidence;

    public Hp8C40FanControlBackend(
        string modulesDirectory,
        IFanControlWatchdogLeaseClient? watchdogLease = null)
    {
        var identity = HardwareIdentityReader.ReadCurrent();
        _targetSupported = Hp8C40TargetProfile.Matches(identity, out var reason);
        _supportDetail = reason;
        _timing = Hp8C40FanBackendTiming.Production;
        _minimumCommandLevel = Hp8C40TargetProfile.MinimumValidatedFanLevel;
        _maximumCommandLevel = Hp8C40TargetProfile.MaximumValidatedFanLevel;

        if (_targetSupported && watchdogLease is not null)
        {
            Hp8C40ProductionWatchdogGate
                .RequireProductionConstructionAuthorized(
                    identity);
        }

        _watchdogLease = watchdogLease;
        _activeTimeClock = new WindowsActiveTimeClock();
        _qualificationHook = null;
        _lastRestoreEvidence = new FanFirmwareRestoreEvidence(
            LocalFirmwareAckVerified: false,
            WatchdogLeaseRequired: _watchdogLease is not null,
            WatchdogReleaseVerified: false,
            CompletedAtUtc: DateTimeOffset.MinValue,
            Detail: "No firmware restore has completed in this backend instance.");

        if (_targetSupported)
        {
            _hardware = new Hp8C40FanHardware(modulesDirectory);
            _lastDetail = "Validated HP 8C40 backend initialized; firmware authority retained.";
        }
        else
        {
            _lastDetail = $"Write backend disabled: {reason}";
        }
    }

    internal Hp8C40FanControlBackend(
        IHp8C40FanHardware hardware,
        bool targetSupported = true,
        string supportDetail = "Synthetic validated target.",
        Hp8C40FanBackendTiming? timing = null,
        IFanControlWatchdogLeaseClient? watchdogLease = null,
        IActiveTimeClock? activeTimeClock = null,
        int? minimumCommandLevel = null,
        int? maximumCommandLevel = null,
        IHp8C40FanWriteQualificationHook? qualificationHook = null)
    {
        _hardware = hardware;
        _targetSupported = targetSupported;
        _supportDetail = supportDetail;
        _timing = timing ?? Hp8C40FanBackendTiming.Production;
        _minimumCommandLevel =
            minimumCommandLevel ?? Hp8C40TargetProfile.MinimumValidatedFanLevel;
        _maximumCommandLevel =
            maximumCommandLevel ?? Hp8C40TargetProfile.MaximumValidatedFanLevel;

        if (_minimumCommandLevel < 0 ||
            _maximumCommandLevel > byte.MaxValue ||
            _minimumCommandLevel > _maximumCommandLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumCommandLevel),
                $"Invalid HP 8C40 backend command envelope " +
                $"{_minimumCommandLevel}-{_maximumCommandLevel}.");
        }

        _watchdogLease = watchdogLease;
        _activeTimeClock = activeTimeClock ?? new WindowsActiveTimeClock();
        _qualificationHook = qualificationHook;
        _lastRestoreEvidence = new FanFirmwareRestoreEvidence(
            LocalFirmwareAckVerified: false,
            WatchdogLeaseRequired: _watchdogLease is not null,
            WatchdogReleaseVerified: false,
            CompletedAtUtc: DateTimeOffset.MinValue,
            Detail: "No firmware restore has completed in this backend instance.");
        _lastDetail = targetSupported
            ? "Synthetic backend initialized; firmware authority retained."
            : $"Write backend disabled: {supportDetail}";
    }

    public const string LifecycleQualificationToken =
        "8C40-M6-MODERN-STANDBY30";

    /// <summary>
    /// Explicit physical-qualification constructor for the HP 8C40
    /// Modern Standby lifecycle gate. The ordinary public constructor and
    /// production backend factory remain blocked from watchdog-backed 8C40
    /// control until lifecycle qualification is complete.
    /// </summary>
    public static Hp8C40FanControlBackend CreateLifecycleQualificationBackend(
        string modulesDirectory,
        IFanControlWatchdogLeaseClient watchdogLease,
        string qualificationToken)
    {
        if (!string.Equals(
                qualificationToken,
                LifecycleQualificationToken,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"HP 8C40 lifecycle qualification requires exact token '{LifecycleQualificationToken}'.");
        }

        ArgumentNullException.ThrowIfNull(watchdogLease);

        var identity = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(
                identity,
                out var reason))
        {
            throw new NotSupportedException(
                $"HP 8C40 lifecycle qualification refused: {reason}");
        }

        return new Hp8C40FanControlBackend(
            new Hp8C40FanHardware(modulesDirectory),
            targetSupported: true,
            supportDetail:
                "Exact HP 8C40 M6 Modern Standby lifecycle qualification backend.",
            watchdogLease: watchdogLease);
    }

    public string Name => "HP 8C40 BIOS/WMI + EC/tach verification";

    public FanFirmwareRestoreEvidence LastRestoreEvidence =>
        _lastRestoreEvidence;

    public bool CanWrite =>
        !_disposed &&
        _targetSupported &&
        _hardware is not null;

    public FanBackendCapabilities Capabilities =>
        new(
            Hp8C40TargetProfile.BoardProduct,
            _minimumCommandLevel,
            _maximumCommandLevel,
            SupportsIndependentLevels: false);

    public async ValueTask ProbeControlDependencyAsync(
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_watchdogLease is not null &&
                _customModeActive &&
                _ownedSetpoint.HasValue)
            {
                await _watchdogLease.ProbeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async ValueTask<FanBackendStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!CanWrite)
            {
                return new FanBackendStatus(
                    Name,
                    CanWrite: false,
                    CustomModeActive: false,
                    OwnershipValid: true,
                    FeedbackHealthy: true,
                    Detail: _lastDetail);
            }

            if (_watchdogLease is not null &&
                _customModeActive &&
                _ownedSetpoint.HasValue)
            {
                // Probe watchdog transport/lease BEFORE touching EC, but do not
                // renew liveness here. This makes watchdog-process death a
                // distinct failure domain that cannot be masked by an unrelated
                // EC read transient. Heartbeat remains coupled below to a fresh
                // successful ownership + feedback validation.
                await _watchdogLease.ProbeAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            Hp8C40EcControlState state;
            var initialUnexpectedGuard = string.Empty;

            try
            {
                state = _hardware!.ReadEcState();

                if (_customModeActive &&
                    !ControlGuardsAreSane(state))
                {
                    initialUnexpectedGuard =
                        $"max=0x{state.MaxFan:X2}, switch=0x{state.FanSwitch:X2}";

                    state =
                        await ConfirmUnexpectedControlGuardAsync(
                                state,
                                cancellationToken)
                            .ConfigureAwait(false);
                }
            }
            catch (Exception ecFailure)
                when (ecFailure is not OperationCanceledException)
            {
                if (_watchdogLease is not null &&
                    _customModeActive &&
                    _ownedSetpoint.HasValue)
                {
                    // Close the small race where the watchdog can die after the
                    // first liveness probe but before/during the EC transaction.
                    // If IPC is now gone, surface watchdog loss as the causal
                    // failure. If the watchdog is still reachable, preserve the
                    // original EC failure and fail closed exactly as before.
                    try
                    {
                        await _watchdogLease.ProbeAsync(CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        throw;
                    }
                }

                throw;
            }

            var ownershipValid =
                !_customModeActive ||
                (_ownedSetpoint.HasValue
                    ? state.CpuSetpoint == _ownedSetpoint.Value.Cpu &&
                      state.GpuSetpoint == _ownedSetpoint.Value.Gpu
                    : state.CpuSetpoint == byte.MaxValue &&
                      state.GpuSetpoint == byte.MaxValue);

            var feedbackHealthy =
                !_customModeActive ||
                (state.MaxFan == 0 &&
                 state.FanSwitch == 0 &&
                 (!_ownedSetpoint.HasValue ||
                  (IsRunningTachometerValid(state.CpuRpm) &&
                   IsRunningTachometerValid(state.GpuRpm))));

            var ownership = !_customModeActive
                ? "firmware/none"
                : ownershipValid
                    ? _ownedSetpoint.HasValue ? "owned" : "reserved/FF"
                    : "OWNERSHIP-MISMATCH";

            var feedback = !_customModeActive
                ? "firmware"
                : feedbackHealthy
                    ? "healthy"
                    : "FEEDBACK/CONTROL-STATE-INVALID";

            var guardConfirmationDetail =
                !string.IsNullOrEmpty(initialUnexpectedGuard) &&
                ControlGuardsAreSane(state)
                    ? $" Initial unexpected guard sample ({initialUnexpectedGuard}) recovered to 00/00 on bounded read-only confirmation."
                    : string.Empty;

            var detail =
                $"{_lastDetail} EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}, " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}, ownership={ownership}, feedback={feedback}, " +
                $"max=0x{state.MaxFan:X2}, switch=0x{state.FanSwitch:X2}." +
                guardConfirmationDetail;

            if (_watchdogLease is not null &&
                _customModeActive &&
                _ownedSetpoint.HasValue &&
                ownershipValid &&
                feedbackHealthy)
            {
                // The heartbeat is coupled to a successful fresh EC ownership
                // and feedback check. A blind timer must never keep the lease
                // alive while the controller/safety path is stalled.
                await _watchdogLease.HeartbeatAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return new FanBackendStatus(
                Name,
                CanWrite: true,
                CustomModeActive: _customModeActive,
                OwnershipValid: ownershipValid,
                FeedbackHealthy: feedbackHealthy,
                Detail: detail);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async ValueTask EnterCustomModeAsync(
        CancellationToken cancellationToken)
    {
        var gateTaken = false;
        try
        {
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateTaken = true;

            ThrowIfDisposed();
            EnsureWritable();

            if (_customModeActive)
            {
                return;
            }

            var state = _hardware!.ReadEcState();

            if (state.MaxFan != 0)
            {
                throw new FanControlOwnershipConflictException(
                    $"Custom fan authority refused because Max Fan is active (EC 0xEC=0x{state.MaxFan:X2}).");
            }

            if (state.FanSwitch != 0)
            {
                throw new FanControlOwnershipConflictException(
                    $"Custom fan authority refused because the fan switch is not in the validated ON state " +
                    $"(EC 0xF4=0x{state.FanSwitch:X2}).");
            }

            if (state.CpuSetpoint != byte.MaxValue ||
                state.GpuSetpoint != byte.MaxValue)
            {
                throw new FanControlOwnershipConflictException(
                    $"Custom fan authority refused because an existing fixed override is present " +
                    $"(EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}). " +
                    "Restore firmware auto first.");
            }

            if (_watchdogLease is not null)
            {
                await _watchdogLease.PrepareAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            _ownedSetpoint = null;
            _customModeActive = true;
            _lastDetail =
                $"Custom authority prepared from firmware-auto FF/FF state; " +
                $"max=0x{state.MaxFan:X2}, switch=0x{state.FanSwitch:X2}.";
        }
        catch (FanControlAdmissionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // EnterCustomMode performs no hardware write. Treat every failure,
            // including cancellation while waiting for the backend gate, as a
            // no-write admission failure so the coordinator never clears an
            // unrelated external override with FF,FF.
            throw new FanControlAdmissionException(
                $"Custom fan authority admission failed before any fan write was attempted: " +
                $"{ex.GetType().Name}: {ex.Message}",
                ex);
        }
        finally
        {
            if (gateTaken)
            {
                _ioGate.Release();
            }
        }
    }

    public async ValueTask ApplyAsync(
        FanCommand command,
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureWritable();

            if (!_customModeActive)
            {
                throw new InvalidOperationException(
                    "Fan command refused because backend custom mode is not active.");
            }

            // Keep caller-input validation precise. The no-write classification
            // below applies only after a valid first command begins hardware
            // admission and before SetFanLevel is attempted.
            ValidateCommand(command);

            var writeAttempted = _ownedSetpoint.HasValue;
            var leaseWriteArmed = false;
            var admissionStage = "validate caller cancellation";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cpuTarget = checked((byte)command.CpuLevel);
                var gpuTarget = checked((byte)command.GpuLevel);

                admissionStage = "read initial EC control state";
                var before = _hardware!.ReadEcState();

                admissionStage = "validate initial EC ownership";
                VerifyExistingOwnership(before);

                admissionStage = "validate initial EC guards";
                ValidateActiveControlState(
                    before,
                    requireRunningTachometers: _ownedSetpoint.HasValue);

                admissionStage = "read BIOS current-speed telemetry";
                var currentLevels = _hardware.GetCurrentFanLevels();

                admissionStage = "validate BIOS current-speed telemetry";
                ValidateCurrentSpeedLevel(currentLevels.CpuLevel, "CPU");
                ValidateCurrentSpeedLevel(currentLevels.GpuLevel, "GPU");

                admissionStage = "read pre-dispatch EC control state";
                var preDispatch = _hardware.ReadEcState();

                admissionStage = "validate pre-dispatch EC ownership";
                VerifyExistingOwnership(preDispatch);

                admissionStage = "validate pre-dispatch EC guards";
                ValidateActiveControlState(
                    preDispatch,
                    requireRunningTachometers: _ownedSetpoint.HasValue);

                admissionStage = "validate caller cancellation before watchdog/write";
                cancellationToken.ThrowIfCancellationRequested();

                if (preDispatch.CpuSetpoint != cpuTarget ||
                    preDispatch.GpuSetpoint != gpuTarget)
                {
                    if (_watchdogLease is not null)
                    {
                        admissionStage = "durably arm watchdog WRITE_INTENT";
                        await _watchdogLease.WriteIntentAsync(
                                cpuTarget,
                                gpuTarget,
                                cancellationToken)
                            .ConfigureAwait(false);

                        leaseWriteArmed = true;

                        // Durable lease IPC intentionally widens the interval
                        // between the original pre-dispatch check and WMI. Re-read
                        // EC after WriteIntent ACK so an external controller that
                        // appeared during that interval is still preserved.
                        admissionStage = "read EC after watchdog WRITE_INTENT";
                        var postIntent = _hardware.ReadEcState();

                        admissionStage = "validate EC ownership after watchdog WRITE_INTENT";
                        VerifyExistingOwnership(postIntent);

                        admissionStage = "validate EC guards after watchdog WRITE_INTENT";
                        ValidateActiveControlState(
                            postIntent,
                            requireRunningTachometers: _ownedSetpoint.HasValue);

                        admissionStage = "validate caller cancellation before WMI fan write";
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Set this before WMI dispatch: on HP hardware the command
                    // may take effect even if WMI subsequently reports failure.
                    admissionStage = "dispatch WMI SetFanLevel";
                    writeAttempted = true;
                    _hardware.SetFanLevel(cpuTarget, gpuTarget);
                }

                var setpointAck = await WaitForSetpointAsync(
                    cpuTarget,
                    gpuTarget,
                    _timing.SetpointAckTimeout,
                    cancellationToken).ConfigureAwait(false);

                var tachAck = await WaitForTachometerResponseAsync(
                    cpuTarget,
                    gpuTarget,
                    currentLevels,
                    preDispatch,
                    cancellationToken).ConfigureAwait(false);

                if (leaseWriteArmed &&
                    _watchdogLease is not null &&
                    _qualificationHook is not null)
                {
                    // Qualification-only pause point. Production construction
                    // cannot provide this internal hook. It runs only after the
                    // real WMI write has been acknowledged by EC setpoints and
                    // both tachometers, while the watchdog journal is still
                    // durably WRITE_ARMED and before Commit can be dispatched.
                    await _qualificationHook
                        .AfterHardwareAcknowledgedBeforeWatchdogCommitAsync(
                            cpuTarget,
                            gpuTarget,
                            setpointAck,
                            tachAck,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (leaseWriteArmed && _watchdogLease is not null)
                {
                    await _watchdogLease.CommitAsync(
                            cpuTarget,
                            gpuTarget,
                            cancellationToken)
                        .ConfigureAwait(false);

                    leaseWriteArmed = false;
                }

                _ownedSetpoint = (cpuTarget, gpuTarget);
                _lastDetail =
                    $"Command {command.CpuLevel}/{command.GpuLevel} acknowledged by EC setpoints " +
                    $"and both tachometers; RPM={tachAck.CpuRpm}/{tachAck.GpuRpm}, " +
                    $"initial RPM={preDispatch.CpuRpm}/{preDispatch.GpuRpm}, " +
                    $"setpoint={setpointAck.CpuSetpoint}/{setpointAck.GpuSetpoint}.";
            }
            catch (FanControlAdmissionException)
            {
                throw;
            }
            catch (Exception ex) when (!writeAttempted)
            {
                Exception? leaseRollbackFailure = null;

                if (_watchdogLease is not null)
                {
                    try
                    {
                        if (leaseWriteArmed)
                        {
                            await _watchdogLease.AbortWriteIntentAsync(
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }

                        if (!_ownedSetpoint.HasValue)
                        {
                            await _watchdogLease.CancelPreparedAsync(
                                    CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        leaseRollbackFailure = rollbackFailure;
                    }
                }

                // Read-only EnterCustomMode can race an external controller before
                // our first write. Relinquish logical authority without issuing a
                // local FF,FF that might clear that external state. If watchdog
                // rollback could not be proven, its durable WRITE_ARMED record is
                // deliberately left for fail-closed service recovery.
                _ownedSetpoint = null;
                _customModeActive = false;

                var primaryCause =
                    $"{ex.GetType().Name}: {ex.Message}";

                _lastDetail =
                    leaseRollbackFailure is null
                        ? $"First custom command was refused before any fan write at stage '{admissionStage}': {primaryCause}. Logical/watchdog authority was released without FF,FF."
                        : $"First custom command was refused before any fan write at stage '{admissionStage}': {primaryCause}. Watchdog rollback remained armed: {leaseRollbackFailure.GetType().Name}: {leaseRollbackFailure.Message}";

                throw new FanControlAdmissionException(
                    $"First custom fan command failed before any fan write was attempted at stage '{admissionStage}': {primaryCause}",
                    leaseRollbackFailure is null
                        ? ex
                        : new AggregateException(ex, leaseRollbackFailure));
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async ValueTask RestoreFirmwareAutoAsync(
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            EnsureWritable();
            await RestoreWithWatchdogLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            if (_customModeActive && _targetSupported && _hardware is not null)
            {
                await RestoreWithWatchdogLockedAsync(CancellationToken.None).ConfigureAwait(false);
            }

            _disposed = true;
        }
        finally
        {
            // Once the synchronization primitive is disposed the backend must
            // always report itself disposed, even if a final restore failed.
            // The persistent PawnIO EC session is also released here.
            try
            {
                _hardware?.Dispose();

                if (_watchdogLease is not null)
                {
                    await _watchdogLease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _disposed = true;
                _ioGate.Release();
                _ioGate.Dispose();
            }
        }
    }

    private void VerifyExistingOwnership(Hp8C40EcControlState state)
    {
        if (_ownedSetpoint.HasValue)
        {
            if (state.CpuSetpoint != _ownedSetpoint.Value.Cpu ||
                state.GpuSetpoint != _ownedSetpoint.Value.Gpu)
            {
                throw new InvalidOperationException(
                    $"Fan ownership lost before command dispatch. Expected EC setpoint " +
                    $"{_ownedSetpoint.Value.Cpu}/{_ownedSetpoint.Value.Gpu}, read " +
                    $"{state.CpuSetpoint}/{state.GpuSetpoint}.");
            }

            return;
        }

        if (state.CpuSetpoint != byte.MaxValue ||
            state.GpuSetpoint != byte.MaxValue)
        {
            throw new InvalidOperationException(
                $"Fan ownership changed after authority acquisition but before the first command. " +
                $"Expected FF/FF, read {state.CpuSetpoint}/{state.GpuSetpoint}.");
        }
    }

    private async ValueTask<Hp8C40EcControlState> WaitForTachometerResponseAsync(
        byte cpuTarget,
        byte gpuTarget,
        (byte CpuLevel, byte GpuLevel) currentLevels,
        Hp8C40EcControlState baseline,
        CancellationToken cancellationToken)
    {
        var cpuExpectation = DetermineExpectation(cpuTarget, currentLevels.CpuLevel);
        var gpuExpectation = DetermineExpectation(gpuTarget, currentLevels.GpuLevel);

        var started = _activeTimeClock.Milliseconds;
        var cpuEverAcknowledged = false;
        var gpuEverAcknowledged = false;
        var confirmationSamples = 0;
        var transientSnapshotReadFailures = 0;
        Hp8C40EcControlState? last = null;
        string? lastSnapshotReadFailure = null;

        while (!ActiveTimeClock.HasElapsed(
                   _activeTimeClock,
                   started,
                   _timing.TachometerAckTimeout))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                last = _hardware!.ReadEcState();
            }
            catch (IOException ex)
            {
                transientSnapshotReadFailures++;
                lastSnapshotReadFailure =
                    $"{ex.GetType().Name}: {ex.Message}";
                confirmationSamples = 0;

                if (transientSnapshotReadFailures >
                    MaximumTransientTachSnapshotReadFailures)
                {
                    throw new IOException(
                        $"Tachometer acknowledgement lost EC observability after " +
                        $"{transientSnapshotReadFailures} failed control-state snapshots. " +
                        $"The real command remains uncommitted and must be restored fail-closed. " +
                        $"Last failure: {ex.Message}",
                        ex);
                }

                await Task.Delay(
                        _timing.PollInterval,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (last.CpuSetpoint != cpuTarget ||
                last.GpuSetpoint != gpuTarget)
            {
                throw new InvalidOperationException(
                    $"Fan ownership was overwritten during tachometer acknowledgement. " +
                    $"Expected EC setpoint {cpuTarget}/{gpuTarget}, read " +
                    $"{last.CpuSetpoint}/{last.GpuSetpoint}.");
            }

            ValidateTachometerRange(last.CpuRpm, "CPU");
            ValidateTachometerRange(last.GpuRpm, "GPU");

            var cpuCurrentlyRunning = last.CpuRpm > 0;
            var gpuCurrentlyRunning = last.GpuRpm > 0;

            var cpuSampleAcknowledged = cpuCurrentlyRunning &&
                HasTachometerResponded(
                    cpuExpectation,
                    baseline.CpuRpm,
                    last.CpuRpm,
                    int.MaxValue);

            var gpuSampleAcknowledged = gpuCurrentlyRunning &&
                HasTachometerResponded(
                    gpuExpectation,
                    baseline.GpuRpm,
                    last.GpuRpm,
                    int.MaxValue);

            cpuEverAcknowledged |= cpuSampleAcknowledged;
            gpuEverAcknowledged |= gpuSampleAcknowledged;

            // Require the directional/continuity evidence itself to remain true
            // for consecutive samples. A one-sample RPM spike must not latch an
            // acknowledgement that later samples no longer support.
            if (cpuSampleAcknowledged && gpuSampleAcknowledged)
            {
                confirmationSamples++;
                if (confirmationSamples >= RequiredTachConfirmationSamples)
                {
                    return last;
                }
            }
            else
            {
                confirmationSamples = 0;
            }

            await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Both fan tachometers did not acknowledge command {cpuTarget}/{gpuTarget} within " +
            $"{_timing.TachometerAckTimeout.TotalSeconds:0.0} s. " +
            $"CPU ever-ack={cpuEverAcknowledged}, GPU ever-ack={gpuEverAcknowledged}, " +
            $"baseline RPM={baseline.CpuRpm}/{baseline.GpuRpm}, " +
            $"last RPM={last?.CpuRpm.ToString() ?? "n/a"}/{last?.GpuRpm.ToString() ?? "n/a"}, " +
            $"baseline current-level={currentLevels.CpuLevel}/{currentLevels.GpuLevel}, " +
            $"transient EC snapshot failures={transientSnapshotReadFailures}, " +
            $"last EC snapshot failure={lastSnapshotReadFailure ?? "none"}.");
    }

    private static TachExpectation DetermineExpectation(
        byte requestedLevel,
        byte currentLevel)
    {
        if (requestedLevel >= currentLevel + DirectionLevelDeadband + 1)
        {
            return TachExpectation.Increase;
        }

        if (requestedLevel + DirectionLevelDeadband + 1 <= currentLevel)
        {
            return TachExpectation.Decrease;
        }

        return TachExpectation.Steady;
    }

    private static bool HasTachometerResponded(
        TachExpectation expectation,
        ushort baselineRpm,
        ushort currentRpm,
        int observedMaximumRpm)
    {
        if (currentRpm == 0)
        {
            return false;
        }

        return expectation switch
        {
            TachExpectation.Increase =>
                baselineRpm >= observedMaximumRpm - 100 ||
                currentRpm >= baselineRpm + MinimumDirectionalRpmDelta,

            TachExpectation.Decrease =>
                baselineRpm <= 1500 ||
                currentRpm + MinimumDirectionalRpmDelta <= baselineRpm,

            TachExpectation.Steady => true,
            _ => false
        };
    }



    private async ValueTask<Hp8C40EcControlState> ConfirmUnexpectedControlGuardAsync(
        Hp8C40EcControlState initial,
        CancellationToken cancellationToken)
    {
        var last = initial;
        var previousMaxFan = initial.MaxFan;
        var previousFanSwitch = initial.FanSwitch;
        var consecutiveUnexpected = 1;

        for (var read = 1;
             read < MaximumUnexpectedGuardConfirmationReads;
             read++)
        {
            await Task.Delay(
                    UnexpectedGuardConfirmationDelay,
                    cancellationToken)
                .ConfigureAwait(false);

            var next = _hardware!.ReadEcState();

            if (ControlGuardsAreSane(next))
            {
                return next;
            }

            if (next.MaxFan == previousMaxFan &&
                next.FanSwitch == previousFanSwitch)
            {
                consecutiveUnexpected++;
                if (consecutiveUnexpected >=
                    RequiredConsecutiveUnexpectedGuardSamples)
                {
                    return next;
                }
            }
            else
            {
                consecutiveUnexpected = 1;
            }

            previousMaxFan = next.MaxFan;
            previousFanSwitch = next.FanSwitch;
            last = next;
        }

        // No sane sample and no stable repeated conflict was obtained within
        // the bounded confirmation budget. Preserve fail-closed behavior by
        // returning the last unexpected state as unhealthy.
        return last;
    }

    private static bool ControlGuardsAreSane(
        Hp8C40EcControlState state) =>
        state.MaxFan == 0 &&
        state.FanSwitch == 0;

    private static void ValidateActiveControlState(
        Hp8C40EcControlState state,
        bool requireRunningTachometers)
    {
        if (state.MaxFan != 0)
        {
            throw new InvalidOperationException(
                $"Fan control state changed: Max Fan is active (EC 0xEC=0x{state.MaxFan:X2}).");
        }

        if (state.FanSwitch != 0)
        {
            throw new InvalidOperationException(
                $"Fan control state changed: fan switch is not ON (EC 0xF4=0x{state.FanSwitch:X2}).");
        }

        if (requireRunningTachometers &&
            (!IsRunningTachometerValid(state.CpuRpm) ||
             !IsRunningTachometerValid(state.GpuRpm)))
        {
            throw new InvalidOperationException(
                $"Fan feedback became invalid before command dispatch: " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}.");
        }
    }

    private static bool IsRunningTachometerValid(ushort rpm) =>
        rpm is > 0 and <= 10_000;

    private static void ValidateCurrentSpeedLevel(byte level, string fanName)
    {
        // OmenMon's GetFanLevel is current-speed telemetry on this platform.
        // Values around the normal fan range are expected; FF is a setpoint
        // sentinel and is not a valid current-speed reading here.
        if (level > 100)
        {
            throw new InvalidDataException(
                $"{fanName} BIOS current fan level is implausible: {level}.");
        }
    }

    private static void ValidateTachometerRange(ushort rpm, string fanName)
    {
        if (rpm > 10_000)
        {
            throw new InvalidDataException(
                $"{fanName} tachometer is implausible during command acknowledgement: {rpm} RPM.");
        }
    }

    private async ValueTask RestoreWithWatchdogLockedAsync(
        CancellationToken cancellationToken)
    {
        Exception? watchdogBeginFailure = null;
        Exception? watchdogReleaseFailure = null;
        var watchdogLeaseRequired = _watchdogLease is not null;

        _lastRestoreEvidence = new FanFirmwareRestoreEvidence(
            LocalFirmwareAckVerified: false,
            WatchdogLeaseRequired: watchdogLeaseRequired,
            WatchdogReleaseVerified: false,
            CompletedAtUtc: DateTimeOffset.MinValue,
            Detail: "Firmware restore is in progress.");

        if (_watchdogLease is not null)
        {
            try
            {
                await _watchdogLease.RestoreBeginAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Watchdog loss must never prevent the still-alive controller
                // from performing its local validated firmware restore.
                watchdogBeginFailure = ex;
            }
        }

        // RestoreLockedAsync cannot return successfully until the production
        // hardware path has issued FF/FF -> LegacyDefault and observed EC
        // setpoints FF/FF through WaitForSetpointAsync.
        await RestoreLockedAsync(cancellationToken).ConfigureAwait(false);

        var watchdogReleaseVerified = !watchdogLeaseRequired;

        if (_watchdogLease is not null)
        {
            try
            {
                // A successful Release response is causal evidence that the
                // service completed/normalized its own validated HP restore,
                // verified FF/FF and deleted the durable lease journal before
                // replying. Gate G consumes this already-paid-for evidence
                // without opening another reader during PBT_APMSUSPEND.
                await _watchdogLease.ReleaseAsync(
                        CancellationToken.None)
                    .ConfigureAwait(false);
                watchdogReleaseVerified = true;
            }
            catch (Exception ex)
            {
                // Hardware is already locally verified FF/FF. Retaining the
                // service journal is safer than reclassifying this handoff as
                // a hardware restore failure; a fresh watchdog handshake must
                // recover/clear it before any later Custom admission.
                watchdogReleaseFailure = ex;
            }
        }

        if (watchdogBeginFailure is not null ||
            watchdogReleaseFailure is not null)
        {
            _lastDetail +=
                $" Watchdog lease handoff degraded but local HP restore succeeded. " +
                $"begin={watchdogBeginFailure?.Message ?? "ok"}; " +
                $"release={watchdogReleaseFailure?.Message ?? "ok"}.";
        }

        _lastRestoreEvidence = new FanFirmwareRestoreEvidence(
            LocalFirmwareAckVerified: true,
            WatchdogLeaseRequired: watchdogLeaseRequired,
            WatchdogReleaseVerified: watchdogReleaseVerified,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            Detail:
                watchdogReleaseVerified
                    ? "Local FF/FF acknowledgement completed and watchdog release was verified before backend return."
                    : $"Local FF/FF acknowledgement completed but watchdog release was not verified: {watchdogReleaseFailure?.Message ?? "watchdog lease unavailable"}");
    }

    private async ValueTask RestoreLockedAsync(CancellationToken cancellationToken)
    {
        // The command is intentionally issued even when _customModeActive is
        // false. Callers can use this after an uncertain/partial transition.
        _hardware!.RestoreFirmwareAuto();

        _ = await WaitForSetpointAsync(
            byte.MaxValue,
            byte.MaxValue,
            _timing.RestoreAckTimeout,
            cancellationToken).ConfigureAwait(false);

        _ownedSetpoint = null;
        _customModeActive = false;
        _lastDetail =
            "HP firmware authority restored; EC setpoints FF/FF.";
    }

    private async ValueTask<(byte CpuSetpoint, byte GpuSetpoint)> WaitForSetpointAsync(
        byte cpuLevel,
        byte gpuLevel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = _activeTimeClock.Milliseconds;
        var transientReadFailures = 0;
        string? lastReadFailure = null;
        (byte CpuSetpoint, byte GpuSetpoint)? last = null;

        while (!ActiveTimeClock.HasElapsed(
                   _activeTimeClock,
                   started,
                   timeout))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                last = _hardware!.ReadSetpoint();
            }
            catch (Exception ex) when (
                ex is TimeoutException ||
                ex is IOException)
            {
                transientReadFailures++;
                lastReadFailure =
                    $"{ex.GetType().Name}: {ex.Message}";

                if (transientReadFailures >
                    MaximumTransientSetpointReadFailures)
                {
                    throw new IOException(
                        $"Setpoint acknowledgement lost EC observability after " +
                        $"{transientReadFailures} failed setpoint snapshots. " +
                        $"The real command remains uncommitted and must be restored fail-closed. " +
                        $"Last failure: {ex.Message}",
                        ex);
                }

                // A Global\Access_EC acquisition timeout already consumed a
                // bounded wait. Retry the complete narrow setpoint snapshot
                // without relaxing ownership or acknowledgement requirements.
                continue;
            }

            if (last.Value.CpuSetpoint == cpuLevel &&
                last.Value.GpuSetpoint == gpuLevel)
            {
                return last.Value;
            }

            await Task.Delay(
                    _timing.PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"EC setpoint acknowledgement timed out after {timeout.TotalSeconds:0.0} s. " +
            $"Expected {cpuLevel}/{gpuLevel}, last " +
            $"{last?.CpuSetpoint.ToString() ?? "n/a"}/{last?.GpuSetpoint.ToString() ?? "n/a"}, " +
            $"transient EC setpoint failures={transientReadFailures}, " +
            $"last EC setpoint failure={lastReadFailure ?? "none"}.");
    }

    private void ValidateCommand(FanCommand command)
    {
        if (command.CpuLevel != command.GpuLevel)
        {
            throw new ArgumentException(
                "HP 8C40 independent CPU/GPU fan levels are not physically qualified. " +
                "Only equal levels are permitted.",
                nameof(command));
        }

        if (command.CpuLevel < _minimumCommandLevel ||
            command.CpuLevel > _maximumCommandLevel ||
            command.GpuLevel < _minimumCommandLevel ||
            command.GpuLevel > _maximumCommandLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                $"HP 8C40 backend fan-level range is " +
                $"{_minimumCommandLevel}-{_maximumCommandLevel}.");
        }
    }

    private void EnsureWritable()
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException(
                $"HP 8C40 fan backend is not write-capable: {_supportDetail}");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private enum TachExpectation
    {
        Steady,
        Increase,
        Decrease
    }
}
