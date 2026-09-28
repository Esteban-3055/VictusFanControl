using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

internal static class M2Hp8C40ReadOnlySelfTest
{
    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await CaseAsync(
            output,
            "M2 hardware interface exposes read-only methods only",
            InterfaceIsReadOnlyAsync);

        failures += await CaseAsync(
            output,
            "M2 exact 8C40 LocalSystem Session-0 path succeeds",
            ExactTargetSucceedsAsync);

        failures += await CaseAsync(
            output,
            "M2 wrong service context fails before hardware access",
            WrongContextFailsBeforeHardwareAsync);

        failures += await CaseAsync(
            output,
            "M2 target mismatch refuses EC/WMI reads",
            TargetMismatchFailsBeforeDependencyReadsAsync);

        failures += await CaseAsync(
            output,
            "M2 WMI read failure remains fail-closed",
            WmiFailureIsReportedAsync);

        if (failures == 0)
        {
            output.WriteLine(
                "HP 8C40 watchdog M2 read-only self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"HP 8C40 watchdog M2 read-only self-test: FAIL ({failures} case(s))");
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

    private static Task InterfaceIsReadOnlyAsync()
    {
        var methods =
            typeof(IM2Hp8C40ReadOnlyHardware)
                .GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        var expected = new[]
        {
            nameof(IM2Hp8C40ReadOnlyHardware.ReadBiosCurrentLevels),
            nameof(IM2Hp8C40ReadOnlyHardware.ReadEcSetpoint),
            nameof(IM2Hp8C40ReadOnlyHardware.ReadHardwareIdentity)
        };

        Assert(
            methods.SequenceEqual(
                expected,
                StringComparer.Ordinal),
            "M2 hardware interface gained an unexpected method: " +
            string.Join(", ", methods));

        Assert(
            methods.All(
                method =>
                    method.StartsWith(
                        "Read",
                        StringComparison.Ordinal)),
            "M2 hardware interface contains a non-read operation.");

        return Task.CompletedTask;
    }

    private static Task ExactTargetSucceedsAsync()
    {
        var hardware =
            new FakeReadOnlyHardware(
                Exact8C40Identity());

        var result =
            M2Hp8C40ReadOnlyProbe.Evaluate(
                ExactContext(),
                hardware,
                modulePresent: true,
                timestamp:
                    DateTimeOffset.UnixEpoch,
                runId:
                    Guid.Parse(
                        "11111111-1111-1111-1111-111111111111"));

        Assert(result.Success);
        Assert(result.TargetMatched);
        Assert(result.EcReadSucceeded);
        Assert(result.WmiReadSucceeded);
        Assert(result.CpuSetpoint == 255);
        Assert(result.GpuSetpoint == 255);
        Assert(
            result.TargetProfileId ==
            Hp8C40TargetProfile.Instance.Id);
        Assert(hardware.IdentityReads == 1);
        Assert(hardware.EcReads == 1);
        Assert(hardware.WmiReads == 1);

        return Task.CompletedTask;
    }

    private static Task WrongContextFailsBeforeHardwareAsync()
    {
        var hardware =
            new FakeReadOnlyHardware(
                Exact8C40Identity());

        var context =
            ExactContext() with
            {
                SessionId = 1,
                UserSid = "S-1-5-21-test"
            };

        var result =
            M2Hp8C40ReadOnlyProbe.Evaluate(
                context,
                hardware,
                modulePresent: true);

        Assert(!result.Success);
        Assert(hardware.IdentityReads == 0);
        Assert(hardware.EcReads == 0);
        Assert(hardware.WmiReads == 0);

        return Task.CompletedTask;
    }

    private static Task TargetMismatchFailsBeforeDependencyReadsAsync()
    {
        var mismatch =
            Exact8C40Identity() with
            {
                BoardProduct = "88F8"
            };

        var hardware =
            new FakeReadOnlyHardware(
                mismatch);

        var result =
            M2Hp8C40ReadOnlyProbe.Evaluate(
                ExactContext(),
                hardware,
                modulePresent: true);

        Assert(!result.Success);
        Assert(!result.TargetMatched);
        Assert(hardware.IdentityReads == 1);
        Assert(hardware.EcReads == 0);
        Assert(hardware.WmiReads == 0);

        return Task.CompletedTask;
    }

    private static Task WmiFailureIsReportedAsync()
    {
        var hardware =
            new FakeReadOnlyHardware(
                Exact8C40Identity())
            {
                ThrowOnWmiRead = true
            };

        var result =
            M2Hp8C40ReadOnlyProbe.Evaluate(
                ExactContext(),
                hardware,
                modulePresent: true);

        Assert(!result.Success);
        Assert(result.TargetMatched);
        Assert(result.EcReadSucceeded);
        Assert(result.WmiReadAttempted);
        Assert(!result.WmiReadSucceeded);
        Assert(hardware.IdentityReads == 1);
        Assert(hardware.EcReads == 1);
        Assert(hardware.WmiReads == 1);

        return Task.CompletedTask;
    }

    private static M2Hp8C40ProbeContext ExactContext() =>
        new(
            ProcessId: 4242,
            SessionId: 0,
            AccountName:
                @"NT AUTHORITY\SYSTEM",
            UserSid:
                "S-1-5-18",
            ModulesDirectory:
                @"C:\Synthetic\modules");

    private static HardwareIdentity Exact8C40Identity() =>
        new(
            BoardManufacturer:
                Hp8C40TargetProfile.BoardManufacturer,
            BoardProduct:
                Hp8C40TargetProfile.BoardProduct,
            BoardVersion:
                Hp8C40TargetProfile.BoardVersion,
            SystemManufacturer:
                Hp8C40TargetProfile.SystemManufacturer,
            SystemProductName:
                Hp8C40TargetProfile.SystemProductName,
            SystemSku:
                Hp8C40TargetProfile.SystemSkuPrefix +
                "#AKH",
            BiosVersion:
                Hp8C40TargetProfile.ValidatedBiosVersion);

    private static void Assert(
        bool condition,
        string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message ??
                "Assertion failed.");
        }
    }

    private sealed class FakeReadOnlyHardware :
        IM2Hp8C40ReadOnlyHardware
    {
        private readonly HardwareIdentity _identity;

        public FakeReadOnlyHardware(
            HardwareIdentity identity)
        {
            _identity = identity;
        }

        public int IdentityReads { get; private set; }
        public int EcReads { get; private set; }
        public int WmiReads { get; private set; }

        public bool ThrowOnWmiRead { get; init; }

        public HardwareIdentity ReadHardwareIdentity()
        {
            IdentityReads++;
            return _identity;
        }

        public (byte CpuSetpoint, byte GpuSetpoint) ReadEcSetpoint()
        {
            EcReads++;
            return (byte.MaxValue, byte.MaxValue);
        }

        public (byte CpuLevel, byte GpuLevel) ReadBiosCurrentLevels()
        {
            WmiReads++;

            if (ThrowOnWmiRead)
            {
                throw new InvalidOperationException(
                    "synthetic WMI failure");
            }

            return (31, 31);
        }
    }
}
