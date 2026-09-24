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
    public const byte MinimumValidatedLevel = (byte)Hp88F8TargetProfile.MinimumValidatedFanLevel;
    public const byte MaximumValidatedLevel = (byte)Hp88F8TargetProfile.MaximumValidatedFanLevel;

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

    public static HpBiosRequest BuildReleaseFanLevelRequest() =>
        new(
            Command: DefaultCommand,
            CommandType: SetFanLevelCommandType,
            Payload: [0xFF, 0xFF, 0x00, 0x00],
            OutputSize: 0);

    /// <summary>
    /// HP GetFanLevel (0x2D) reports the current fan speed level, not the
    /// commanded SetFanLevel target. It therefore must not be used as an
    /// immediate command acknowledgement.
    /// </summary>
    public (byte CpuLevel, byte GpuLevel) GetCurrentFanLevels()
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

    public void ReleaseFanLevelOverride()
    {
        EnsureSupportedBoard();

        var rc = _client.Send(BuildReleaseFanLevelRequest());
        if (rc != 0)
        {
            throw new HpBiosCallException(
                $"HP BIOS rejected FF,FF fan-level release with return code {rc}.");
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

    /// <summary>
    /// Mirrors the relevant OmenMon fan-program release sequence for the
    /// target: release the fixed fan-level override with FF,FF, then restore
    /// LegacyDefault. The EC manual flag/countdown are intentionally left
    /// untouched because OMEN Gaming Hub may own that state.
    /// </summary>
    public void RestoreFirmwareAuto()
    {
        EnsureSupportedBoard();

        ExecuteRestoreSequence(
            releaseAction: () =>
            {
                var rc = _client.Send(BuildReleaseFanLevelRequest());
                if (rc != 0)
                {
                    throw new HpBiosCallException(
                        $"HP BIOS rejected FF,FF fan-level release with return code {rc}.");
                }
            },
            legacyDefaultAction: () =>
            {
                var rc = _client.Send(BuildLegacyDefaultRequest());
                if (rc != 0)
                {
                    throw new HpBiosCallException(
                        $"HP BIOS rejected LegacyDefault restore with return code {rc}.");
                }
            });
    }

    internal static void ExecuteRestoreSequence(
        Action releaseAction,
        Action legacyDefaultAction)
    {
        Exception? releaseFailure = null;
        Exception? modeFailure = null;

        // SetFanLevel is known on some HP systems to take effect even when the
        // BIOS call reports an error. Never let an uncertain FF,FF result prevent
        // the LegacyDefault attempt from running.
        try
        {
            releaseAction();
        }
        catch (Exception ex)
        {
            releaseFailure = ex;
        }

        try
        {
            legacyDefaultAction();
        }
        catch (Exception ex)
        {
            modeFailure = ex;
        }

        ThrowIfRestoreSequenceFailed(releaseFailure, modeFailure);
    }

    internal static void ThrowIfRestoreSequenceFailed(
        Exception? releaseFailure,
        Exception? modeFailure)
    {
        if (releaseFailure is null && modeFailure is null)
        {
            return;
        }

        if (releaseFailure is not null && modeFailure is not null)
        {
            throw new AggregateException(
                "HP firmware-auto restore had failures in both FF,FF release and LegacyDefault.",
                releaseFailure,
                modeFailure);
        }

        if (releaseFailure is not null)
        {
            throw new HpBiosCallException(
                "HP FF,FF release reported a failure; LegacyDefault was still attempted.",
                releaseFailure);
        }

        throw new HpBiosCallException(
            "HP LegacyDefault restore reported a failure after FF,FF release.",
            modeFailure!);
    }

    private static void EnsureSupportedBoard()
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp88F8TargetProfile.Matches(hardware, out var reason))
        {
            throw new InvalidOperationException(
                $"HP BIOS fan operation refused: {reason}");
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
