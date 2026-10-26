using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Product;

/// <summary>Target-specific user release. Historical qualification gates remain unchanged.</summary>
public static class ProductRelease
{
    public const string Version = "1.3.5";
    public static System.Version NumericVersion => System.Version.Parse(Version.Split('-')[0].Split('+')[0]);
    // Distribution channel is independent of the numeric tag required by the 1.2.9 updater.
    public static bool IsPreview => true;
    public static string DisplayVersion => Version + (IsPreview ? " · Preliminar" : "");
    public static bool IsAutomaticAuthorized(string? target) =>
        string.Equals(target, Hp8C40TargetProfile.Instance.Id, StringComparison.Ordinal);
}
