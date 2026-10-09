using VictusFanControl.Control.Adaptive;

namespace VictusFanControl.Product;

/// <summary>Explicit AC trial preset. A hypothesis about lower fan demand, not measured acoustic performance.</summary>
public static class ProductQuietCandidate
{
    public static ProductProfiles Stage(ProductProfiles profiles)
    {
        profiles.Validate();
        var fan=profiles.Ac.Fan;
        var original=fan.UnifiedDemand??throw new InvalidDataException("La candidata requiere la curva única.");
        double previous=10;
        var curve=original.Curve.Select(p=>
        {
            var level=p.Input is >40 and <90 ? Math.Max(previous,Math.Max(10,p.Level-2)) : p.Level;
            previous=level;return p with{Level=level};
        }).ToArray();
        var demand=original with{Curve=curve};
        var tuning=fan.Tuning with {ShortLoadFallTimeConstantSeconds=Math.Min(fan.Tuning.ShortLoadFallTimeConstantSeconds,10),
            ShortLoadDecreaseConfirmationSeconds=Math.Min(fan.Tuning.ShortLoadDecreaseConfirmationSeconds,4),
            FallTimeConstantSeconds=Math.Min(fan.Tuning.FallTimeConstantSeconds,25),
            DecreaseConfirmationSeconds=Math.Min(fan.Tuning.DecreaseConfirmationSeconds,12)};
        var result=profiles with{Ac=profiles.Ac with{Fan=fan with{UnifiedDemand=demand,Tuning=tuning}}};
        result.Validate();return result;
    }
}
