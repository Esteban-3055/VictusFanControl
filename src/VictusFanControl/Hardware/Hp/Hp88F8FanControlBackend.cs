using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

internal interface IHp88F8FanHardware
{
    Hp88F8EcControlState ReadEcState();
    (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels();
    void SetFanLevel(byte cpuLevel, byte gpuLevel);
    void RestoreFirmwareAuto();
}

internal sealed class Hp88F8FanHardware : IHp88F8FanHardware
{
    private readonly Hp88F8BiosFanControl _bios;
    private readonly Hp88F8EcControlStateProbe _probe;

    public Hp88F8FanHardware(string modulesDirectory)
    {
        _bios = new Hp88F8BiosFanControl();
        _probe = new Hp88F8EcControlStateProbe(modulesDirectory);
    }

    public Hp88F8EcControlState ReadEcState() => _probe.Read();

    public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
        _bios.GetCurrentFanLevels();

    public void SetFanLevel(byte cpuLevel, byte gpuLevel) =>
        _bios.SetFanLevel(cpuLevel, gpuLevel);

    public void RestoreFirmwareAuto() =>
        _bios.RestoreFirmwareAuto();
}

/// <summary>
/// Production HP 88F8 fan-control backend.
///
/// This class is deliberately narrow:
/// - exact target fingerprint only;
/// - ordinary commands restricted to the validated 14-50 range;
/// - no arbitrary EC writes;
/// - fixed-level ownership is acknowledged through EC 0x34/0x35;
/// - firmware restore uses the hardware-validated FF,FF -> LegacyDefault path.
///
/// It does not implement a fan curve. Policy remains outside the backend.
/// </summary>
public sealed class Hp88F8FanControlBackend : IFanControlBackend
{
    private static readonly TimeSpan SetpointAckTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RestoreAckTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly IHp88F8FanHardware? _hardware;
    private readonly bool _targetSupported;
    private readonly string _supportDetail;

    private bool _customModeActive;
    private bool _disposed;
    private string _lastDetail;

    public Hp88F8FanControlBackend(string modulesDirectory)
    {
        var identity = HardwareIdentityReader.ReadCurrent();
        _targetSupported = Hp88F8TargetProfile.Matches(identity, out var reason);
        _supportDetail = reason;

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
        string supportDetail = "Synthetic validated target.")
    {
        _hardware = hardware;
        _targetSupported = targetSupported;
        _supportDetail = supportDetail;
        _lastDetail = targetSupported
            ? "Synthetic backend initialized; firmware authority retained."
            : $"Write backend disabled: {supportDetail}";
    }

    public string Name => "HP 88F8 BIOS/WMI + EC verification";

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
                    Detail: _lastDetail);
            }

            var state = _hardware!.ReadEcState();
            var detail =
                $"{_lastDetail} EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}, " +
                $"RPM={state.CpuRpm}/{state.GpuRpm}, manual=0x{state.Manual:X2}, " +
                $"countdown={state.Countdown}.";

            return new FanBackendStatus(
                Name,
                CanWrite: true,
                CustomModeActive: _customModeActive,
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
                throw new InvalidOperationException(
                    $"Custom fan authority refused because Max Fan is active (EC 0xEC=0x{state.MaxFan:X2}).");
            }

            if (state.FanSwitch != 0)
            {
                throw new InvalidOperationException(
                    $"Custom fan authority refused because the fan switch is not in the validated ON state " +
                    $"(EC 0xF4=0x{state.FanSwitch:X2}).");
            }

            if (state.CpuSetpoint != byte.MaxValue ||
                state.GpuSetpoint != byte.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Custom fan authority refused because an existing fixed override is present " +
                    $"(EC setpoint={state.CpuSetpoint}/{state.GpuSetpoint}). " +
                    "Restore firmware auto first.");
            }

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

            _hardware!.SetFanLevel(
                checked((byte)command.CpuLevel),
                checked((byte)command.GpuLevel));

            var acknowledged = await WaitForSetpointAsync(
                checked((byte)command.CpuLevel),
                checked((byte)command.GpuLevel),
                SetpointAckTimeout,
                cancellationToken).ConfigureAwait(false);

            _lastDetail =
                $"Command {command.CpuLevel}/{command.GpuLevel} acknowledged by EC setpoints; " +
                $"RPM={acknowledged.CpuRpm}/{acknowledged.GpuRpm}.";
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
            _ioGate.Release();
            _ioGate.Dispose();
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
            RestoreAckTimeout,
            cancellationToken).ConfigureAwait(false);

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

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
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
}
