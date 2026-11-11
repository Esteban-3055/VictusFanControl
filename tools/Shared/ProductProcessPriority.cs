using System.Diagnostics;

namespace VictusFanControl.Scheduling;

internal static class ProductProcessPriority
{
    internal static bool IsProductionGuardian(string[] args) => args.Length > 0 &&
        args[0] is "--gui-session" or "--fan-wmi-session" or "--recover-gui-session" or "--recover-product-fans";

    // Each production process configures itself; High is not inherited reliably.
    // Failure is diagnostic, never a reason to bypass admission or stop cleanup.
    internal static bool Apply(Action<string> log, Action? set = null, Func<ProcessPriorityClass>? read = null)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            (set ?? (() => process.PriorityClass = ProcessPriorityClass.High))();
            process.Refresh();
            var effective = (read ?? (() => process.PriorityClass))();
            log($"PRODUCT PROCESS PRIORITY requested=High effective={effective}");
            return effective == ProcessPriorityClass.High;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            log("PRODUCT PROCESS PRIORITY requested=High not confirmed: " + ex.Message);
            return false;
        }
    }
}
