using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Product;

/// <summary>Target-specific user release. Historical qualification gates remain unchanged.</summary>
public static class ProductRelease
{
    public const string Version = "1.2.8-preview.1";
    public static System.Version NumericVersion => System.Version.Parse(Version.Split('-')[0].Split('+')[0]);
    public static bool IsPreview => Version.Contains('-');
    public static bool IsAutomaticAuthorized(string? target) =>
        string.Equals(target, Hp8C40TargetProfile.Instance.Id, StringComparison.Ordinal);
}
