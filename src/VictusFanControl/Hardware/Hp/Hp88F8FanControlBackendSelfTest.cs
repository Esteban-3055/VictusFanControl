using VictusFanControl.Control;

namespace VictusFanControl.Hardware.Hp;

public static class Hp88F8FanControlBackendSelfTest
{
    private static readonly Hp88F8FanBackendTiming FastTiming = new(
        SetpointAckTimeout: TimeSpan.FromMilliseconds(100),
        RestoreAckTimeout: TimeSpan.FromMilliseconds(100),
        TachometerAckTimeout: TimeSpan.FromMilliseconds(250),
        PollInterval: TimeSpan.FromMilliseconds(10));

    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        failures += await TestHappyPathAsync(output);
        failures += await TestExistingOverrideRefusedAsync(output);
        failures += await TestCancelledAdmissionIsNoWriteAsync(output);
        failures += await TestFirstCommandExternalOverrideIsNoWriteAsync(output);
        failures += await TestUnsupportedTargetRefusedAsync(output);
        failures += await TestRangeRefusedAsync(output);
        failures += await TestRestoreVerificationAsync(output);
        failures += await TestCpuTachFailureAsync(output);
        failures += await TestGpuTachFailureAsync(output);
        failures += await TestOwnershipLossAsync(output);
        failures += await TestStatusDetectsOwnershipLossAsync(output);
        failures += await TestStatusDetectsRuntimeTachFailureAsync(output);
        failures += await TestStoppedFansCanSpinUpWithinAckWindowAsync(output);
        failures += await TestOneSampleDirectionalSpikeIsRejectedAsync(output);
        failures += await TestCancellationAtPreDispatchPreventsWriteAsync(output);

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "Hp88F8FanControlBackend self-test: PASS"
            : $"Hp88F8FanControlBackend self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 12;
    }

    private static async Task<int> TestHappyPathAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "backend-self-test"),
            CancellationToken.None);

        var active = await backend.GetStatusAsync(CancellationToken.None);

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        var restored = await backend.GetStatusAsync(CancellationToken.None);

        return Report(
            output,
            "real backend boundary: enter -> EC+tachs ack -> verified restore",
            hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            active.CustomModeActive &&
            active.Detail.Contains("both tachometers", StringComparison.OrdinalIgnoreCase) &&
            !restored.CustomModeActive);
    }

    private static async Task<int> TestExistingOverrideRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            State = FakeHardware.AutoState with
            {
                CpuSetpoint = 30,
                GpuSetpoint = 30
            }
        };

        await using var backend = NewBackend(hardware);

        var refused = false;
        try
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
        }
        catch (FanControlOwnershipConflictException)
        {
            refused = true;
        }

        return Report(
            output,
            "existing fixed override blocks authority acquisition",
            refused && hardware.SetCalls == 0);
    }


    private static async Task<int> TestCancelledAdmissionIsNoWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var refusedAsNoWrite = false;
        try
        {
            await backend.EnterCustomModeAsync(cts.Token);
        }
        catch (FanControlAdmissionException ex)
        {
            refusedAsNoWrite = ex.InnerException is OperationCanceledException;
        }

        return Report(
            output,
            "cancelled authority admission is classified as no-write",
            refusedAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static async Task<int> TestFirstCommandExternalOverrideIsNoWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var refusedAsNoWrite = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "first-command-race"),
                CancellationToken.None);
        }
        catch (FanControlAdmissionException ex)
        {
            refusedAsNoWrite = ex.InnerException is InvalidOperationException;
        }

        var status = await backend.GetStatusAsync(CancellationToken.None);

        return Report(
            output,
            "external override before first write is preserved as no-write",
            refusedAsNoWrite &&
            hardware.SetCalls == 0 &&
            hardware.RestoreCalls == 0 &&
            hardware.State.CpuSetpoint == 31 &&
            hardware.State.GpuSetpoint == 31 &&
            !status.CustomModeActive);
    }

    private static async Task<int> TestUnsupportedTargetRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = new Hp88F8FanControlBackend(
            hardware,
            targetSupported: false,
            supportDetail: "synthetic mismatch",
            timing: FastTiming);

        var refused = false;
        try
        {
            await backend.EnterCustomModeAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        return Report(
            output,
            "unsupported target remains fail-closed",
            refused && !backend.CanWrite && hardware.SetCalls == 0);
    }

    private static async Task<int> TestRangeRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var refused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(13, 30, "out-of-range"),
                CancellationToken.None);
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend independently enforces 14-50 hard range",
            refused && hardware.SetCalls == 0);
    }

    private static async Task<int> TestRestoreVerificationAsync(TextWriter output)
    {
        var hardware = new FakeHardware { IgnoreRestore = true };
        await using var backend = NewBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "restore-timeout"),
            CancellationToken.None);

        var failed = false;
        try
        {
            await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        // Allow DisposeAsync to finish without repeating the synthetic failure.
        hardware.IgnoreRestore = false;

        return Report(
            output,
            "restore is not accepted until EC returns to FF/FF",
            failed && hardware.State.CpuSetpoint == 30 && hardware.State.GpuSetpoint == 30);
    }

    private static async Task<int> TestCpuTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware { FreezeCpuTach = true };
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "cpu-tach-failure"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "CPU tachometer non-response rejects command",
            failed);
    }

    private static async Task<int> TestGpuTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware { FreezeGpuTach = true };
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var failed = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "gpu-tach-failure"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            failed = true;
        }

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "GPU tachometer non-response rejects command",
            failed);
    }

    private static async Task<int> TestOwnershipLossAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        await backend.ApplyAsync(
            new FanCommand(30, 30, "establish-ownership"),
            CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var refused = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(32, 32, "external-overwrite"),
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            refused = true;
        }

        // Return synthetic state to the owned value so the explicit restore can
        // exercise the normal path rather than hiding the assertion in Dispose.
        hardware.State = hardware.State with
        {
            CpuSetpoint = 30,
            GpuSetpoint = 30
        };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "external setpoint overwrite is detected instead of fought",
            refused && hardware.SetCalls == 1);
    }


    private static async Task<int> TestStatusDetectsOwnershipLossAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "status-ownership"),
            CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 31,
            GpuSetpoint = 31
        };

        var status = await backend.GetStatusAsync(CancellationToken.None);

        hardware.State = hardware.State with
        {
            CpuSetpoint = 30,
            GpuSetpoint = 30
        };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend status exposes active ownership mismatch",
            status.CustomModeActive &&
            !status.OwnershipValid &&
            status.Detail.Contains("OWNERSHIP-MISMATCH", StringComparison.Ordinal));
    }



    private static async Task<int> TestStatusDetectsRuntimeTachFailureAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "runtime-tach-health"),
            CancellationToken.None);

        hardware.FreezeCpuTach = true;
        hardware.State = hardware.State with { CpuRpm = 0 };

        var status = await backend.GetStatusAsync(CancellationToken.None);

        hardware.FreezeCpuTach = false;
        hardware.State = hardware.State with { CpuRpm = 3000 };
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "backend status exposes post-ack tachometer failure",
            status.CustomModeActive &&
            status.OwnershipValid &&
            !status.FeedbackHealthy &&
            status.Detail.Contains("FEEDBACK/CONTROL-STATE-INVALID", StringComparison.Ordinal));
    }

    private static async Task<int> TestOneSampleDirectionalSpikeIsRejectedAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            PulseCpuTachOnceThenReturnBaseline = true
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var rejected = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "single-sample-spike"),
                CancellationToken.None);
        }
        catch (TimeoutException)
        {
            rejected = true;
        }

        hardware.PulseCpuTachOnceThenReturnBaseline = false;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "one-sample directional RPM spike cannot satisfy dual-tach acknowledgement",
            rejected);
    }

    private static async Task<int> TestStoppedFansCanSpinUpWithinAckWindowAsync(TextWriter output)
    {
        var hardware = new FakeHardware
        {
            State = FakeHardware.AutoState with
            {
                CpuRpm = 0,
                GpuRpm = 0
            }
        };

        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        var passed = true;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "spin-up-from-zero"),
                CancellationToken.None);
        }
        catch
        {
            passed = false;
        }

        var state = hardware.State;
        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);

        return Report(
            output,
            "zero RPM is treated as bounded spin-up transient, not immediate tach failure",
            passed &&
            state.CpuRpm > 0 &&
            state.GpuRpm > 0);
    }


    private static async Task<int> TestCancellationAtPreDispatchPreventsWriteAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = NewBackend(hardware);
        await backend.EnterCustomModeAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();

        // EnterCustomMode performs EC read #1. Apply performs its baseline read
        // (#2), then the final pre-dispatch ownership read (#3). Cancel exactly
        // on #3 to verify that no WMI fan-level write can escape the boundary.
        hardware.OnEcRead = count =>
        {
            if (count == 3)
            {
                cts.Cancel();
            }
        };

        var cancelled = false;
        try
        {
            await backend.ApplyAsync(
                new FanCommand(30, 30, "cancel-before-wmi"),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        hardware.OnEcRead = null;

        return Report(
            output,
            "cancellation at pre-dispatch boundary prevents WMI fan write",
            cancelled &&
            hardware.SetCalls == 0 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue);
    }

    private static Hp88F8FanControlBackend NewBackend(FakeHardware hardware) =>
        new(
            hardware,
            targetSupported: true,
            supportDetail: "synthetic validated target",
            timing: FastTiming);

    private static int Report(TextWriter output, string name, bool pass)
    {
        output.WriteLine($"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }

    internal sealed class FakeHardware : IHp88F8FanHardware
    {
        public static readonly Hp88F8EcControlState AutoState = new(
            CpuRateTarget: 255,
            GpuRateTarget: 255,
            CpuRate: 54,
            GpuRate: 54,
            CpuSetpoint: 255,
            GpuSetpoint: 255,
            Manual: 0x06,
            Countdown: 230,
            Mode: 0x00,
            MaxFan: 0x00,
            FanSwitch: 0x00,
            CpuRpm: 2200,
            GpuRpm: 2400);

        private byte? _targetCpu;
        private byte? _targetGpu;
        private ushort _cpuRpmAtCommand;
        private int _postSetReadCount;

        public Hp88F8EcControlState State { get; set; } = AutoState;
        public int SetCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public bool IgnoreRestore { get; set; }
        public bool FreezeCpuTach { get; set; }
        public bool FreezeGpuTach { get; set; }
        public bool PulseCpuTachOnceThenReturnBaseline { get; set; }
        public Action<int>? OnEcRead { get; set; }
        public int EcReadCalls { get; private set; }

        public Hp88F8EcControlState ReadEcState()
        {
            EcReadCalls++;
            OnEcRead?.Invoke(EcReadCalls);
            if (_targetCpu.HasValue && _targetGpu.HasValue)
            {
                var cpuDesired = DesiredCpuRpm(_targetCpu.Value);
                var gpuDesired = DesiredGpuRpm(_targetGpu.Value);

                ushort cpuRpm;
                if (PulseCpuTachOnceThenReturnBaseline)
                {
                    cpuRpm = _postSetReadCount == 1
                        ? (ushort)Math.Min(10_000, _cpuRpmAtCommand + 200)
                        : _cpuRpmAtCommand;
                }
                else
                {
                    cpuRpm = FreezeCpuTach
                        ? State.CpuRpm
                        : MoveToward(State.CpuRpm, cpuDesired, 200);
                }

                State = State with
                {
                    CpuRpm = cpuRpm,
                    GpuRpm = FreezeGpuTach
                        ? State.GpuRpm
                        : MoveToward(State.GpuRpm, gpuDesired, 200)
                };

                _postSetReadCount++;
            }

            return State;
        }

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
            ((byte)Math.Clamp(State.CpuRpm / 100, 0, 255),
             (byte)Math.Clamp(State.GpuRpm / 100, 0, 255));

        public void SetFanLevel(byte cpuLevel, byte gpuLevel)
        {
            SetCalls++;
            _cpuRpmAtCommand = State.CpuRpm;
            _postSetReadCount = 0;
            _targetCpu = cpuLevel;
            _targetGpu = gpuLevel;
            State = State with
            {
                CpuSetpoint = cpuLevel,
                GpuSetpoint = gpuLevel
            };
        }

        public void RestoreFirmwareAuto()
        {
            RestoreCalls++;
            if (!IgnoreRestore)
            {
                _targetCpu = null;
                _targetGpu = null;
                State = State with
                {
                    CpuSetpoint = byte.MaxValue,
                    GpuSetpoint = byte.MaxValue
                };
            }
        }

        public void Dispose()
        {
        }

        private static ushort DesiredCpuRpm(byte level) =>
            (ushort)Math.Min(level * 100, Hp88F8TargetProfile.CpuObservedMaximumRpm);

        private static ushort DesiredGpuRpm(byte level) =>
            (ushort)Math.Min(level * 100, Hp88F8TargetProfile.GpuObservedMaximumRpm);

        private static ushort MoveToward(ushort current, ushort target, int step)
        {
            if (current < target)
            {
                return (ushort)Math.Min(target, current + step);
            }

            if (current > target)
            {
                return (ushort)Math.Max(target, current - step);
            }

            return current;
        }
    }
}
