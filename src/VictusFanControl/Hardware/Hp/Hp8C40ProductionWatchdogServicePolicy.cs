using VictusFanControl.Control;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Candidate SCM lifecycle policy for the already-qualified HP 8C40 watchdog.
///
/// This class does not manipulate SCM and does not authorize production
/// watchdog construction. M9E qualifies only service lifecycle/configuration;
/// the final M9 promotion remains a separate explicit commit.
/// </summary>
public static class Hp8C40ProductionWatchdogServicePolicy
{
    public const string ServiceName = "VictusFanControlWatchdogM4";
    public const string PipeName = FanControlWatchdogLeaseContract.Hp8C40M4PipeName;
    public const string ServiceModeArgument = "--m4-8c40-lease-service";
    public const string QualificationToken = "8C40-M9E-SERVICE-LIFECYCLE";

    public const int FailureResetSeconds = 86400;
    public const int FirstRestartDelayMs = 5000;
    public const int SecondRestartDelayMs = 5000;
    public const int ThirdRestartDelayMs = 10000;

    public const bool AutomaticStart = true;
    public const bool DelayedAutomaticStart = false;
    public const bool FailureActionsOnNonCrashFailures = true;

    // M9E preparation is code-only. This remains false until M9B/M9C/M9D
    // physical evidence has been formally closed and a dedicated M9E
    // authorization commit is reviewed.
    public static readonly bool ServiceConfigurationAuthorized = false;
}
