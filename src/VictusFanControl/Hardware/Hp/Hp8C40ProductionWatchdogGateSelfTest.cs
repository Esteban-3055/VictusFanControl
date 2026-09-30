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
            "M9 production watchdog gate is closed by default",
            !Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized &&
            !Hp8C40ProductionWatchdogGate.M9CPhysicalQualificationConstructionAuthorized &&
            !Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated);

        var authorized =
            Hp8C40ProductionWatchdogGate
                .IsProductionConstructionAuthorizedFor(
                    exact,
                    out var reason);

        failures += Report(
            output,
            "exact HP 8C40 still cannot create production watchdog before M9 promotion",
            !authorized &&
            reason.Contains(
                "WatchdogRecoveryValidated=false",
                StringComparison.Ordinal));

        var lease =
            Hp8C40ProductionWatchdogGate
                .CreateLeaseIfAuthorized(exact);

        failures += Report(
            output,
            "normal M9 wiring returns no lease while promotion is closed",
            lease is null);

        var fakeLease = new RecordingLease();
        var factoryBlocked = false;

        try
        {
            _ = HpFanControlBackendFactory.Create(
                modulesDirectory: "M9-NO-HARDWARE-SENTINEL",
                hardware: exact,
                watchdogLease: fakeLease);
        }
        catch (NotSupportedException ex)
            when (ex.Message.Contains(
                "M9",
                StringComparison.OrdinalIgnoreCase) ||
                  ex.Message.Contains(
                      "WatchdogRecoveryValidated=false",
                      StringComparison.Ordinal))
        {
            factoryBlocked = true;
        }

        failures += Report(
            output,
            "factory rejects supplied 8C40 production lease before backend/hardware construction",
            factoryBlocked &&
            fakeLease.Calls == 0);

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
            "M9C temporary construction scope is compile-time blocked before physical authorization",
            m9cBlocked &&
            !Hp8C40ProductionWatchdogGate.IsM9CPhysicalQualificationScopeActive);

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

    private sealed class RecordingLease :
        IFanControlWatchdogLeaseClient
    {
        public int Calls { get; private set; }

        private ValueTask Record()
        {
            Calls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask PrepareAsync(CancellationToken cancellationToken) => Record();
        public ValueTask CancelPreparedAsync(CancellationToken cancellationToken) => Record();
        public ValueTask WriteIntentAsync(int cpuLevel, int gpuLevel, CancellationToken cancellationToken) => Record();
        public ValueTask AbortWriteIntentAsync(CancellationToken cancellationToken) => Record();
        public ValueTask CommitAsync(int cpuLevel, int gpuLevel, CancellationToken cancellationToken) => Record();
        public ValueTask ProbeAsync(CancellationToken cancellationToken) => Record();
        public ValueTask HeartbeatAsync(CancellationToken cancellationToken) => Record();
        public ValueTask RestoreBeginAsync(CancellationToken cancellationToken) => Record();
        public ValueTask ReleaseAsync(CancellationToken cancellationToken) => Record();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
