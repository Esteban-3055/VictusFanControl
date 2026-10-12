namespace VictusFanControl.App;

// Presentation only: accepted controller demand drives rotation; RPM is only the hub readout.
internal sealed class ProductFanAnimation
{
    private const double MaximumDegreesPerSecond = 24;
    private const double RestSpeed = 0.1;
    internal double Phase { get; private set; }
    internal double Speed { get; private set; }
    internal bool IsMoving => Speed > 0;

    internal void Pause() => Speed = 0;

    internal void Advance(double elapsed, double? demandPercent)
    {
        if (demandPercent is null || !double.IsFinite(demandPercent.Value)) { Pause(); return; }
        if (!double.IsFinite(elapsed) || elapsed <= 0) return;
        elapsed = Math.Min(elapsed, 0.1); // Never catch up a delayed or hidden frame.
        var target = Math.Clamp(demandPercent.Value, 0, 100) / 100 * MaximumDegreesPerSecond;
        var inertia = target > Speed ? 1.2 : 1.8;
        var decay = Math.Exp(-elapsed / inertia);
        // Integrate the easing analytically so irregular frame times do not change the motion.
        Phase = (Phase + target * elapsed + (Speed - target) * inertia * (1 - decay)) % 360;
        Speed = target + (Speed - target) * decay;
        if (target == 0 && Speed < RestSpeed) Pause();
    }
}
