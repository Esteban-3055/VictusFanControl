using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public readonly record struct HpFanBackendSelection(
    IFanControlBackend Backend,
    HardwareTargetProfile? TargetProfile,
    string Detail);

/// <summary>
/// Exact-target fan backend selection. Unsupported hardware always receives the
/// disabled/read-only backend. No board-family or nearest-profile fallback is
/// permitted.
/// </summary>
public static class HpFanControlBackendFactory
{
    public static HpFanBackendSelection Create(
        string modulesDirectory,
        HardwareIdentity hardware,
        IFanControlWatchdogLeaseClient? watchdogLease = null)
    {
        var target = HpHardwareTargetResolver.Resolve(hardware, out var reason);
        if (target is null)
        {
            return new HpFanBackendSelection(
                new DisabledFanControlBackend(),
                null,
                $"No write backend selected: {reason}");
        }

        if (ReferenceEquals(target, Hp88F8TargetProfile.Instance) ||
            string.Equals(
                target.Id,
                Hp88F8TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            var backend = new Hp88F8FanControlBackend(
                modulesDirectory,
                watchdogLease);

            return new HpFanBackendSelection(
                backend,
                target,
                watchdogLease is null
                    ? "Validated HP 88F8 write/restore backend selected."
                    : "Validated HP 88F8 backend selected with watchdog lease.");
        }

        if (ReferenceEquals(target, Hp8C40TargetProfile.Instance) ||
            string.Equals(
                target.Id,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            if (watchdogLease is not null)
            {
                Hp8C40ProductionWatchdogGate
                    .RequireProductionConstructionAuthorized(
                        hardware);
            }

            var backend =
                new Hp8C40FanControlBackend(
                    modulesDirectory,
                    watchdogLease);

            return new HpFanBackendSelection(
                backend,
                target,
                watchdogLease is null
                    ? "HP 8C40 bounded write/restore backend selected. " +
                      $"Production envelope is equal-only {Hp8C40TargetProfile.MinimumValidatedFanLevel}.." +
                      $"{Hp8C40TargetProfile.MaximumValidatedFanLevel}; automatic policy and watchdog remain OFF."
                    : "HP 8C40 production watchdog-backed backend selected through the explicit M9 promotion gate; automatic policy remains OFF.");
        }

        return new HpFanBackendSelection(
            new DisabledFanControlBackend(),
            target,
            $"Validated target '{target.Id}' has no fan backend implementation.");
    }
}
