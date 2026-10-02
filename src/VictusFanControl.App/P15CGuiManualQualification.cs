using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal enum P13ControlInteractionKind
{
    ModeRequest = 0,
    ManualApply = 1
}

internal sealed record P13ControlInteractionObservation(
    P13ControlInteractionKind Kind,
    AdaptiveFanProductionMode? RequestedMode,
    int? EqualFanLevel,
    AdaptiveFanProductionResult? Result,
    string? Failure,
    DateTimeOffset TimestampUtc);

/// <summary>
/// Dedicated P15C qualification barrier. This never changes the normal
/// post-M9 user-facing Manual/Automatic gate. A later, separate authorization
/// commit may set this true only for the explicit P15C GUI process mode.
/// </summary>
internal static class Hp8C40P15CGuiManualQualificationGate
{
    public static readonly bool PhysicalExecutionAuthorized = false;

    public const string RequiredToken = "8C40-P15C-GUI-MANUAL30";
    public const int QualificationLevel = 30;
    public const int RequiredHealthyPreWriteSamples = 3;
    public const double MaximumCpuPhysicalC = 90.0;
    public const double MaximumGpuPhysicalC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    public const string ReadyFileName = "p15c-gui-ready.json";
    public const string ManualAppliedFileName = "p15c-gui-manual-applied.json";
    public const string ParentOwnedVerifiedFileName = "p15c-parent-owned-verified.txt";
    public const string ResultFileName = "p15c-gui-result.json";
    public const string EventsFileName = "p15c-gui-events.jsonl";

    public static bool NormalUserExecutionGatesClosed() =>
        !Hp8C40PostM9UserControlGate.ManualExecutionAuthorized &&
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;

    public static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
}
