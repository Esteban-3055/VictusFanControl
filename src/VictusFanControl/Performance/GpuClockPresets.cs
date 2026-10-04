namespace VictusFanControl.Performance;

internal readonly record struct GpuClockLimitRequest(
    uint MinGraphicsClockMHz,
    uint MaxGraphicsClockMHz);

internal readonly record struct GpuClockPreset(
    bool Enabled,
    uint MinGraphicsClockMHz,
    uint MaxGraphicsClockMHz)
{
    internal static GpuClockPreset Disabled =>
        new(
            Enabled: false,
            MinGraphicsClockMHz: 0,
            MaxGraphicsClockMHz: 0);

    internal GpuClockLimitRequest ToRequest()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "A disabled GPU clock preset has no active request.");
        }

        return new GpuClockLimitRequest(
            MinGraphicsClockMHz,
            MaxGraphicsClockMHz);
    }
}

internal readonly record struct GpuClockPresetSet(
    GpuClockPreset Ac,
    GpuClockPreset Battery)
{
    /// <summary>
    /// User-requested Victus profile values proven manually through nvidia-smi:
    /// AC      -> -lgc 210,1850
    /// Battery -> -lgc 210,1200
    ///
    /// These are desired preset values only. Merely constructing this set does
    /// not grant startup/resume authority and performs no NVML write.
    /// </summary>
    internal static GpuClockPresetSet UserRequestedVictus =>
        new(
            Ac:
                new GpuClockPreset(
                    Enabled: true,
                    MinGraphicsClockMHz: 210,
                    MaxGraphicsClockMHz: 1850),

            Battery:
                new GpuClockPreset(
                    Enabled: true,
                    MinGraphicsClockMHz: 210,
                    MaxGraphicsClockMHz: 1200));

    internal static GpuClockPresetSet Disabled =>
        new(
            GpuClockPreset.Disabled,
            GpuClockPreset.Disabled);
}

internal readonly record struct GpuClockPresetSelection(
    PerformancePowerSourceKind Source,
    PerformancePresetSlot? Slot,
    bool SourceKnown,
    bool Enabled,
    GpuClockLimitRequest? Request,
    string Status);

/// <summary>
/// Pure AC/Battery selector for the future GPU locked-graphics-clock
/// controller. It performs no NVML calls and never spawns nvidia-smi.
///
/// Hardware ownership, recovery, driver-reset handling and external-writer
/// conflict logic remain separate from source selection.
/// </summary>
internal sealed class GpuClockPresetPolicy
{
    private readonly GpuClockPresetSet _presets;

    internal GpuClockPresetPolicy(
        GpuClockPresetSet presets)
    {
        ValidatePreset(
            PerformancePresetSlot.Ac,
            presets.Ac);

        ValidatePreset(
            PerformancePresetSlot.Battery,
            presets.Battery);

        _presets = presets;
    }

    internal GpuClockPresetSet Presets =>
        _presets;

    internal GpuClockPresetSelection Resolve(
        PerformancePowerSourceKind source) =>
        source switch
        {
            PerformancePowerSourceKind.Ac =>
                ResolveKnown(
                    source,
                    PerformancePresetSlot.Ac,
                    _presets.Ac),

            PerformancePowerSourceKind.Battery =>
                ResolveKnown(
                    source,
                    PerformancePresetSlot.Battery,
                    _presets.Battery),

            PerformancePowerSourceKind.Unknown =>
                new GpuClockPresetSelection(
                    Source: source,
                    Slot: null,
                    SourceKnown: false,
                    Enabled: false,
                    Request: null,
                    Status:
                        "GPU_CLOCK_SOURCE_UNKNOWN__NO_PRESET_AUTHORITY"),

            _ =>
                throw new ArgumentOutOfRangeException(
                    nameof(source),
                    source,
                    "Unknown performance power source kind.")
        };

    private static GpuClockPresetSelection ResolveKnown(
        PerformancePowerSourceKind source,
        PerformancePresetSlot slot,
        GpuClockPreset preset)
    {
        if (!preset.Enabled)
        {
            return new GpuClockPresetSelection(
                Source: source,
                Slot: slot,
                SourceKnown: true,
                Enabled: false,
                Request: null,
                Status:
                    $"GPU_CLOCK_PRESET_{slot.ToString().ToUpperInvariant()}_DISABLED");
        }

        return new GpuClockPresetSelection(
            Source: source,
            Slot: slot,
            SourceKnown: true,
            Enabled: true,
            Request: preset.ToRequest(),
            Status:
                $"GPU_CLOCK_PRESET_{slot.ToString().ToUpperInvariant()}_SELECTED");
    }

    private static void ValidatePreset(
        PerformancePresetSlot slot,
        GpuClockPreset preset)
    {
        if (!preset.Enabled)
            return;

        if (preset.MinGraphicsClockMHz == 0 ||
            preset.MaxGraphicsClockMHz == 0 ||
            preset.MaxGraphicsClockMHz <
                preset.MinGraphicsClockMHz)
        {
            throw new ArgumentOutOfRangeException(
                nameof(preset),
                $"Enabled {slot} GPU clock preset requires positive clocks and Max >= Min.");
        }
    }
}

/// <summary>
/// Source-driven performance configuration keeps CPU RAPL and GPU clock
/// control in separate ownership domains while sharing the same AC/Battery
/// source selector.
/// </summary>
internal readonly record struct PerformancePresetConfiguration(
    CpuPowerPresetSet Cpu,
    GpuClockPresetSet Gpu)
{
    internal static PerformancePresetConfiguration
        UserRequestedGpuWithCpuDisabled =>
        new(
            CpuPowerPresetSet.Disabled,
            GpuClockPresetSet.UserRequestedVictus);
}
