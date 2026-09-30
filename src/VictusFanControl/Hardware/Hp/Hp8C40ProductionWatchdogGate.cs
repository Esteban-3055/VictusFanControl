using VictusFanControl.Control;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M9 production-watchdog promotion boundary for the exact HP 8C40 target.
///
/// M4-M8 qualify the underlying lease/recovery/lifecycle/load behavior, but
/// those gates do not implicitly expose watchdog-backed construction through
/// the normal GUI/factory path. M9 must be closed explicitly. Until then this
/// class returns no production lease and both factory/backend defenses reject
/// an externally supplied lease fail-closed.
/// </summary>
public static class Hp8C40ProductionWatchdogGate
{
    public const string GateId = "M9";

    // M9A prepares wiring only. Do not set true until the separately versioned
    // M9 physical production-path gates have passed and the profile is promoted.
    public static readonly bool ProductionConstructionAuthorized = false;

    public static bool IsProductionConstructionAuthorizedFor(
        HardwareIdentity hardware,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var targetReason))
        {
            reason =
                $"M9 exact-target refusal: {targetReason}";
            return false;
        }

        if (!Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated)
        {
            reason =
                "M9 production watchdog construction is blocked because " +
                "Hp8C40TargetProfile.WatchdogRecoveryValidated=false.";
            return false;
        }

        if (!ProductionConstructionAuthorized)
        {
            reason =
                "M9 production watchdog construction is compile-time blocked. " +
                "A dedicated post-M8 promotion commit is required.";
            return false;
        }

        reason =
            "M9 production watchdog construction is authorized for the exact HP 8C40 target.";
        return true;
    }

    public static void RequireProductionConstructionAuthorized(
        HardwareIdentity hardware)
    {
        if (!IsProductionConstructionAuthorizedFor(
                hardware,
                out var reason))
        {
            throw new NotSupportedException(reason);
        }
    }

    /// <summary>
    /// Normal GUI wiring point. While M9 is open this returns null and performs
    /// no IPC/service/hardware work. After a future explicit M9 promotion it
    /// constructs only the already-qualified target-bound named-pipe client;
    /// the client still cannot acquire a lease until coordinator admission.
    /// </summary>
    public static IFanControlWatchdogLeaseClient? CreateLeaseIfAuthorized(
        HardwareIdentity hardware)
    {
        if (!IsProductionConstructionAuthorizedFor(
                hardware,
                out _))
        {
            return null;
        }

        return new NamedPipeFanControlWatchdogLeaseClient(
            Hp8C40TargetProfile.Instance.Id,
            FanControlWatchdogLeaseContract.Hp8C40M4PipeName);
    }
}
