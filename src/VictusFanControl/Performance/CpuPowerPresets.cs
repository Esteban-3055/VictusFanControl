namespace VictusFanControl.Performance;

internal enum CpuPowerSourceKind
{
    Unknown,
    Ac,
    Battery
}

internal enum CpuPowerPresetSlot
{
    Ac,
    Battery
}

internal readonly record struct CpuPowerPreset(
    bool Enabled,
    double Pl1Watts,
    double Pl2Watts)
{
    internal static CpuPowerPreset Disabled =>
        new(
            Enabled: false,
            Pl1Watts: 0,
            Pl2Watts: 0);

    internal CpuPowerLimitRequest ToRequest()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "A disabled CPU power preset has no active request.");
        }

        return new CpuPowerLimitRequest(
            Pl1Watts,
            Pl2Watts);
    }
}

internal readonly record struct CpuPowerPresetSet(
    CpuPowerPreset Ac,
    CpuPowerPreset Battery)
{
    internal static CpuPowerPresetSet Disabled =>
        new(
            CpuPowerPreset.Disabled,
            CpuPowerPreset.Disabled);
}

internal readonly record struct CpuPowerPresetSelection(
    CpuPowerSourceKind Source,
    CpuPowerPresetSlot? Slot,
    bool SourceKnown,
    bool Enabled,
    CpuPowerLimitRequest? Request,
    string Status);

/// <summary>
/// Pure source-to-preset selector. It owns no power notifications, timers,
/// journal, limiter state or hardware writes.
///
/// Runtime authority is intentionally separate: resolving a Battery preset
/// does not by itself authorize applying it at startup, after guardian restart,
/// or after a Yielded external-writer episode.
/// </summary>
internal sealed class CpuPowerPresetPolicy
{
    private readonly CpuPowerPresetSet _presets;

    internal CpuPowerPresetPolicy(
        CpuPowerPresetSet presets)
    {
        ValidatePreset(
            CpuPowerPresetSlot.Ac,
            presets.Ac);

        ValidatePreset(
            CpuPowerPresetSlot.Battery,
            presets.Battery);

        _presets = presets;
    }

    internal CpuPowerPresetSet Presets =>
        _presets;

    internal CpuPowerPresetSelection Resolve(
        CpuPowerSourceKind source) =>
        source switch
        {
            CpuPowerSourceKind.Ac =>
                ResolveKnown(
                    source,
                    CpuPowerPresetSlot.Ac,
                    _presets.Ac),

            CpuPowerSourceKind.Battery =>
                ResolveKnown(
                    source,
                    CpuPowerPresetSlot.Battery,
                    _presets.Battery),

            CpuPowerSourceKind.Unknown =>
                new CpuPowerPresetSelection(
                    Source: source,
                    Slot: null,
                    SourceKnown: false,
                    Enabled: false,
                    Request: null,
                    Status:
                        "CPU_POWER_SOURCE_UNKNOWN__NO_PRESET_AUTHORITY"),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source,
                    "Unknown CPU power source kind.")
        };

    private static CpuPowerPresetSelection ResolveKnown(
        CpuPowerSourceKind source,
        CpuPowerPresetSlot slot,
        CpuPowerPreset preset)
    {
        if (!preset.Enabled)
        {
            return new CpuPowerPresetSelection(
                Source: source,
                Slot: slot,
                SourceKnown: true,
                Enabled: false,
                Request: null,
                Status:
                    $"CPU_POWER_PRESET_{slot.ToString().ToUpperInvariant()}_DISABLED");
        }

        return new CpuPowerPresetSelection(
            Source: source,
            Slot: slot,
            SourceKnown: true,
            Enabled: true,
            Request: preset.ToRequest(),
            Status:
                $"CPU_POWER_PRESET_{slot.ToString().ToUpperInvariant()}_SELECTED");
    }

    private static void ValidatePreset(
        CpuPowerPresetSlot slot,
        CpuPowerPreset preset)
    {
        if (!preset.Enabled)
            return;

        if (!double.IsFinite(preset.Pl1Watts) ||
            !double.IsFinite(preset.Pl2Watts) ||
            preset.Pl1Watts < 10 ||
            preset.Pl2Watts <
                preset.Pl1Watts)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset),
                $"Enabled {slot} CPU power preset must have finite PL1/PL2, PL1 >= 10 W and PL2 >= PL1.");
        }
    }
}
