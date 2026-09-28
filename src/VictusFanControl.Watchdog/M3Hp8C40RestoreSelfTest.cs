using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

internal static class M3Hp8C40RestoreSelfTest
{
    private static readonly TimeSpan FastTimeout =
        TimeSpan.FromMilliseconds(8);

    private static readonly TimeSpan FastPoll =
        TimeSpan.FromMilliseconds(1);

    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await CaseAsync(
            output,
            "M3 hardware surface exposes restore-only authority",
            RestoreOnlySurfaceAsync);

        failures += await CaseAsync(
            output,
            "valid fresh 30/30 handoff restores exactly once",
            ValidRestoreAsync);

        failures += await CaseAsync(
            output,
            "stale handoff is no-write",
            StaleHandoffAsync);

        failures += await CaseAsync(
            output,
            "wrong target handoff is no-write",
            WrongTargetAsync);

        failures += await CaseAsync(
            output,
            "dead/mismatched armer identity is no-write",
            InvalidArmerAsync);

        failures += await CaseAsync(
            output,
            "FF/FF before restore is no-write refusal",
            FirmwareAlreadyOwnsAsync);

        failures += await CaseAsync(
            output,
            "unknown fixed override is preserved",
            UnknownOverrideAsync);

        failures += await CaseAsync(
            output,
            "invalid control guards are no-write",
            InvalidGuardAsync);

        failures += await CaseAsync(
            output,
            "reported WMI failure with physical FF/FF does not pass",
            ReportedRestoreFailureAsync);

        failures += await CaseAsync(
            output,
            "restore success without FF/FF acknowledgement fails",
            MissingFfFfAsync);

