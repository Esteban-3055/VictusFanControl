using System.Text.Json;
using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.App;

internal static class Hp8C40P15D2VariableManualQualificationGate
{
    // Re-blocked after the preserved P15D2 EC-transient fail-closed attempt.
    // A retry requires a separate fresh authorization and same-HEAD CI.
    public static readonly bool PhysicalExecutionAuthorized = true;

    public const string RequiredToken = "8C40-P15D2-MANUAL30-40-40-30";
    public const int InitialLevel = 30;
    public const int ChangedLevel = 40;
    public const int ReturnLevel = 30;
    // One watchdog session is retained across the variable-level sequence.
    // Generation advances on each durable WriteIntent + Commit pair:
    // Prepare=1, initial 30/30 Commit=3, changed 40/40 Commit=5,
    // duplicate 40/40 HoldCustom stays 5, return 30/30 Commit=7.
    public const int InitialOwnedGeneration = 3;
    public const int ChangedOwnedGeneration = 5;
    public const int DuplicateHoldOwnedGeneration = 5;
    public const int ReturnOwnedGeneration = 7;
    public const int RequiredHealthyPreWriteSamples = 3;

    public const double MaximumCpuPhysicalC = 90.0;
    public const double MaximumGpuPhysicalC = 82.0;
    public const double MaximumCpuPackagePowerW = 60.0;
    public const double MaximumGpuPowerW = 75.0;

    public const string ReadyFileName = "p15d2-gui-ready.json";
    public const string Step1Apply30FileName = "p15d2-step1-apply30.json";
    public const string Parent30VerifiedFileName = "p15d2-parent-30-verified.txt";
    public const string Step2Apply40FileName = "p15d2-step2-apply40.json";
    public const string Parent40VerifiedFileName = "p15d2-parent-40-verified.txt";
    public const string Step3Hold40FileName = "p15d2-step3-hold40.json";
    public const string ParentHold40VerifiedFileName = "p15d2-parent-hold40-verified.txt";
    public const string Step4Return30FileName = "p15d2-step4-return30.json";
    public const string ParentReturn30VerifiedFileName = "p15d2-parent-return30-verified.txt";
    public const string FirmwareRestoredFileName = "p15d2-firmware-restored.json";
    public const string EventsFileName = "p15d2-gui-events.jsonl";

    public static bool NormalUserExecutionGatesClosed() =>
        !Hp8C40PostM9UserControlGate.ManualExecutionAuthorized &&
        !Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
