using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

internal static class M4Hp8C40LeaseSelfTest
{
    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await CaseAsync(
            output,
            "M4 service hardware surface remains restore-only",
            RestoreOnlyHardwareSurfaceAsync);

        failures += await CaseAsync(
            output,
            "M4 target policy is equal-only 10-50",
            TargetPolicyAsync);

        failures += await CaseAsync(
            output,
            "M4 protocol uses target-bound v2 contract and isolated pipe",
            ProtocolContractAsync);

        failures += await CaseAsync(
            output,
            "M4 A/B/C gate mapping pins only 30/10/50 and exact tokens",
            EndpointGateMappingAsync);

        if (failures == 0)
        {
            output.WriteLine(
                "HP 8C40 watchdog M4 lease preparation self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"HP 8C40 watchdog M4 lease preparation self-test: FAIL ({failures} case(s))");
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

    private static Task RestoreOnlyHardwareSurfaceAsync()
    {
        var methods =
            typeof(ILeaseRecoveryHardware)
                .GetMethods()
                .Select(method => method.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

        Assert(
            methods.SequenceEqual(
                new[]
                {
                    nameof(ILeaseRecoveryHardware.ReadSetpointAsync),
                    nameof(ILeaseRecoveryHardware.RestoreFirmwareAutoAsync)
                },
                StringComparer.Ordinal));

        Assert(
            typeof(M4Hp8C40LeaseHardware)
                .GetMethods()
                .All(
                    method =>
                        !string.Equals(
                            method.Name,
                            "SetFanLevel",
                            StringComparison.Ordinal)));

        return Task.CompletedTask;
    }

    private static Task TargetPolicyAsync()
    {
        var policy =
            WatchdogTargetPolicies.Hp8C40;

        Assert(
            string.Equals(
                policy.TargetProfileId,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal));

        Assert(
            policy.IsValidatedCustom(
                new FanSetpoint(10, 10)));
        Assert(
            policy.IsValidatedCustom(
                new FanSetpoint(30, 30)));
        Assert(
            policy.IsValidatedCustom(
                new FanSetpoint(50, 50)));

        Assert(
            !policy.IsValidatedCustom(
                new FanSetpoint(9, 9)));
        Assert(
            !policy.IsValidatedCustom(
                new FanSetpoint(10, 11)));
        Assert(
            !policy.IsValidatedCustom(
                new FanSetpoint(51, 51)));

        return Task.CompletedTask;
    }

    private static Task EndpointGateMappingAsync()
    {
        Assert(
            Hp8C40M4LeaseQualificationTest.GetGateName(30) == "M4A");
        Assert(
            Hp8C40M4LeaseQualificationTest.GetRequiredToken(30) ==
            Hp8C40M4LeaseQualificationTest.RequiredToken30);

        Assert(
            Hp8C40M4LeaseQualificationTest.GetGateName(10) == "M4B");
        Assert(
            Hp8C40M4LeaseQualificationTest.GetRequiredToken(10) ==
            Hp8C40M4LeaseQualificationTest.RequiredToken10);

        Assert(
            Hp8C40M4LeaseQualificationTest.GetGateName(50) == "M4C");
        Assert(
            Hp8C40M4LeaseQualificationTest.GetRequiredToken(50) ==
            Hp8C40M4LeaseQualificationTest.RequiredToken50);

        var refused = false;
        try
        {
            _ =
                Hp8C40M4LeaseQualificationTest.GetRequiredToken(20);
        }
        catch (ArgumentOutOfRangeException)
        {
            refused = true;
        }

        Assert(refused);
        return Task.CompletedTask;
    }

    private static Task ProtocolContractAsync()
    {
        Assert(
            FanControlWatchdogLeaseContract.ProtocolVersion == 2);

        Assert(
            !string.Equals(
                FanControlWatchdogLeaseContract.Hp8C40M4PipeName,
                FanControlWatchdogLeaseContract.PipeName,
                StringComparison.Ordinal));

        var request =
            new FanControlWatchdogLeaseRequest(
                FanControlWatchdogLeaseContract.ProtocolVersion,
                Guid.NewGuid(),
                Hp8C40TargetProfile.Instance.Id,
                FanControlWatchdogLeaseContract.Hello,
                ControllerPid: 1234,
                ControllerStartUtcTicks: 5678);

        Assert(
            request.TargetProfileId ==
            Hp8C40TargetProfile.Instance.Id);

        return Task.CompletedTask;
    }

    private static void Assert(
        bool condition)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                "Assertion failed.");
        }
    }
}
