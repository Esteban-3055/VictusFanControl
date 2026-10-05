using VictusFanControl.Product;

namespace VictusFanControl.Performance;

/// <summary>Validated product preferences, never raw register or arbitrary clock commands.</summary>
public sealed record PerformanceGuiSessionConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public string TargetProfileId { get; init; } = CpuPowerProductDefaults.TargetProfileId;
    public bool CpuEnabled { get; init; } = true;
    public bool GpuEnabled { get; init; } = true;
    public int AcPl1Watts { get; init; } = 35;
    public int AcPl2Watts { get; init; } = 60;
    public int BatteryPl1Watts { get; init; } = 8;
    public int BatteryPl2Watts { get; init; } = 15;

    public int AcGpuMaximumMHz { get; init; } = GpuProductPreferences.QualifiedAcMaximumMHz;
    public int BatteryGpuMaximumMHz { get; init; } = GpuProductPreferences.QualifiedBatteryMaximumMHz;

    internal GpuClockPresetSet GpuPresets()
    {
        Validate();
        return new(new(true, GpuProductPreferences.MinimumMHz, (uint)AcGpuMaximumMHz),
            new(true, GpuProductPreferences.MinimumMHz, (uint)BatteryGpuMaximumMHz));
    }

    public void Validate()
    {
        if (AcGpuMaximumMHz < GpuProductPreferences.MinimumMHz || AcGpuMaximumMHz > GpuProductPreferences.QualifiedAcMaximumMHz ||
            BatteryGpuMaximumMHz < GpuProductPreferences.MinimumMHz || BatteryGpuMaximumMHz > GpuProductPreferences.QualifiedBatteryMaximumMHz)
            throw new ArgumentException("GPU preferences outside the conservative envelope.");
        if (GpuEnabled && !GpuProductPreferences.CustomClockExecutionAuthorized &&
            (AcGpuMaximumMHz != GpuProductPreferences.QualifiedAcMaximumMHz || BatteryGpuMaximumMHz != GpuProductPreferences.QualifiedBatteryMaximumMHz))
            throw new InvalidOperationException("Los límites GPU personalizados están guardados, pero su aplicación requiere calificación física. Valores habilitados: AC 1850 / Batería 1200 MHz.");
        if (SchemaVersion != 1 || TargetProfileId != CpuPowerProductDefaults.TargetProfileId ||
            (!CpuEnabled && !GpuEnabled) ||
            !CpuPowerProductDefaults.IsConfigurable(AcPl1Watts, AcPl2Watts) ||
            !CpuPowerProductDefaults.IsConfigurable(BatteryPl1Watts, BatteryPl2Watts))
            throw new ArgumentException("Invalid target-specific GUI performance configuration.");
    }

    internal CpuPowerPresetSet CpuPresets()
    {
        Validate();
        return CpuPowerProductDefaults.CreatePresetSet(AcPl1Watts, AcPl2Watts, BatteryPl1Watts, BatteryPl2Watts);
    }
}
