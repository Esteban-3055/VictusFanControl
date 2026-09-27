using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Resolves only exact, explicitly qualified hardware targets. There is no
/// model-family fallback because HP reuses platform components across systems
/// with different BIOS/EC behavior.
/// </summary>
public static class HpHardwareTargetResolver
{
    private static readonly HardwareTargetProfile[] KnownTargets =
    [
        Hp88F8TargetProfile.Instance,
        Hp8C40TargetProfile.Instance
    ];

    public static IReadOnlyList<HardwareTargetProfile> SupportedTargets =>
        KnownTargets;

    public static HardwareTargetProfile? Resolve(
        HardwareIdentity hardware,
        out string reason)
    {
        var failures = new List<string>(KnownTargets.Length);

        foreach (var target in KnownTargets)
        {
            if (target.Matches(hardware, out var targetReason))
            {
                reason = targetReason;
                return target;
            }

            failures.Add($"{target.Id}: {targetReason}");
        }

        reason =
            "No validated hardware target matched. " +
            string.Join(" | ", failures);
        return null;
    }

    public static HardwareTargetProfile? ResolveCurrent(out string reason) =>
        Resolve(HardwareIdentityReader.ReadCurrent(), out reason);
}
