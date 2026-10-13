namespace VictusFanControl.App;

// Presentation only: measured RPM drives rotation, retaining the target during acquisition gaps.
internal sealed class ProductFanAnimation
{
    internal const double ReferenceRpm = 3000;
    internal const double ReferenceSecondsPerRevolution = 1;
    // 3000 measured RPM / 50 = 60 visual RPM: one revolution every second.
    private const double VisualRpmDivisor = ReferenceRpm * ReferenceSecondsPerRevolution / 60;
    private const double RestSpeed = 0.1;
    private double _target;
    internal double Phase { get; private set; }
    internal double Speed { get; private set; }
    internal bool IsMoving => Speed > 0;

    internal static double TargetDegreesPerSecond(double rpm) => rpm / VisualRpmDivisor * 6;

    internal void Advance(double elapsed, double? rpm)
    {
        if (rpm is {} value && double.IsFinite(value) && value is >=0 and <=10000)
            _target = TargetDegreesPerSecond(value);
        if (!double.IsFinite(elapsed) || elapsed <= 0) return;
        elapsed = Math.Min(elapsed, 0.1); // Never catch up a delayed or hidden frame.
        var target = _target;
        var inertia = target > Speed ? 1.2 : 1.8;
        var decay = Math.Exp(-elapsed / inertia);
        // Integrate the easing analytically so irregular frame times do not change the motion.
        Phase = (Phase + target * elapsed + (Speed - target) * inertia * (1 - decay)) % 360;
        Speed = target + (Speed - target) * decay;
        if (target == 0 && Speed < RestSpeed) Speed = 0;
    }
}
