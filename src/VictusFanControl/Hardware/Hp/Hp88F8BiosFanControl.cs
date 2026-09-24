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
    public const uint GetFanLevelCommandType = 0x2D;
    public const uint SetFanLevelCommandType = 0x2E;
    public const byte MinimumValidatedLevel = 14;
    public const byte MaximumValidatedLevel = 50;

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

    public static HpBiosRequest BuildGetFanLevelRequest() =>
        new(
            Command: DefaultCommand,
            CommandType: GetFanLevelCommandType,
            Payload: [0x00, 0x00, 0x00, 0x00],
            OutputSize: 128);

    public static HpBiosRequest BuildSetFanLevelRequest(byte cpuLevel, byte gpuLevel)
    {
        ValidateLevel(cpuLevel, nameof(cpuLevel));
        ValidateLevel(gpuLevel, nameof(gpuLevel));

        return new HpBiosRequest(
            Command: DefaultCommand,
            CommandType: SetFanLevelCommandType,
            Payload: [cpuLevel, gpuLevel, 0x00, 0x00],
            OutputSize: 0);
    }

    public (byte CpuLevel, byte GpuLevel) GetFanLevels()
    {
        EnsureSupportedBoard();

        var response = _client.SendWithResponse(BuildGetFanLevelRequest());
        if (response.ReturnCode != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected GetFanLevel with return code {response.ReturnCode}.");
        }

        if (response.Data.Length < 2)
        {
            throw new HpBiosCallException(
                $"HP BIOS GetFanLevel returned only {response.Data.Length} byte(s).");
        }

        return (response.Data[0], response.Data[1]);
    }

    public void SetFanLevel(byte cpuLevel, byte gpuLevel)
    {
        EnsureSupportedBoard();

        var rc = _client.Send(BuildSetFanLevelRequest(cpuLevel, gpuLevel));
        if (rc != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected fan-level command with return code {rc}.");
        }
    }

    public void RestoreLegacyDefault()
    {
        EnsureSupportedBoard();

        var rc = _client.Send(BuildLegacyDefaultRequest());
        if (rc != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected LegacyDefault restore with return code {rc}.");
        }
    }

    private static void EnsureSupportedBoard()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!string.Equals(
                hardware.BoardProduct,
                "88F8",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"HP BIOS fan operation refused on unsupported board '{hardware.BoardProduct}'.");
        }
    }

    private static void ValidateLevel(byte level, string name)
    {
        if (level < MinimumValidatedLevel || level > MaximumValidatedLevel)
        {
            throw new ArgumentOutOfRangeException(
                name,
                level,
                $"Validated 88F8 fan-level range is {MinimumValidatedLevel}-{MaximumValidatedLevel}.");
        }
    }
}
