using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only WMI writer for the next unvalidated HP 8C40 range.
/// Production remains capped by Hp8C40TargetProfile and never calls this type.
/// </summary>
internal sealed class Hp8C40UpperFanLevelQualificationControl
{
    public const byte MinimumQualificationLevel = 33;
    public const byte MaximumQualificationLevel = 36;

    private readonly HpOmenBiosWmiClient _client;

    public Hp8C40UpperFanLevelQualificationControl(
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
                $"8C40 upper qualification writer is hard-limited to equal " +
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

        // Use the validated release sequence directly. The production 30..32
        // range check is intentionally irrelevant to FF/FF restoration.
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
                $"8C40 upper fan-level qualification refused: {reason}");
        }
    }
}
