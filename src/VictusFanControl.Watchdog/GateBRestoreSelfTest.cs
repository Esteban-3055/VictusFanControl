using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

internal static class GateBRestoreSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;

        failures += await RunCaseAsync(
            output,
            "refuse FF/FF without restore",
            new FakeHardware(State(255, 255)),
            expectedSuccess: false,
            expectedRestoreCalls: 0,
            expectedVerifiedFfFf: false);

        failures += await RunCaseAsync(
            output,
            "refuse unknown fixed override",
            new FakeHardware(State(31, 31)),
            expectedSuccess: false,
            expectedRestoreCalls: 0,
            expectedVerifiedFfFf: false);

        failures += await RunCaseAsync(
            output,
            "refuse invalid MaxFan control state",
            new FakeHardware(State(30, 30, maxFan: 1)),
            expectedSuccess: false,
            expectedRestoreCalls: 0,
            expectedVerifiedFfFf: false);

        failures += await RunCaseAsync(
            output,
            "successful 30/30 emergency restore",
            new FakeHardware(
                State(30, 30),
                State(255, 255)),
            expectedSuccess: true,
            expectedRestoreCalls: 1,
            expectedVerifiedFfFf: true);

        failures += await RunCaseAsync(
            output,
            "reported WMI failure still verifies physical FF/FF but does not pass",
            new FakeHardware(
                State(30, 30),
                State(255, 255),
                throwOnRestore: true),
            expectedSuccess: false,
            expectedRestoreCalls: 1,
            expectedVerifiedFfFf: true);

        failures += await RunCaseAsync(
            output,
            "restore success without FF/FF acknowledgement fails",
            new FakeHardware(
                State(30, 30),
                State(30, 30)),
            expectedSuccess: false,
            expectedRestoreCalls: 1,
            expectedVerifiedFfFf: false);

        if (failures == 0)
        {
            output.WriteLine("Watchdog Gate B restore self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"Watchdog Gate B restore self-test: FAIL ({failures} case(s))");
        return 1;
    }

    private static async Task<int> RunCaseAsync(
        TextWriter output,
        string name,
        FakeHardware hardware,
        bool expectedSuccess,
        int expectedRestoreCalls,
        bool expectedVerifiedFfFf)
    {
        var result = await GateBRestoreExecutor.ExecuteAsync(
            hardware,
            verifyTimeout: TimeSpan.FromMilliseconds(8),
            pollInterval: TimeSpan.FromMilliseconds(1),
            log: null,
            CancellationToken.None).ConfigureAwait(false);

        var passed =
            result.Success == expectedSuccess &&
            hardware.RestoreCalls == expectedRestoreCalls &&
            result.VerifiedFfFf == expectedVerifiedFfFf;

        output.WriteLine(
            $"  {(passed ? "PASS" : "FAIL")} {name}: " +
            $"success={result.Success}, restoreCalls={hardware.RestoreCalls}, " +
            $"verifiedFF={result.VerifiedFfFf}, failure={result.Failure ?? "none"}");

        return passed ? 0 : 1;
    }

    private static Hp88F8EcControlState State(
        byte cpu,
        byte gpu,
        byte maxFan = 0,
        byte fanSwitch = 0) =>
        new(
            CpuRateTarget: 255,
            GpuRateTarget: 255,
            CpuRate: 60,
            GpuRate: 60,
            CpuSetpoint: cpu,
            GpuSetpoint: gpu,
            Manual: 0x06,
            Countdown: 200,
            Mode: 0,
            MaxFan: maxFan,
            FanSwitch: fanSwitch,
            CpuRpm: 3000,
            GpuRpm: 3000);

    private sealed class FakeHardware : IGateBRestoreHardware
    {
        private readonly Hp88F8EcControlState _before;
        private readonly Hp88F8EcControlState _after;
        private readonly bool _throwOnRestore;
        private bool _restoreAttempted;

        public FakeHardware(
            Hp88F8EcControlState before,
            Hp88F8EcControlState? after = null,
            bool throwOnRestore = false)
        {
            _before = before;
            _after = after ?? before;
            _throwOnRestore = throwOnRestore;
        }

        public int RestoreCalls { get; private set; }

        public Hp88F8EcControlState ReadEcState() =>
            _restoreAttempted ? _after : _before;

        public void RestoreFirmwareAuto()
        {
            RestoreCalls++;
            _restoreAttempted = true;

            if (_throwOnRestore)
            {
                throw new InvalidOperationException(
                    "synthetic WMI restore failure");
            }
        }

        public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels() =>
            (22, 24);
    }
}
