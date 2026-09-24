using VictusFanControl.Control;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

internal interface IHp88F8FanHardware : IDisposable
{
    Hp88F8EcControlState ReadEcState();
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
        var state = _ec.ReadHp88F8ControlState();
        return new Hp88F8EcControlState(
            state.CpuRateTarget,
            state.GpuRateTarget,
            state.CpuRate,
            state.GpuRate,
            state.CpuSetpoint,
            state.GpuSetpoint,
            state.Manual,
            state.Countdown,
            state.Mode,
            state.MaxFan,
            state.FanSwitch,
            state.CpuRpm,
            state.GpuRpm);
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
public sealed class Hp88F8FanControlBackend : IFanControlBackend
{
    private const int DirectionLevelDeadband = 2;
    private const int MinimumDirectionalRpmDelta = 150;
    private const int RequiredTachConfirmationSamples = 2;

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly IHp88F8FanHardware? _hardware;
    private readonly bool _targetSupported;
    private readonly string _supportDetail;
    private readonly Hp88F8FanBackendTiming _timing;

    private bool _customModeActive;
    private bool _disposed;
    private string _lastDetail;
    private (byte Cpu, byte Gpu)? _ownedSetpoint;

    public Hp88F8FanControlBackend(string modulesDirectory)
    {
        var identity = HardwareIdentityReader.ReadCurrent();
        _targetSupported = Hp88F8TargetProfile.Matches(identity, out var reason);
        _supportDetail = reason;
        _timing = Hp88F8FanBackendTiming.Production;

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
        Hp88F8FanBackendTiming? timing = null)
    {
        _hardware = hardware;
        _targetSupported = targetSupported;
        _supportDetail = supportDetail;
        _timing = timing ?? Hp88F8FanBackendTiming.Production;
        _lastDetail = targetSupported
            ? "Synthetic backend initialized; firmware authority retained."
            : $"Write backend disabled: {supportDetail}";
    }

    public string Name => "HP 88F8 BIOS/WMI + EC/tach verification";

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
                    Detail: _lastDetail);
            }

            var state = _hardware!.ReadEcState();
            var ownershipValid =
                !_customModeActive ||
                (_ownedSetpoint.HasValue
                    ? state.CpuSetpoint == _ownedSetpoint.Value.Cpu &&
                      state.GpuSetpoint == _ownedSetpoint.Value.Gpu
                    : state.CpuSetpoint == byte.MaxValue &&
                      state.GpuSetpoint == byte.MaxValue);

            var ownership = !_customModeActive
                ? "firmware/none"
                : ownershipValid
                    ? _ownedSetpoint.HasValue ? "owned" : "reserved/FF"
                    : "OWNERSHIP-MISMATCH";

            var detail =
                $"{_lastDetail} EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}, " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}, ownership={ownership}, " +
                $"manual=0x{state.Manual:X2}, countdown={state.Countdown}.";

            return new FanBackendStatus(
                Name,
                CanWrite: true,
                CustomModeActive: _customModeActive,
                OwnershipValid: ownershipValid,
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
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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

            _ownedSetpoint = null;
            _customModeActive = true;
            _lastDetail =
                $"Custom authority prepared from firmware-auto state; " +
                $"manual=0x{state.Manual:X2}, countdown={state.Countdown}.";
        }
        finally
        {
            _ioGate.Release();
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

            ValidateCommand(command);

            var cpuTarget = checked((byte)command.CpuLevel);
            var gpuTarget = checked((byte)command.GpuLevel);

            var before = _hardware!.ReadEcState();
            VerifyExistingOwnership(before);

            var currentLevels = _hardware.GetCurrentFanLevels();
            ValidateCurrentSpeedLevel(currentLevels.CpuLevel, "CPU");
            ValidateCurrentSpeedLevel(currentLevels.GpuLevel, "GPU");

            // Recheck after the WMI read so an external controller changing the
            // EC setpoint in the admission-to-dispatch window is detected rather
            // than silently overwritten.
            var preDispatch = _hardware.ReadEcState();
            VerifyExistingOwnership(preDispatch);

            if (preDispatch.CpuSetpoint != cpuTarget ||
                preDispatch.GpuSetpoint != gpuTarget)
            {
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

            _ownedSetpoint = (cpuTarget, gpuTarget);
            _lastDetail =
                $"Command {command.CpuLevel}/{command.GpuLevel} acknowledged by EC setpoints " +
                $"and both tachometers; RPM={tachAck.CpuRpm}/{tachAck.GpuRpm}, " +
                $"initial RPM={preDispatch.CpuRpm}/{preDispatch.GpuRpm}, " +
                $"setpoint-ack RPM={setpointAck.CpuRpm}/{setpointAck.GpuRpm}.";
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
            await RestoreLockedAsync(cancellationToken).ConfigureAwait(false);
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
                await RestoreLockedAsync(CancellationToken.None).ConfigureAwait(false);
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

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cpuAcknowledged = false;
        var gpuAcknowledged = false;
        var confirmationSamples = 0;
        Hp88F8EcControlState? last = null;

        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < _timing.TachometerAckTimeout)
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

            cpuAcknowledged |= cpuCurrentlyRunning &&
                HasTachometerResponded(
                    cpuExpectation,
                    baseline.CpuRpm,
                    last.CpuRpm,
                    Hp88F8TargetProfile.CpuObservedMaximumRpm);

            gpuAcknowledged |= gpuCurrentlyRunning &&
                HasTachometerResponded(
                    gpuExpectation,
                    baseline.GpuRpm,
                    last.GpuRpm,
                    Hp88F8TargetProfile.GpuObservedMaximumRpm);

            // Zero RPM can be a legitimate transient immediately after commanding
            // a stopped fan to start. Do not fail instantly; let the bounded ack
            // window observe spin-up. Once acknowledgement has occurred, however,
            // both current confirmation samples must still show both fans running.
            if (cpuAcknowledged &&
                gpuAcknowledged &&
                cpuCurrentlyRunning &&
                gpuCurrentlyRunning)
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
            $"CPU ack={cpuAcknowledged}, GPU ack={gpuAcknowledged}, " +
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

    private async ValueTask RestoreLockedAsync(CancellationToken cancellationToken)
    {
        // The command is intentionally issued even when _customModeActive is
        // false. Callers can use this after an uncertain/partial transition.
        _hardware!.RestoreFirmwareAuto();

        var restored = await WaitForSetpointAsync(
            byte.MaxValue,
            byte.MaxValue,
            _timing.RestoreAckTimeout,
            cancellationToken).ConfigureAwait(false);

        _ownedSetpoint = null;
        _customModeActive = false;
        _lastDetail =
            $"HP firmware authority restored; EC setpoints FF/FF, " +
            $"RPM={restored.CpuRpm}/{restored.GpuRpm}.";
    }

    private async ValueTask<Hp88F8EcControlState> WaitForSetpointAsync(
        byte cpuLevel,
        byte gpuLevel,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Hp88F8EcControlState? last = null;

        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            last = _hardware!.ReadEcState();
            if (last.CpuSetpoint == cpuLevel &&
                last.GpuSetpoint == gpuLevel)
            {
                return last;
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
