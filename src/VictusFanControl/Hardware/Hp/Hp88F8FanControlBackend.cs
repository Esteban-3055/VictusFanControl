using VictusFanControl.Control;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;

namespace VictusFanControl.Hardware.Hp;

internal interface IHp88F8FanHardware : IDisposable
{
    Hp88F8EcControlState ReadEcState();

    (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
    {
        var state = ReadEcState();
        return (state.CpuSetpoint, state.GpuSetpoint);
    }

    (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels();
    void SetFanLevel(byte cpuLevel, byte gpuLevel);
    void RestoreFirmwareAuto();
}

internal sealed class Hp88F8FanHardware : IHp88F8FanHardware
{
    private readonly Hp88F8BiosFanControl _bios;
    private readonly AcpiEcReader _ec;

    public Hp88F8FanHardware(string modulesDirectory)
    {
        _bios = new Hp88F8BiosFanControl();
        _ec = new AcpiEcReader(Path.Combine(modulesDirectory, "LpcACPIEC.bin"));
    }

    public Hp88F8EcControlState ReadEcState()
    {
        // The control path consumes only ownership, MaxFan/FanSwitch and the
        // two physical tachometers. Keep those as three independently retried
        // narrow EC snapshots instead of reopening the broad diagnostic state.
        var setpoint = _ec.ReadHp88F8Setpoint();
        var controlGuard = _ec.ReadHp88F8FanControlGuard();
        var tachometers = _ec.ReadFanTachometers();

        return new Hp88F8EcControlState(
            CpuRateTarget: byte.MaxValue,
            GpuRateTarget: byte.MaxValue,
            CpuRate: byte.MaxValue,
            GpuRate: byte.MaxValue,
            CpuSetpoint: setpoint.CpuSetpoint,
            GpuSetpoint: setpoint.GpuSetpoint,
            Manual: byte.MaxValue,
            Countdown: byte.MaxValue,
            Mode: byte.MaxValue,
            MaxFan: controlGuard.MaxFan,
            FanSwitch: controlGuard.FanSwitch,
            CpuRpm: tachometers.CpuRpm,
            GpuRpm: tachometers.GpuRpm);
    }

    public (byte CpuSetpoint, byte GpuSetpoint) ReadSetpoint()
    {
        var state = _ec.ReadHp88F8Setpoint();
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

internal readonly record struct Hp88F8FanBackendTiming(
    TimeSpan SetpointAckTimeout,
    TimeSpan RestoreAckTimeout,
    TimeSpan TachometerAckTimeout,
    TimeSpan PollInterval)
{
    public static Hp88F8FanBackendTiming Production => new(
        SetpointAckTimeout: TimeSpan.FromMilliseconds(1500),
        RestoreAckTimeout: TimeSpan.FromSeconds(5),
        TachometerAckTimeout: TimeSpan.FromSeconds(8),
        PollInterval: TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Production HP 88F8 fan-control backend.
///
/// This class is deliberately narrow:
/// - exact target fingerprint only;
/// - ordinary commands restricted to the validated 14-50 range;
/// - no arbitrary EC writes;
/// - fixed-level ownership is acknowledged through EC 0x34/0x35;
/// - both physical tachometers must acknowledge every new command;
/// - firmware restore uses the hardware-validated FF,FF -> LegacyDefault path.
///
/// It does not implement a fan curve. Policy remains outside the backend.
/// </summary>
public sealed class Hp88F8FanControlBackend :
    IFanControlBackend,
    IFanControlRestoreEvidenceSource
{
    private const int DirectionLevelDeadband = 2;
    private const int MinimumDirectionalRpmDelta = 150;
    private const int RequiredTachConfirmationSamples = 2;

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly IHp88F8FanHardware? _hardware;
    private readonly bool _targetSupported;
    private readonly string _supportDetail;
    private readonly Hp88F8FanBackendTiming _timing;
    private readonly IFanControlWatchdogLeaseClient? _watchdogLease;
    private readonly IActiveTimeClock _activeTimeClock;

    private bool _customModeActive;
    private bool _disposed;
    private string _lastDetail;
    private (byte Cpu, byte Gpu)? _ownedSetpoint;
    private FanFirmwareRestoreEvidence _lastRestoreEvidence;

    public Hp88F8FanControlBackend(
        string modulesDirectory,
        IFanControlWatchdogLeaseClient? watchdogLease = null)
    {
        var identity = HardwareIdentityReader.ReadCurrent();
        _targetSupported = Hp88F8TargetProfile.Matches(identity, out var reason);
        _supportDetail = reason;
        _timing = Hp88F8FanBackendTiming.Production;
        _watchdogLease = watchdogLease;
        _activeTimeClock = new WindowsActiveTimeClock();
        _lastRestoreEvidence = new FanFirmwareRestoreEvidence(
            LocalFirmwareAckVerified: false,
            WatchdogLeaseRequired: _watchdogLease is not null,
            WatchdogReleaseVerified: false,
            CompletedAtUtc: DateTimeOffset.MinValue,
            Detail: "No firmware restore has completed in this backend instance.");

        if (_targetSupported)
        {
            _hardware = new Hp88F8FanHardware(modulesDirectory);
            _lastDetail = "Validated HP 88F8 backend initialized; firmware authority retained.";
        }
        else
        {
            _lastDetail = $"Write backend disabled: {reason}";
        }
    }

    internal Hp88F8FanControlBackend(
        IHp88F8FanHardware hardware,
        bool targetSupported = true,
        string supportDetail = "Synthetic validated target.",
        Hp88F8FanBackendTiming? timing = null,
        IFanControlWatchdogLeaseClient? watchdogLease = null,
        IActiveTimeClock? activeTimeClock = null)
    {
        _hardware = hardware;
        _targetSupported = targetSupported;
        _supportDetail = supportDetail;
        _timing = timing ?? Hp88F8FanBackendTiming.Production;
        _watchdogLease = watchdogLease;
        _activeTimeClock = activeTimeClock ?? new WindowsActiveTimeClock();
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

    public string Name => "HP 88F8 BIOS/WMI + EC/tach verification";

    public FanFirmwareRestoreEvidence LastRestoreEvidence =>
        _lastRestoreEvidence;

    public bool CanWrite =>
        !_disposed &&
        _targetSupported &&
        _hardware is not null;

    public FanBackendCapabilities Capabilities =>
        new(
            Hp88F8TargetProfile.BoardProduct,
            Hp88F8TargetProfile.MinimumValidatedFanLevel,
            Hp88F8TargetProfile.MaximumValidatedFanLevel,
            SupportsIndependentLevels: true);

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

            Hp88F8EcControlState state;
            try
            {
                state = _hardware!.ReadEcState();
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

            var detail =
                $"{_lastDetail} EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}, " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}, ownership={ownership}, feedback={feedback}, " +
                $"max=0x{state.MaxFan:X2}, switch=0x{state.FanSwitch:X2}.";

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

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cpuTarget = checked((byte)command.CpuLevel);
                var gpuTarget = checked((byte)command.GpuLevel);

                var before = _hardware!.ReadEcState();
                VerifyExistingOwnership(before);
                ValidateActiveControlState(
                    before,
                    requireRunningTachometers: _ownedSetpoint.HasValue);

                var currentLevels = _hardware.GetCurrentFanLevels();
                ValidateCurrentSpeedLevel(currentLevels.CpuLevel, "CPU");
                ValidateCurrentSpeedLevel(currentLevels.GpuLevel, "GPU");

                var preDispatch = _hardware.ReadEcState();
                VerifyExistingOwnership(preDispatch);
                ValidateActiveControlState(
                    preDispatch,
                    requireRunningTachometers: _ownedSetpoint.HasValue);

                cancellationToken.ThrowIfCancellationRequested();

                if (preDispatch.CpuSetpoint != cpuTarget ||
                    preDispatch.GpuSetpoint != gpuTarget)
                {
                    if (_watchdogLease is not null)
                    {
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
                        var postIntent = _hardware.ReadEcState();
                        VerifyExistingOwnership(postIntent);
                        ValidateActiveControlState(
                            postIntent,
                            requireRunningTachometers: _ownedSetpoint.HasValue);

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Set this before WMI dispatch: on HP hardware the command
                    // may take effect even if WMI subsequently reports failure.
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
                _lastDetail =
                    leaseRollbackFailure is null
                        ? "First custom command was refused before any fan write; logical/watchdog authority was released without FF,FF."
                        : $"First custom command was refused before any fan write; watchdog rollback remained armed: {leaseRollbackFailure.Message}";

                throw new FanControlAdmissionException(
                    "First custom fan command failed before any fan write was attempted.",
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

    private void VerifyExistingOwnership(Hp88F8EcControlState state)
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

    private async ValueTask<Hp88F8EcControlState> WaitForTachometerResponseAsync(
        byte cpuTarget,
        byte gpuTarget,
        (byte CpuLevel, byte GpuLevel) currentLevels,
        Hp88F8EcControlState baseline,
        CancellationToken cancellationToken)
    {
        var cpuExpectation = DetermineExpectation(cpuTarget, currentLevels.CpuLevel);
        var gpuExpectation = DetermineExpectation(gpuTarget, currentLevels.GpuLevel);

        var started = _activeTimeClock.Milliseconds;
        var cpuEverAcknowledged = false;
        var gpuEverAcknowledged = false;
        var confirmationSamples = 0;
        Hp88F8EcControlState? last = null;

        while (!ActiveTimeClock.HasElapsed(
                   _activeTimeClock,
                   started,
                   _timing.TachometerAckTimeout))
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = _hardware!.ReadEcState();

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
                    Hp88F8TargetProfile.CpuObservedMaximumRpm);

            var gpuSampleAcknowledged = gpuCurrentlyRunning &&
                HasTachometerResponded(
                    gpuExpectation,
                    baseline.GpuRpm,
                    last.GpuRpm,
                    Hp88F8TargetProfile.GpuObservedMaximumRpm);

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
            $"baseline current-level={currentLevels.CpuLevel}/{currentLevels.GpuLevel}.");
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



    private static void ValidateActiveControlState(
        Hp88F8EcControlState state,
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
        (byte CpuSetpoint, byte GpuSetpoint)? last = null;

        while (!ActiveTimeClock.HasElapsed(
                   _activeTimeClock,
                   started,
                   timeout))
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = _hardware!.ReadSetpoint();
            if (last.Value.CpuSetpoint == cpuLevel &&
                last.Value.GpuSetpoint == gpuLevel)
            {
                return last.Value;
            }

            await Task.Delay(_timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"EC setpoint acknowledgement timed out after {timeout.TotalSeconds:0.0} s. " +
            $"Expected {cpuLevel}/{gpuLevel}, last " +
            $"{last?.CpuSetpoint.ToString() ?? "n/a"}/{last?.GpuSetpoint.ToString() ?? "n/a"}.");
    }

    private static void ValidateCommand(FanCommand command)
    {
        if (command.CpuLevel < Hp88F8TargetProfile.MinimumValidatedFanLevel ||
            command.CpuLevel > Hp88F8TargetProfile.MaximumValidatedFanLevel ||
            command.GpuLevel < Hp88F8TargetProfile.MinimumValidatedFanLevel ||
            command.GpuLevel > Hp88F8TargetProfile.MaximumValidatedFanLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command),
                command,
                $"HP 88F8 validated fan-level range is " +
                $"{Hp88F8TargetProfile.MinimumValidatedFanLevel}-" +
                $"{Hp88F8TargetProfile.MaximumValidatedFanLevel}.");
        }
    }

    private void EnsureWritable()
    {
        if (!CanWrite)
        {
            throw new InvalidOperationException(
                $"HP 88F8 fan backend is not write-capable: {_supportDetail}");
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
