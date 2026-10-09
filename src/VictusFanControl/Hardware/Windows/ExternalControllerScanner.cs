using System.Diagnostics;

namespace VictusFanControl.Hardware.Windows;

public sealed record PotentialControllerProcess(
    int ProcessId,
    string ProcessName,
    string? ProductName,
    string? FileDescription);

/// <summary>
/// Discovery helper only. Results are intentionally not used as a hard block
/// until the exact HP OMEN Gaming Hub process set is validated on the target.
/// </summary>
public static class ExternalControllerScanner
{
    public static IReadOnlyList<PotentialControllerProcess> ScanPotentialOmenProcesses()
    {
        var results = new List<PotentialControllerProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    var processName = process.ProcessName;
                    string? productName = null;
                    string? fileDescription = null;

                    try
                    {
                        var version = process.MainModule?.FileVersionInfo;
                        productName = version?.ProductName;
                        fileDescription = version?.FileDescription;
                    }
                    catch
                    {
                        // Some protected/system processes do not expose module metadata.
                    }

                    if (LooksLikeOmen(processName) ||
                        LooksLikeOmen(productName) ||
                        LooksLikeOmen(fileDescription))
                    {
                        results.Add(new PotentialControllerProcess(
                            process.Id,
                            processName,
                            productName,
                            fileDescription));
                    }
                }
                catch
                {
                    // Process may exit while being inspected.
                }
            }
        }

        return results
            .OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ProcessId)
            .ToArray();
    }

    private static bool LooksLikeOmen(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("omen", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("gaming hub", StringComparison.OrdinalIgnoreCase));
}
