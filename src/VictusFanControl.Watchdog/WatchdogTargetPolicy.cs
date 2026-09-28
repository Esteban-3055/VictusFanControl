using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Target-specific durable-watchdog setpoint policy. The lease state machine is
/// hardware-neutral; every concrete target supplies the exact custom envelope
/// that its journal is allowed to represent.
/// </summary>
internal sealed record WatchdogTargetPolicy(
    string TargetProfileId,
    byte MinimumValidatedFanLevel,
    byte MaximumValidatedFanLevel,
    bool SupportsIndependentFanLevels,
    bool LegacySchemaV1Compatible)
{
    public bool IsValidatedCustom(FanSetpoint setpoint)
    {
        if (setpoint.Cpu < MinimumValidatedFanLevel ||
            setpoint.Cpu > MaximumValidatedFanLevel ||
            setpoint.Gpu < MinimumValidatedFanLevel ||
            setpoint.Gpu > MaximumValidatedFanLevel)
        {
            return false;
        }

        return SupportsIndependentFanLevels ||
               setpoint.Cpu == setpoint.Gpu;
    }

    public string DescribeCustomEnvelope() =>
        SupportsIndependentFanLevels
            ? $"{MinimumValidatedFanLevel}-{MaximumValidatedFanLevel} per fan"
            : $"equal-only {MinimumValidatedFanLevel}-{MaximumValidatedFanLevel}";
}

internal static class WatchdogTargetPolicies
{
    public static WatchdogTargetPolicy Hp88F8 { get; } =
        Create(
            Hp88F8TargetProfile.Instance,
            legacySchemaV1Compatible: true);

    public static WatchdogTargetPolicy Hp8C40 { get; } =
        Create(
            Hp8C40TargetProfile.Instance,
            legacySchemaV1Compatible: false);

    public static WatchdogTargetPolicy ForProfile(
        HardwareTargetProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.Equals(
                profile.Id,
                Hp88F8TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            return Hp88F8;
        }

        if (string.Equals(
                profile.Id,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            return Hp8C40;
        }

        throw new NotSupportedException(
            $"No watchdog target policy is defined for hardware profile '{profile.Id}'.");
    }

    private static WatchdogTargetPolicy Create(
        HardwareTargetProfile profile,
        bool legacySchemaV1Compatible) =>
        new(
            profile.Id,
            checked((byte)profile.MinimumValidatedFanLevel),
            checked((byte)profile.MaximumValidatedFanLevel),
            profile.SupportsIndependentFanLevels,
            legacySchemaV1Compatible);
}
