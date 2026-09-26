using Microsoft.Win32;

namespace VictusFanControl.Hardware.Windows;

public sealed record HardwareIdentity(
    string BoardManufacturer,
    string BoardProduct,
    string BoardVersion,
    string SystemManufacturer,
    string SystemProductName,
    string SystemSku,
    string BiosVersion)
{
    public string BoardDisplay =>
        $"{BoardManufacturer} {BoardProduct} ({BoardVersion})".Trim();
}

public static class HardwareIdentityReader
{
    private const string BiosKeyPath = @"HARDWARE\DESCRIPTION\System\BIOS";

    public static HardwareIdentity ReadCurrent()
    {
        using var key = Registry.LocalMachine.OpenSubKey(BiosKeyPath, writable: false);

        return new HardwareIdentity(
            BoardManufacturer: Read(key, "BaseBoardManufacturer"),
            BoardProduct: Read(key, "BaseBoardProduct"),
            BoardVersion: Read(key, "BaseBoardVersion"),
            SystemManufacturer: Read(key, "SystemManufacturer"),
            SystemProductName: Read(key, "SystemProductName"),
            SystemSku: Read(key, "SystemSKU"),
            BiosVersion: Read(key, "BIOSVersion"));
    }

    private static string Read(RegistryKey? key, string name)
    {
        var raw = key?.GetValue(name);

        var value = raw switch
        {
            string text => text.Trim(),
            string[] items => string.Join(
                " | ",
                items.Where(item => !string.IsNullOrWhiteSpace(item))
                     .Select(item => item.Trim())),
            _ => raw?.ToString()?.Trim()
        };

        return string.IsNullOrWhiteSpace(value) ? "unknown" : value;
    }
}