        if (failures == 0)
        {
            output.WriteLine(
                "HP 8C40 watchdog M3 restore-only self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"HP 8C40 watchdog M3 restore-only self-test: FAIL ({failures} case(s))");
        return 1;
    }

    private static async Task<int> CaseAsync(
        TextWriter output,
        string name,
        Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            output.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                $"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static Task RestoreOnlySurfaceAsync()
    {
        var methods =
            typeof(IM3Hp8C40RestoreHardware)
                .GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        var expected = new[]
        {
            nameof(IM3Hp8C40RestoreHardware.GetCurrentFanLevels),
            nameof(IM3Hp8C40RestoreHardware.ReadControlEvidence),
            nameof(IM3Hp8C40RestoreHardware.RestoreFirmwareAuto)
        };

        Assert(
            methods.SequenceEqual(
                expected,
                StringComparer.Ordinal));

        Assert(
            !methods.Any(
                name =>
                    name.Contains(
                        "SetFanLevel",
                        StringComparison.Ordinal)));

        return Task.CompletedTask;
    }

    private static async Task ValidRestoreAsync()
    {
        var hardware =
            new FakeHardware(
                State(30, 30),
                State(255, 255));

        var result =
            await ExecuteAsync(
                FreshHandoff(),
                hardware,
                processValid: true);

        Assert(result.Success);
        Assert(result.HandoffValidated);
        Assert(result.ArmerIdentityValidated);
        Assert(result.VerifiedFfFf);
        Assert(hardware.RestoreCalls == 1);
    }

    private static async Task StaleHandoffAsync()
    {
        var handoff =
            FreshHandoff() with
            {
                CreatedAtUtc =
                    DateTimeOffset.UtcNow -
                    TimeSpan.FromMinutes(1)
            };

        await AssertNoWriteAsync(
            handoff,
            new FakeHardware(
                State(30, 30)),
            processValid: true);
    }

    private static async Task WrongTargetAsync()
    {
        await AssertNoWriteAsync(
            FreshHandoff() with
            {
                TargetProfileId =
                    "HP-88F8-62C37LA-F32"
            },
            new FakeHardware(
                State(30, 30)),
            processValid: true);
    }

    private static async Task InvalidArmerAsync()
    {
        await AssertNoWriteAsync(
            FreshHandoff(),
            new FakeHardware(
                State(30, 30)),
            processValid: false);
    }

    private static async Task FirmwareAlreadyOwnsAsync()
    {
        await AssertNoWriteAsync(
            FreshHandoff(),
            new FakeHardware(
                State(255, 255)),
            processValid: true);
    }

    private static async Task UnknownOverrideAsync()
    {
        await AssertNoWriteAsync(
            FreshHandoff(),
            new FakeHardware(
                State(31, 31)),
            processValid: true);
    }

    private static async Task InvalidGuardAsync()
    {
        await AssertNoWriteAsync(
            FreshHandoff(),
            new FakeHardware(
                State(
                    30,
                    30,
                    maxFan: 1)),
            processValid: true);
    }

    private static async Task ReportedRestoreFailureAsync()
    {
        var hardware =
            new FakeHardware(
                State(30, 30),
                State(255, 255),
                throwOnRestore: true);

        var result =
            await ExecuteAsync(
                FreshHandoff(),
                hardware,
                processValid: true);

        Assert(!result.Success);
        Assert(result.VerifiedFfFf);
        Assert(hardware.RestoreCalls == 1);
    }

    private static async Task MissingFfFfAsync()
    {
        var hardware =
            new FakeHardware(
                State(30, 30),
                State(30, 30));

        var result =
            await ExecuteAsync(
                FreshHandoff(),
                hardware,
                processValid: true);

        Assert(!result.Success);
        Assert(!result.VerifiedFfFf);
        Assert(hardware.RestoreCalls == 1);
    }

    private static async Task AssertNoWriteAsync(
        Hp8C40M3HandoffRecord handoff,
        FakeHardware hardware,
        bool processValid)
    {
        var result =
            await ExecuteAsync(
                handoff,
                hardware,
                processValid);

        Assert(!result.Success);
        Assert(hardware.RestoreCalls == 0);
    }

    private static Task<M3Hp8C40RestoreExecution>
        ExecuteAsync(
            Hp8C40M3HandoffRecord handoff,
            FakeHardware hardware,
            bool processValid) =>
        M3Hp8C40RestoreExecutor.ExecuteAsync(
            handoff,
            hardware,
            new FakeProcessValidator(
                processValid),
            DateTimeOffset.UtcNow,
            FastTimeout,
            FastPoll,
            log: null,
            CancellationToken.None);

    private static Hp8C40M3HandoffRecord
        FreshHandoff() =>
        new(
            SchemaVersion:
                Hp8C40M3HandoffRecord.CurrentSchemaVersion,
            TargetProfileId:
                Hp8C40TargetProfile.Instance.Id,
            ArmRunId:
                Guid.Parse(
                    "11111111-1111-1111-1111-111111111111"),
            Nonce:
                Guid.Parse(
                    "22222222-2222-2222-2222-222222222222"),
            ArmProcessId:
                4242,
            ArmProcessStartUtcTicks:
                DateTime.UtcNow.Ticks,
            CreatedAtUtc:
                DateTimeOffset.UtcNow,
            ExpectedCpuSetpoint:
                30,
            ExpectedGpuSetpoint:
                30,
            BaselineWasFirmwareOwned:
                true,
            MaxFanAtArm:
                0,
            FanSwitchAtArm:
                0,
            CpuRpmAtArm:
                3000,
            GpuRpmAtArm:
                3000);

    private static Hp8C40EcControlState State(
        byte cpu,
        byte gpu,
        byte maxFan = 0,
        byte fanSwitch = 0) =>
        new(
            CpuRateTarget: 255,
            GpuRateTarget: 255,
            CpuRate: 255,
            GpuRate: 255,
            CpuSetpoint: cpu,
            GpuSetpoint: gpu,
            Diagnostic62: 255,
            Diagnostic63: 255,
            Mode: 255,
            MaxFan: maxFan,
            FanSwitch: fanSwitch,
            CpuRpm: 3000,
            GpuRpm: 3000);

    private static void Assert(
        bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Assertion failed.");
        }
    }

    private sealed class FakeProcessValidator :
        IM3ArmProcessValidator
    {
        private readonly bool _valid;

        public FakeProcessValidator(
            bool valid)
        {
            _valid = valid;
        }

        public bool Matches(
            int processId,
            long processStartUtcTicks,
            out string detail)
        {
            detail =
                _valid
                    ? "synthetic process identity match"
                    : "synthetic process identity mismatch";

            return _valid;
        }
    }

    private sealed class FakeHardware :
        IM3Hp8C40RestoreHardware
    {
        private readonly Hp8C40EcControlState _before;
        private readonly Hp8C40EcControlState _after;
        private readonly bool _throwOnRestore;
        private bool _restoreAttempted;

        public FakeHardware(
            Hp8C40EcControlState before,
            Hp8C40EcControlState? after = null,
            bool throwOnRestore = false)
        {
            _before = before;
            _after = after ?? before;
            _throwOnRestore =
                throwOnRestore;
        }

        public int RestoreCalls { get; private set; }

        public Hp8C40EcControlState ReadControlEvidence() =>
            _restoreAttempted
                ? _after
                : _before;

        public void RestoreFirmwareAuto()
        {
            RestoreCalls++;
            _restoreAttempted = true;

            if (_throwOnRestore)
            {
                throw new InvalidOperationException(
                    "synthetic restore failure");
            }
        }

        public (byte CpuLevel, byte GpuLevel)
            GetCurrentFanLevels() =>
            (26, 24);
    }
}
