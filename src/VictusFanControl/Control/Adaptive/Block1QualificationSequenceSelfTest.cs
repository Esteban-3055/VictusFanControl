namespace VictusFanControl.Control.Adaptive;

public static class Block1QualificationSequenceSelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = 0;
        void Check(string name, bool ok) { output.WriteLine($"{(ok ? "PASS" : "FAIL")}: Block1 {name}"); if (!ok) failures++; }
        Block1QualificationSequence Begin()
        {
            var s = new Block1QualificationSequence();
            s.Ready();
            s.Mode(AdaptiveFanProductionMode.Manual, AdaptiveFanProductionActionKind.HoldFirmware, FanAuthority.Firmware);
            s.Manual(40, AdaptiveFanProductionActionKind.EnterCustomAndApply, FanAuthority.Custom);
            s.Mode(AdaptiveFanProductionMode.Automatic, AdaptiveFanProductionActionKind.HoldCustom, FanAuthority.Custom);
            return s;
        }
        var good = Begin();
        good.Automatic(AdaptiveFanProductionActionKind.HoldCustom, FanAuthority.Custom, 40);
        Check("hold cannot authorize return before a changed command", !good.Allows(false, AdaptiveFanProductionMode.Manual, null));
        good.Automatic(AdaptiveFanProductionActionKind.ApplyChangedLevel, FanAuthority.Custom, 31);
        good.Mode(AdaptiveFanProductionMode.Manual, AdaptiveFanProductionActionKind.HoldCustom, FanAuthority.Custom, 31);
        Check("return Manual target is different from last WMI target", good.ReturnManualLevel == 32);
        good.Manual(32, AdaptiveFanProductionActionKind.ApplyChangedLevel, FanAuthority.Custom);
        good.Mode(AdaptiveFanProductionMode.Firmware, AdaptiveFanProductionActionKind.RestoreFirmware, FanAuthority.Firmware);
        good.Mode(AdaptiveFanProductionMode.Manual, AdaptiveFanProductionActionKind.HoldFirmware, FanAuthority.Firmware);
        good.Manual(31, AdaptiveFanProductionActionKind.EnterCustomAndApply, FanAuthority.Custom);
        good.BeginTrayExit(); good.Complete(true, true, true);
        Check("complete combined sequence", good.Phase == Block1Phase.Completed && good.HandoffPassed && good.RearmPassed && good.ClosePassed);
        foreach (var action in new Action<Block1QualificationSequence>[]
        {
            s => s.Mode(AdaptiveFanProductionMode.Firmware, AdaptiveFanProductionActionKind.RestoreFirmware, FanAuthority.Firmware),
            s => s.Automatic(AdaptiveFanProductionActionKind.RestoreFirmware, FanAuthority.Firmware, null),
            s => s.Automatic(AdaptiveFanProductionActionKind.ApplyChangedLevel, FanAuthority.Custom, 29),
            s => s.BeginTrayExit()
        })
        {
            var bad = Begin();
            try { action(bad); } catch (InvalidOperationException) { }
            Check("bad order/authority/range/exit closes evidence but preserves Firmware escape", bad.Failed && bad.Allows(false, AdaptiveFanProductionMode.Firmware, null));
        }
        var wrong = new Block1QualificationSequence(); wrong.Ready();
        Check("wrong initial Manual level fenced", !wrong.Allows(true, null, 31));
        return failures;
    }
}
