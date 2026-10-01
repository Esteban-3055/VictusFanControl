using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public static class Hp8C40ProductionWatchdogGateSelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;
        var exact = ExactTargetIdentity();

        failures += Report(
            output,
            "M9 production watchdog is promoted while M9C/M9D qualification gates remain closed",
            Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized &&
            !Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationConstructionAuthorized &&
            !Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationConstructionAuthorized &&
            Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated);

        var authorized =
            Hp8C40ProductionWatchdogGate
                .IsProductionConstructionAuthorizedFor(
                    exact,
                    out var reason);

        failures += Report(
            output,
            "exact HP 8C40 production watchdog construction is authorized after M9 promotion",
            authorized &&
            reason.Contains(
                "authorized",
                StringComparison.OrdinalIgnoreCase));

        var lease =
            Hp8C40ProductionWatchdogGate
                .CreateLeaseIfAuthorized(exact);

        failures += Report(
            output,
            "normal M9 wiring creates only the target-bound named-pipe lease client after promotion",
            lease is NamedPipeFanControlWatchdogLeaseClient);

        var requirePassed = true;
        try
        {
            Hp8C40ProductionWatchdogGate
                .RequireProductionConstructionAuthorized(exact);
        }
        catch
        {
            requirePassed = false;
        }

        failures += Report(
            output,
            "production authorization check passes without constructing hardware",
            requirePassed);

        var m9cBlocked = false;
        try
        {
            using var scope =
                Hp8C40ProductionWatchdogGate
                    .EnterM9CPhysicalQualificationConstructionScope(
                        exact,
                        Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationToken);
            _ = scope;
        }
        catch (NotSupportedException ex)
            when (ex.Message.Contains(
                "M9C",
                StringComparison.OrdinalIgnoreCase))
        {
            m9cBlocked = true;
        }

        failures += Report(
            output,
            "M9C temporary construction scope remains re-blocked after production promotion",
            m9cBlocked &&
            !Hp8C40ProductionWatchdogGate.IsM9CPhysicalQualificationScopeActive);

        var m9dBlocked = false;
        try
        {
            using var scope =
                Hp8C40ProductionWatchdogGate
                    .EnterM9DPhysicalQualificationConstructionScope(
                        exact,
                        Hp8C40ProductionWatchdogGate.M9DPhysicalQualificationToken);
            _ = scope;
        }
        catch (NotSupportedException ex)
            when (ex.Message.Contains(
                "M9D",
                StringComparison.OrdinalIgnoreCase))
        {
            m9dBlocked = true;
        }

        failures += Report(
            output,
            "M9D temporary construction scope remains re-blocked after production promotion",
            m9dBlocked &&
            !Hp8C40ProductionWatchdogGate.IsM9DPhysicalQualificationScopeActive);

        var wrongTarget =
            exact with
            {
                BoardProduct = "FFFF"
            };

        failures += Report(
            output,
            "M9 production watchdog gate remains exact-target only",
            !Hp8C40ProductionWatchdogGate
                .IsProductionConstructionAuthorizedFor(
                    wrongTarget,
                    out _));

        output.WriteLine();
        output.WriteLine(
            failures == 0
                ? "Hp8C40 M9 production-watchdog gate self-test: PASS"
                : $"Hp8C40 M9 production-watchdog gate self-test: FAIL ({failures} case(s))");

        return failures == 0 ? 0 : 19;
    }

    private static HardwareIdentity ExactTargetIdentity() =>
        new(
            BoardManufacturer: Hp8C40TargetProfile.BoardManufacturer,
            BoardProduct: Hp8C40TargetProfile.BoardProduct,
            BoardVersion: Hp8C40TargetProfile.BoardVersion,
            SystemManufacturer: Hp8C40TargetProfile.SystemManufacturer,
            SystemProductName: Hp8C40TargetProfile.SystemProductName,
            SystemSku: Hp8C40TargetProfile.SystemSkuPrefix + "#AKH",
            BiosVersion: Hp8C40TargetProfile.ValidatedBiosVersion);

    private static int Report(
        TextWriter output,
        string name,
        bool pass)
    {
        output.WriteLine(
            $"{(pass ? "PASS" : "FAIL")}  {name}");
        return pass ? 0 : 1;
    }
}
