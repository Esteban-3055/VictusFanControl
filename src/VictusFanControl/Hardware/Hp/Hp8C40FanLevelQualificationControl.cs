using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Qualification-only WMI writer for the exact HP 8C40 target.
///
/// This type intentionally exposes only equal CPU/GPU levels 30, 31 and 32.
/// It is not used by the production backend and does not expand the validated
/// production command range. Its sole purpose is bounded hardware
/// characterization before those levels are promoted into Hp8C40TargetProfile.
/// </summary>
internal sealed class Hp8C40FanLevelQualificationControl
{
    public const byte MinimumQualificationLevel = 30;
    public const byte MaximumQualificationLevel = 32;

    private readonly HpOmenBiosWmiClient _client;

    public Hp8C40FanLevelQualificationControl(
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
                $"8C40 qualification writer is hard-limited to equal " +
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
        new Hp8C40BiosFanControl(_client).RestoreFirmwareAuto();
    }

    private static void EnsureSupportedTarget()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var reason))
        {
            throw new InvalidOperationException(
                $"8C40 fan-level qualification refused: {reason}");
        }
    }
}
