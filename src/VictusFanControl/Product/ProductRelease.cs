using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Product;

/// <summary>Target-specific user release. Historical qualification gates remain unchanged.</summary>
public static class ProductRelease
{
    public const string Version = "1.0.0";
    public static bool IsAutomaticAuthorized(string? target) =>
        string.Equals(target, Hp8C40TargetProfile.Instance.Id, StringComparison.Ordinal);
}
