using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// Narrow 88F8 BIOS fan operations. No arbitrary command API is exposed to
/// the fan controller.
/// </summary>
public sealed class Hp88F8BiosFanControl
{
    public const uint DefaultCommand = 0x00020008;
    public const uint SetFanModeCommandType = 0x1A;

    private readonly HpOmenBiosWmiClient _client;

    public Hp88F8BiosFanControl(HpOmenBiosWmiClient? client = null)
    {
        _client = client ?? new HpOmenBiosWmiClient();
    }

    public static HpBiosRequest BuildLegacyDefaultRequest() =>
        new(
            Command: DefaultCommand,
            CommandType: SetFanModeCommandType,
            Payload: [0xFF, 0x00, 0x00, 0x00],
            OutputSize: 0);

    public void RestoreLegacyDefault()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!string.Equals(
                hardware.BoardProduct,
                "88F8",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"HP BIOS fan restore refused on unsupported board '{hardware.BoardProduct}'.");
        }

        var rc = _client.Send(BuildLegacyDefaultRequest());
        if (rc != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected LegacyDefault restore with return code {rc}.");
        }
    }
}
