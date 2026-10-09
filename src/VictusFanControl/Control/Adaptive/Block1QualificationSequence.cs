namespace VictusFanControl.Control.Adaptive;

public enum Block1Phase
{
    Preparing, Ready, ManualSelectedA, ManualActiveA, AutomaticRunning,
    AutomaticChanged, ManualReturnedA, ManualChangedA, FirmwareReleasedA,
    ManualSelectedB, ManualActiveB, TrayExit, Completed, Failed
}

/// <summary>Evidence-only sequence; grants no hardware authority and never writes hardware.</summary>
public sealed class Block1QualificationSequence
{
    public const string RequiredToken = "8C40-WMI-BLOCK1";
    public Block1Phase Phase { get; private set; } = Block1Phase.Preparing;
    public string? Failure { get; private set; }
    public int? LastAutomaticLevel { get; private set; }
    public int ReturnManualLevel { get; private set; }
    public int AutomaticDecisions { get; private set; }
    public int AutomaticCommands { get; private set; }
    public bool HandoffPassed { get; private set; }
    public bool RearmPassed { get; private set; }
    public bool ClosePassed { get; private set; }
    public bool Failed => Phase == Block1Phase.Failed;

    public void Fail(string reason)
    {
        Failure ??= reason;
        Phase = Block1Phase.Failed;
    }

    public void Ready()
    {
        Require(Phase == Block1Phase.Preparing, "Duplicate READY.");
        Phase = Block1Phase.Ready;
    }

    public bool Allows(bool manualApply, AdaptiveFanProductionMode? mode, int? level)
    {
        if (!manualApply && mode == AdaptiveFanProductionMode.Firmware) return true; // Unconditional escape.
        if (Failed) return false;
        if (manualApply) return Phase switch
        {
            Block1Phase.ManualSelectedA => level == 40,
            Block1Phase.ManualReturnedA => level == ReturnManualLevel,
            Block1Phase.ManualSelectedB => level == 31,
            _ => false
        };
        return Phase switch
        {
            Block1Phase.Ready or Block1Phase.FirmwareReleasedA => mode == AdaptiveFanProductionMode.Manual,
            Block1Phase.ManualActiveA => mode == AdaptiveFanProductionMode.Automatic,
            Block1Phase.AutomaticChanged => mode == AdaptiveFanProductionMode.Manual,
            _ => false
        };
    }

    public void Mode(AdaptiveFanProductionMode mode, AdaptiveFanProductionActionKind action, FanAuthority authority,
        int? lastAcceptedLevel = null)
    {
        Require(!Failed, "Sequence was already interrupted.");
        if (mode == AdaptiveFanProductionMode.Firmware)
        {
            Require(Phase == Block1Phase.ManualChangedA && action == AdaptiveFanProductionActionKind.RestoreFirmware &&
                authority == FanAuthority.Firmware, "Firmware escape occurred before the complete handoff.");
            HandoffPassed = true;
            Phase = Block1Phase.FirmwareReleasedA;
            return;
        }
        Require(Allows(false, mode, null), "Unexpected mode selection.");
        if (Phase == Block1Phase.Ready || Phase == Block1Phase.FirmwareReleasedA)
        {
            Require(action == AdaptiveFanProductionActionKind.HoldFirmware && authority == FanAuthority.Firmware,
                "Manual selection must be a no-write Firmware transition.");
            Phase = Phase == Block1Phase.Ready ? Block1Phase.ManualSelectedA : Block1Phase.ManualSelectedB;
        }
        else
        {
            Require(action == AdaptiveFanProductionActionKind.HoldCustom && authority == FanAuthority.Custom,
                "Custom handoff inserted a release or lost authority.");
            if (mode == AdaptiveFanProductionMode.Automatic) Phase = Block1Phase.AutomaticRunning;
            else
            {
                Require(lastAcceptedLevel.HasValue, "Missing last accepted WMI level for Manual handoff.");
                ReturnManualLevel = lastAcceptedLevel == 31 ? 32 : 31;
                Phase = Block1Phase.ManualReturnedA;
            }
        }
    }

    public void Manual(int level, AdaptiveFanProductionActionKind action, FanAuthority authority)
    {
        Require(Allows(true, null, level) && authority == FanAuthority.Custom, "Unexpected Manual command.");
        if (Phase == Block1Phase.ManualReturnedA)
        {
            Require(action == AdaptiveFanProductionActionKind.ApplyChangedLevel, "Manual return did not change target in Custom.");
            Phase = Block1Phase.ManualChangedA;
        }
        else
        {
            Require(action == AdaptiveFanProductionActionKind.EnterCustomAndApply, "Manual acquisition did not create Custom.");
            if (Phase == Block1Phase.ManualSelectedA) Phase = Block1Phase.ManualActiveA;
            else { RearmPassed = true; Phase = Block1Phase.ManualActiveB; }
        }
    }

    public void Automatic(AdaptiveFanProductionActionKind action, FanAuthority authority, int? level)
    {
        Require(Phase is Block1Phase.AutomaticRunning or Block1Phase.AutomaticChanged,
            "Automatic decision outside its sequence stage.");
        Require(authority == FanAuthority.Custom && level is >= 30 and <= 50 &&
            action is AdaptiveFanProductionActionKind.HoldCustom or AdaptiveFanProductionActionKind.ApplyChangedLevel,
            "Automatic decision lost Custom, safety admission or its 30..50 envelope.");
        AutomaticDecisions++;
        if (action == AdaptiveFanProductionActionKind.ApplyChangedLevel)
        {
            Require(level != 40 || AutomaticCommands > 0, "First Automatic command did not change the initial Manual target.");
            AutomaticCommands++;
            LastAutomaticLevel = level;
            Phase = Block1Phase.AutomaticChanged;
        }
    }

    public void BeginTrayExit()
    {
        Require(Phase == Block1Phase.ManualActiveB, "Tray Exit requested before the active second session.");
        Phase = Block1Phase.TrayExit;
    }

    public void Complete(bool fanReleased, bool performanceReleased, bool telemetryStopped)
    {
        Require(Phase == Block1Phase.TrayExit && fanReleased && performanceReleased && telemetryStopped,
            "Tray shutdown lacks complete fan/performance/telemetry cleanup.");
        ClosePassed = true;
        Phase = Block1Phase.Completed;
    }

    private void Require(bool condition, string reason)
    {
        if (condition) return;
        Fail(reason);
        throw new InvalidOperationException(reason);
    }
}
