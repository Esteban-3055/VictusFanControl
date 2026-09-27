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
                throw new NotSupportedException(
                    "HP 8C40 matched, but watchdog/service recovery has not yet " +
                    "been physically validated for Modern Standby. The old 88F8 " +
                    "S3 watchdog gates must not be reused on this target.");
            }

            var backend = new Hp8C40FanControlBackend(modulesDirectory);

            return new HpFanBackendSelection(
                backend,
                target,
                "HP 8C40 bounded write/restore backend selected. " +
                "Only equal 30/30 is qualified; automatic policy and watchdog remain OFF.");
        }

        return new HpFanBackendSelection(
            new DisabledFanControlBackend(),
            target,
            $"Validated target '{target.Id}' has no fan backend implementation.");
    }
}
