namespace VictusFanControl.Hardware.Hp;

/// <summary>User-requested product command limits: CPU/GPU 54. Levels are nominal, not verified RPM.</summary>
public static class Hp8C40ProductFanTargets
{
    public const int MaximumLogicalLevel = 54;
    public const int MaximumGpuLevel = 54;
    public static (int Cpu, int Gpu) FromLogical(int level)
    {
        if (level is <10 or >MaximumLogicalLevel) throw new ArgumentOutOfRangeException(nameof(level));
        return (level, Math.Min(level, MaximumGpuLevel));
    }
    internal static bool IsPair(int cpu, int gpu) => cpu is >=10 and <=MaximumLogicalLevel && gpu == Math.Min(cpu, MaximumGpuLevel);
    internal static HpBiosRequest BuildRequest(int level)
    {
        var target = FromLogical(level);
        return new(Hp8C40BiosFanControl.DefaultCommand, Hp8C40BiosFanControl.SetFanLevelCommandType,
            [(byte)target.Cpu, (byte)target.Gpu, 0, 0], 0);
    }
}
