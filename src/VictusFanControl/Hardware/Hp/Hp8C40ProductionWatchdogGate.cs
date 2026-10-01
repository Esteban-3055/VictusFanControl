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
    public const string M9CPhysicalQualificationToken = "8C40-M9C-PRODUCTION30";
    public const string M9DPhysicalQualificationToken = "8C40-M9D-PRODUCTION-LIFECYCLE30";

    private static readonly AsyncLocal<int> M9CQualificationScopeDepth = new();
    private static readonly AsyncLocal<int> M9DQualificationScopeDepth = new();

    // M9A prepares production wiring only. Do not set true until the separately
    // versioned M9 physical gates have passed and the profile is promoted.
    public static readonly bool ProductionConstructionAuthorized = false;

    // Separate temporary construction gate for one versioned M9C physical
    // qualification. It remains false until M9B evidence is physically closed.
    public static readonly bool M9CPhysicalQualificationConstructionAuthorized = false;

    // Separate construction-only gate for the M9D GUI lifecycle regression.
    public static readonly bool M9DPhysicalQualificationConstructionAuthorized = false;

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
        ArgumentNullException.ThrowIfNull(hardware);

        // M9C may temporarily traverse the exact same factory/public constructor
        // only inside its separately authorized AsyncLocal construction scope.
        // The scope is disposed before Custom admission or any fan write.
        if (M9CQualificationScopeDepth.Value == 1 ||
            M9DQualificationScopeDepth.Value == 1)
        {
            if (M9CQualificationScopeDepth.Value == 1 &&
                M9DQualificationScopeDepth.Value == 1)
            {
                throw new InvalidOperationException(
                    "M9 qualification construction scopes may not overlap.");
            }

            if (!Hp8C40TargetProfile.Matches(
                    hardware,
                    out var qualificationTargetReason))
            {
                var gateName =
                    M9DQualificationScopeDepth.Value == 1
                        ? "M9D"
                        : "M9C";

                throw new NotSupportedException(
                    gateName + " exact-target refusal: " + qualificationTargetReason);
            }

            return;
        }

        if (!IsProductionConstructionAuthorizedFor(
                hardware,
                out var reason))
        {
            throw new NotSupportedException(reason);
        }
    }

    public static IDisposable EnterM9CPhysicalQualificationConstructionScope(
        HardwareIdentity hardware,
        string qualificationToken)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var targetReason))
        {
            throw new NotSupportedException(
                $"M9C exact-target refusal: {targetReason}");
        }

        if (!string.Equals(
                qualificationToken,
                M9CPhysicalQualificationToken,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                $"M9C construction requires exact token '{M9CPhysicalQualificationToken}'.");
        }

        if (!M9CPhysicalQualificationConstructionAuthorized)
        {
            throw new NotSupportedException(
                "M9C physical qualification construction is compile-time blocked.");
        }

        if (ProductionConstructionAuthorized ||
            Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated)
        {
            throw new InvalidOperationException(
                "M9C qualification scope is forbidden while/after production watchdog promotion is active.");
        }

        if (M9CQualificationScopeDepth.Value != 0)
        {
            throw new InvalidOperationException(
                "Nested M9C production-watchdog construction scopes are forbidden.");
        }

        M9CQualificationScopeDepth.Value = 1;
        return new M9CConstructionScope();
    }

    internal static bool IsM9CPhysicalQualificationScopeActive =>
        M9CQualificationScopeDepth.Value == 1;

    private sealed class M9CConstructionScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (M9CQualificationScopeDepth.Value != 1)
            {
                M9CQualificationScopeDepth.Value = 0;
                throw new InvalidOperationException(
                    "M9C construction scope depth was corrupted.");
            }

            M9CQualificationScopeDepth.Value = 0;
        }
    }

    public static IDisposable EnterM9DPhysicalQualificationConstructionScope(
        HardwareIdentity hardware,
        string qualificationToken)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var targetReason))
        {
            throw new NotSupportedException(
                "M9D exact-target refusal: " + targetReason);
        }

        if (!string.Equals(
                qualificationToken,
                M9DPhysicalQualificationToken,
                StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "M9D construction requires exact token '" +
                M9DPhysicalQualificationToken + "'.");
        }

        if (!M9DPhysicalQualificationConstructionAuthorized)
        {
            throw new NotSupportedException(
                "M9D physical qualification construction is compile-time blocked.");
        }

        if (ProductionConstructionAuthorized ||
            Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated)
        {
            throw new InvalidOperationException(
                "M9D qualification scope is forbidden while/after production watchdog promotion is active.");
        }

        if (M9CQualificationScopeDepth.Value != 0 ||
            M9DQualificationScopeDepth.Value != 0)
        {
            throw new InvalidOperationException(
                "Nested/overlapping M9 production-watchdog construction scopes are forbidden.");
        }

        M9DQualificationScopeDepth.Value = 1;
        return new M9DConstructionScope();
    }

    public static bool IsM9DPhysicalQualificationScopeActive =>
        M9DQualificationScopeDepth.Value == 1;

    private sealed class M9DConstructionScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (M9DQualificationScopeDepth.Value != 1)
            {
                M9DQualificationScopeDepth.Value = 0;
                throw new InvalidOperationException(
                    "M9D construction scope depth was corrupted.");
            }

            M9DQualificationScopeDepth.Value = 0;
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
