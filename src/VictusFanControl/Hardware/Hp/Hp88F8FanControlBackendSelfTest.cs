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
        failures += await TestUnsupportedTargetRefusedAsync(output);
        failures += await TestRangeRefusedAsync(output);
        failures += await TestRestoreVerificationAsync(output);
        failures += await TestCpuTachFailureAsync(output);
        failures += await TestGpuTachFailureAsync(output);
        failures += await TestOwnershipLossAsync(output);
        failures += await TestStatusDetectsOwnershipLossAsync(output);

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

        public Hp88F8EcControlState State { get; set; } = AutoState;
        public int SetCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public bool IgnoreRestore { get; set; }
        public bool FreezeCpuTach { get; set; }
        public bool FreezeGpuTach { get; set; }

        public Hp88F8EcControlState ReadEcState()
        {
            if (_targetCpu.HasValue && _targetGpu.HasValue)
            {
                var cpuDesired = DesiredCpuRpm(_targetCpu.Value);
                var gpuDesired = DesiredGpuRpm(_targetGpu.Value);

                State = State with
                {
                    CpuRpm = FreezeCpuTach
                        ? State.CpuRpm
                        : MoveToward(State.CpuRpm, cpuDesired, 200),
                    GpuRpm = FreezeGpuTach
                        ? State.GpuRpm
                        : MoveToward(State.GpuRpm, gpuDesired, 200)
                };
            }

            return State;
        }

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
            ((byte)Math.Clamp(State.CpuRpm / 100, 0, 255),
             (byte)Math.Clamp(State.GpuRpm / 100, 0, 255));

        public void SetFanLevel(byte cpuLevel, byte gpuLevel)
        {
            SetCalls++;
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
