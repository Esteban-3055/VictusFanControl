using System.Text.Json;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal static class Hp8C40P15D1TrayExitQualificationGate
{
    public static readonly bool PhysicalExecutionAuthorized = false;
    public const string RequiredToken = "8C40-P15D1-TRAYEXIT30";
    public const int QualificationLevel = 30;
    public const int RequiredHealthyPreWriteSamples = 3;
    public const double MaximumCpuPhysicalC = 90.0;
    public const double MaximumGpuPhysicalC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;
    public const string ReadyFileName = "p15d1-gui-ready.json";
    public const string ManualAppliedFileName = "p15d1-gui-manual-applied.json";
    public const string ParentOwnedVerifiedFileName = "p15d1-parent-owned-verified.txt";
    public const string WindowHiddenFileName = "p15d1-window-hidden.json";
    public const string ParentHiddenOwnedVerifiedFileName = "p15d1-parent-hidden-owned-verified.txt";
    public const string TrayExitRequestedFileName = "p15d1-tray-exit-requested.json";
    public const string ShutdownResultFileName = "p15d1-shutdown-result.json";
    public const string EventsFileName = "p15d1-gui-events.jsonl";

    public static bool NormalUserExecutionGatesClosed() =>
        !Hp8C40PostM9UserControlGate.ManualExecutionAuthorized &&
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
