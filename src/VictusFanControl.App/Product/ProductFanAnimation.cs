namespace VictusFanControl.App;

// Presentation only: accepted request drives rotation; measured RPM is only the hub readout.
internal sealed class ProductFanAnimation
{
    internal const double ReferenceRequest = 30;
    internal const double ReferenceSecondsPerRevolution = 0.5;
    internal const double SimulatedRpmPerLevel = 100;
    // 3000 simulated RPM / 25 = 120 visual RPM: one revolution every 0.5 s.
    private const double VisualRpmDivisor = ReferenceRequest * SimulatedRpmPerLevel * ReferenceSecondsPerRevolution / 60;
    private const double RestSpeed = 0.1;
    internal double Phase { get; private set; }
    internal double Speed { get; private set; }
    internal bool IsMoving => Speed > 0;

    internal void Pause() => Speed = 0;

    internal static double SimulatedRpm(double requestLevel) => requestLevel * SimulatedRpmPerLevel;
    internal static double TargetDegreesPerSecond(double requestLevel) => SimulatedRpm(requestLevel) / VisualRpmDivisor * 6;

    internal void Advance(double elapsed, double? requestLevel)
    {
        if (requestLevel is null || !double.IsFinite(requestLevel.Value) || requestLevel is <0 or >50) { Pause(); return; }
        if (!double.IsFinite(elapsed) || elapsed <= 0) return;
        elapsed = Math.Min(elapsed, 0.1); // Never catch up a delayed or hidden frame.
        var target = TargetDegreesPerSecond(requestLevel.Value);
        var inertia = target > Speed ? 1.2 : 1.8;
        var decay = Math.Exp(-elapsed / inertia);
        // Integrate the easing analytically so irregular frame times do not change the motion.
        Phase = (Phase + target * elapsed + (Speed - target) * inertia * (1 - decay)) % 360;
        Speed = target + (Speed - target) * decay;
        if (target == 0 && Speed < RestSpeed) Pause();
    }
}
