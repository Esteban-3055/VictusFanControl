namespace VictusFanControl.Control.Adaptive;

/// <summary>
/// Conservative HP 8C40 candidate curve prepared for offline shadow/replay and
/// UI preview. It is deliberately not a production authorization and has not
/// been physically validated as an automatic control policy.
/// </summary>
public static class Hp8C40AdaptiveCandidateV1
{
    public const string Id = "HP-8C40-ADAPTIVE-CANDIDATE-V1";
    public const bool PhysicallyValidated = false;
    public const bool AuthorizedForProduction = false;

    public static AdaptiveFanPolicyConfig Create() =>
        new(
            MinimumLevel: 10,
            MaximumLevel: 50,
            MaximumUpStepPerSample: 4,
            MaximumDownStepPerSample: 1,
            DecreaseConfirmationSamples: 5,
            DecreaseDeadbandLevels: 1.0,
            MaximumSampleGap: TimeSpan.FromSeconds(3),
            CpuTemperatureCurve:
            [
                new(40, 10),
                new(50, 14),
                new(60, 20),
                new(70, 28),
                new(78, 36),
                new(85, 44),
                new(90, 50)
            ],
            GpuTemperatureCurve:
            [
                new(35, 10),
                new(45, 14),
                new(55, 20),
                new(65, 28),
                new(72, 36),
                new(78, 44),
                new(84, 50)
            ],
            CpuPowerCurve:
            [
                new(0, 10),
                new(15, 10),
                new(30, 16),
                new(45, 22),
                new(65, 30),
                new(90, 40),
                new(115, 50)
            ],
            GpuPowerCurve:
            [
                new(0, 10),
                new(20, 10),
                new(40, 16),
                new(70, 24),
                new(95, 34),
                new(115, 42),
                new(140, 50)
            ],
            CpuLoadCurve:
            [
                new(0, 10),
                new(25, 10),
                new(50, 16),
                new(75, 22),
                new(100, 28)
            ],
            GpuLoadCurve:
            [
                new(0, 10),
                new(25, 10),
                new(50, 16),
                new(75, 22),
                new(100, 28)
            ]);
}
