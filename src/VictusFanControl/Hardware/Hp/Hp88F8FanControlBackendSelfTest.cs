using VictusFanControl.Control;

namespace VictusFanControl.Hardware.Hp;

public static class Hp88F8FanControlBackendSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        failures += await TestHappyPathAsync(output);
        failures += await TestExistingOverrideRefusedAsync(output);
        failures += await TestUnsupportedTargetRefusedAsync(output);
        failures += await TestRangeRefusedAsync(output);
        failures += await TestRestoreVerificationAsync(output);

        output.WriteLine();
        output.WriteLine(failures == 0
            ? "Hp88F8FanControlBackend self-test: PASS"
            : $"Hp88F8FanControlBackend self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 12;
    }

    private static async Task<int> TestHappyPathAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = new Hp88F8FanControlBackend(hardware);

        await backend.EnterCustomModeAsync(CancellationToken.None);
        await backend.ApplyAsync(
            new FanCommand(30, 30, "backend-self-test"),
            CancellationToken.None);

        var active = await backend.GetStatusAsync(CancellationToken.None);

        await backend.RestoreFirmwareAutoAsync(CancellationToken.None);
        var restored = await backend.GetStatusAsync(CancellationToken.None);

        return Report(
            output,
            "real backend boundary: enter -> apply -> verified restore",
            hardware.SetCalls == 1 &&
            hardware.RestoreCalls == 1 &&
            hardware.State.CpuSetpoint == byte.MaxValue &&
            hardware.State.GpuSetpoint == byte.MaxValue &&
            active.CustomModeActive &&
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

        await using var backend = new Hp88F8FanControlBackend(hardware);

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
            "existing fixed override blocks authority acquisition",
            refused && hardware.SetCalls == 0);
    }

    private static async Task<int> TestUnsupportedTargetRefusedAsync(TextWriter output)
    {
        var hardware = new FakeHardware();
        await using var backend = new Hp88F8FanControlBackend(
            hardware,
            targetSupported: false,
            supportDetail: "synthetic mismatch");

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
        await using var backend = new Hp88F8FanControlBackend(hardware);
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
        await using var backend = new Hp88F8FanControlBackend(hardware);

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

        public Hp88F8EcControlState State { get; set; } = AutoState;
        public int SetCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public bool IgnoreRestore { get; set; }

        public Hp88F8EcControlState ReadEcState() => State;

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
            ((byte)Math.Clamp(State.CpuRpm / 100, 0, 255),
             (byte)Math.Clamp(State.GpuRpm / 100, 0, 255));

        public void SetFanLevel(byte cpuLevel, byte gpuLevel)
        {
            SetCalls++;
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
                State = State with
                {
                    CpuSetpoint = byte.MaxValue,
                    GpuSetpoint = byte.MaxValue
                };
            }
        }
    }
}
