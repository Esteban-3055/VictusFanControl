using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only writer for the broad HP 8C40 characterization envelope.
///
/// This is deliberately separate from the production backend. It can issue only
/// equal CPU/GPU levels 10..50 and is used exclusively by the guarded extended
/// range hardware test.
/// </summary>
internal sealed class Hp8C40ExtendedFanLevelQualificationControl
{
    public const byte MinimumQualificationLevel = 10;
    public const byte MaximumQualificationLevel = 50;

    private readonly HpOmenBiosWmiClient _client;

    public Hp8C40ExtendedFanLevelQualificationControl(
        HpOmenBiosWmiClient? client = null)
    {
        _client = client ?? new HpOmenBiosWmiClient();
    }

    public void SetEqualLevel(byte level)
    {
        EnsureSupportedTarget();

        if (level < MinimumQualificationLevel ||
            level > MaximumQualificationLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                $"8C40 extended qualification writer is hard-limited to equal " +
                $"{MinimumQualificationLevel}..{MaximumQualificationLevel}.");
        }

        var request = new HpBiosRequest(
            Command: Hp8C40BiosFanControl.DefaultCommand,
            CommandType: Hp8C40BiosFanControl.SetFanLevelCommandType,
            Payload: [level, level, 0x00, 0x00],
            OutputSize: 0);

        var rc = _client.Send(request);
        if (rc != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected qualification fan level {level}/{level} " +
                $"with return code {rc}.");
        }
    }

    public void RestoreFirmwareAuto()
    {
        EnsureSupportedTarget();

        var release = Hp8C40BiosFanControl.BuildReleaseFanLevelRequest();
        var legacy = Hp8C40BiosFanControl.BuildLegacyDefaultRequest();

        Hp8C40BiosFanControl.ExecuteRestoreSequence(
            releaseAction: () =>
            {
                var rc = _client.Send(release);
                if (rc != 0)
                {
                    throw new HpBiosCallException(
                        $"HP BIOS rejected FF,FF release with return code {rc}.");
                }
            },
            legacyDefaultAction: () =>
            {
                var rc = _client.Send(legacy);
                if (rc != 0)
                {
                    throw new HpBiosCallException(
                        $"HP BIOS rejected LegacyDefault restore with return code {rc}.");
                }
            });
    }

    private static void EnsureSupportedTarget()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var reason))
        {
            throw new InvalidOperationException(
                $"8C40 extended fan-level qualification refused: {reason}");
        }
    }
}
