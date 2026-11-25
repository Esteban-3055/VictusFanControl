namespace VictusFanControl.Product;

public enum ProductFunction { CpuLimits, GpuClocks, Ctgp, ExternalCooling }
public enum ProductExternalOutputMode { FollowInternalFans, Fixed, TemperatureCurve }
public enum ProductFunctionBinding { Independent, StartWithAutomatic, FollowAutomatic, FollowInternalFans, StartWithInternalFans }
public enum ProductFunctionFanMode { Firmware, Manual, Automatic }
public enum ProductFunctionPower { Unknown, Ac, Battery }
public enum ProductFunctionEntryReason { User, ApplicationStartup, AutomaticRecovery, SystemResume, Maintenance }
public enum ProductFunctionWait { None, Off, ApplicationClosing, SystemSuspended, PowerSource, Ac,
    Automatic, InternalFans, SharedWmi, OwnController }

/// <summary>Preferences only. Selection for a linked start is separate from runtime intent and hardware ownership.</summary>
public sealed record ProductFunctionPolicy(ProductFunctionBinding Binding, bool SelectedForLinkedStart, bool StartWithApplication = false,
    bool PreserveUnselectedOnLinkedStart = false)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Binding)) throw new ArgumentOutOfRangeException(nameof(Binding));
    }
}

public sealed record ProductFunctionPolicies
{
    public bool StopAutomaticOnPerformanceFailure {get;init;}=true;
    public bool StopAutomaticOnCtgpFailure {get;init;}=true;
    public bool ContinueLinkedFunctionsDuringFanRecovery {get;init;}=true;
    public ProductExternalOutputMode ExternalOutputMode {get;init;}
    public int ExternalFixedOutput {get;init;}=130;
    public ProductFunctionPolicy CpuLimits { get; init; } = new(ProductFunctionBinding.StartWithAutomatic, true);
    public ProductFunctionPolicy GpuClocks { get; init; } = new(ProductFunctionBinding.StartWithAutomatic, true);
    public ProductFunctionPolicy Ctgp { get; init; } = new(ProductFunctionBinding.FollowAutomatic, false);
    public ProductFunctionPolicy ExternalCooling { get; init; } = new(ProductFunctionBinding.FollowInternalFans, false);
    public static ProductFunctionPolicies Legacy(ProductFunctionFlags selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return new() { ContinueLinkedFunctionsDuringFanRecovery=false,CpuLimits = new(ProductFunctionBinding.StartWithAutomatic, selected.CpuLimits),
            GpuClocks = new(ProductFunctionBinding.StartWithAutomatic, selected.GpuClocks),
            Ctgp = new(ProductFunctionBinding.FollowAutomatic, selected.Ctgp),
            ExternalCooling = new(ProductFunctionBinding.FollowInternalFans, selected.ExternalCooling) };
    }
    public ProductFunctionPolicy Get(ProductFunction function) => function switch
    {
        ProductFunction.CpuLimits => CpuLimits, ProductFunction.GpuClocks => GpuClocks,
        ProductFunction.Ctgp => Ctgp, ProductFunction.ExternalCooling => ExternalCooling,
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };
    public ProductFunctionPolicies With(ProductFunction function, ProductFunctionPolicy policy) => function switch
    {
        ProductFunction.CpuLimits => this with { CpuLimits = policy }, ProductFunction.GpuClocks => this with { GpuClocks = policy },
        ProductFunction.Ctgp => this with { Ctgp = policy }, ProductFunction.ExternalCooling => this with { ExternalCooling = policy },
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };
    public void Validate()
    {
        if(!Enum.IsDefined(ExternalOutputMode)||ExternalFixedOutput is <0 or >255)throw new ArgumentException("Modo o salida de base externa no válido.");
        foreach (var function in Enum.GetValues<ProductFunction>())
            (Get(function) ?? throw new ArgumentException("Missing function policy.")).Validate();
    }
    public ProductFunctionPolicies Independent()
    {
        Validate();
        var result = this with{ContinueLinkedFunctionsDuringFanRecovery=true,StopAutomaticOnPerformanceFailure=false,StopAutomaticOnCtgpFailure=false,ExternalOutputMode=ProductExternalOutputMode.TemperatureCurve};
        foreach (var function in Enum.GetValues<ProductFunction>())
        {
            var policy=Get(function);
            result = result.With(function, policy with { Binding = policy.Binding switch
                { ProductFunctionBinding.FollowAutomatic => ProductFunctionBinding.StartWithAutomatic,
                  ProductFunctionBinding.FollowInternalFans => ProductFunctionBinding.StartWithInternalFans,
                  _ => policy.Binding }, PreserveUnselectedOnLinkedStart = true });
        }
        return result;
    }
    public ProductFunctionPolicies Unlinked()
    {
        Validate();
        var result=this with{ContinueLinkedFunctionsDuringFanRecovery=true,StopAutomaticOnPerformanceFailure=false,StopAutomaticOnCtgpFailure=false,ExternalOutputMode=ProductExternalOutputMode.TemperatureCurve};
        foreach(var function in Enum.GetValues<ProductFunction>())
            result=result.With(function,Get(function) with { Binding=ProductFunctionBinding.Independent, PreserveUnselectedOnLinkedStart=true });
        return result;
    }
}

/// <summary>Requested actions, never confirmations. Drivers and live sessions are deliberately absent.</summary>
public sealed record ProductFunctionFlags(bool CpuLimits = false, bool GpuClocks = false, bool Ctgp = false, bool ExternalCooling = false)
{
    public bool Get(ProductFunction function) => function switch
    {
        ProductFunction.CpuLimits => CpuLimits, ProductFunction.GpuClocks => GpuClocks,
        ProductFunction.Ctgp => Ctgp, ProductFunction.ExternalCooling => ExternalCooling,
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };
    public ProductFunctionFlags With(ProductFunction function, bool value) => function switch
    {
        ProductFunction.CpuLimits => this with { CpuLimits = value }, ProductFunction.GpuClocks => this with { GpuClocks = value },
        ProductFunction.Ctgp => this with { Ctgp = value }, ProductFunction.ExternalCooling => this with { ExternalCooling = value },
        _ => throw new ArgumentOutOfRangeException(nameof(function))
    };
}

/// <summary>Readiness means permission to acquire a controller, not proof it is already active.</summary>
public sealed record ProductFunctionContext(ProductFunctionFanMode FanMode, ProductFunctionPower Power,
    ProductFunctionFlags OwnPrerequisitesReady, bool SharedWmiReady, bool Suspended = false, bool Closing = false,
    ProductFunctionFanMode? RequestedFanMode=null);
public sealed record ProductFunctionPlan(ProductFunction Function, bool Requested, ProductFunctionWait WaitingFor)
{
    public bool MayRequestActivation => Requested && WaitingFor == ProductFunctionWait.None;
}

/// <summary>Pure policy used by the product coordinator. No IO, clocks, sensor reads or hardware effects.</summary>
public static class ProductFunctionCoordination
{
    public static ProductFunctionFlags LinkedStarts(ProductFunctionPolicies policies,ProductFunctionFanMode previous,
        ProductFunctionFanMode next,ProductFunctionEntryReason reason)
    {
        policies.Validate();
        if(!Enum.IsDefined(previous)||!Enum.IsDefined(next)||!Enum.IsDefined(reason))throw new ArgumentOutOfRangeException(nameof(reason));
        var selected=new ProductFunctionFlags();
        if(previous==next||reason is not(ProductFunctionEntryReason.User or ProductFunctionEntryReason.ApplicationStartup))return selected;
        foreach(var function in Enum.GetValues<ProductFunction>())
        {
            var p=policies.Get(function);
            var linked=p.Binding switch
            {ProductFunctionBinding.StartWithAutomatic or ProductFunctionBinding.FollowAutomatic=>next==ProductFunctionFanMode.Automatic,
             ProductFunctionBinding.StartWithInternalFans or ProductFunctionBinding.FollowInternalFans=>next!=ProductFunctionFanMode.Firmware,_=>false};
            selected=selected.With(function,linked&&p.SelectedForLinkedStart);
        }
        return selected;
    }
    public static ProductFunctionFlags ApplicationStart(ProductFunctionPolicies policies)
    {
        policies.Validate();
        var requested = new ProductFunctionFlags();
        foreach (var function in Enum.GetValues<ProductFunction>())
            requested = requested.With(function, policies.Get(function).StartWithApplication);
        return requested;
    }

    public static ProductFunctionFlags EnterFanMode(ProductFunctionPolicies policies, ProductFunctionFlags requested,
        ProductFunctionFanMode previous, ProductFunctionFanMode next, ProductFunctionEntryReason reason)
    {
        policies.Validate(); ArgumentNullException.ThrowIfNull(requested);
        if (!Enum.IsDefined(previous) || !Enum.IsDefined(next) || !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        // Repainting an existing mode and resuming an interrupted session must
        // never undo an independent disable that the user already requested.
        if (previous == next || reason is ProductFunctionEntryReason.AutomaticRecovery or ProductFunctionEntryReason.SystemResume or ProductFunctionEntryReason.Maintenance)
            return requested;
        foreach (var function in Enum.GetValues<ProductFunction>())
        {
            var policy = policies.Get(function);
            var linkedStart = policy.Binding switch
            {
                ProductFunctionBinding.StartWithAutomatic or ProductFunctionBinding.FollowAutomatic => next == ProductFunctionFanMode.Automatic,
                ProductFunctionBinding.FollowInternalFans or ProductFunctionBinding.StartWithInternalFans => next != ProductFunctionFanMode.Firmware,
                _ => false
            };
            if (linkedStart && (policy.SelectedForLinkedStart || !policy.PreserveUnselectedOnLinkedStart))
                requested = requested.With(function, policy.SelectedForLinkedStart);
        }
        return requested;
    }

    public static IReadOnlyList<ProductFunctionPlan> Plan(ProductFunctionPolicies policies, ProductFunctionFlags requested, ProductFunctionContext context)
    {
        policies.Validate(); ArgumentNullException.ThrowIfNull(requested); ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.OwnPrerequisitesReady);
        if (!Enum.IsDefined(context.FanMode) || !Enum.IsDefined(context.Power)) throw new ArgumentOutOfRangeException(nameof(context));
        if(context.RequestedFanMode is{} requestedMode&&!Enum.IsDefined(requestedMode))throw new ArgumentOutOfRangeException(nameof(context));
        var mode=policies.ContinueLinkedFunctionsDuringFanRecovery?context.RequestedFanMode??context.FanMode:context.FanMode;
        ProductFunctionWait Wait(ProductFunction function)
        {
            if (!requested.Get(function)) return ProductFunctionWait.Off;
            if (context.Closing) return ProductFunctionWait.ApplicationClosing;
            if (context.Suspended) return ProductFunctionWait.SystemSuspended;
            if (function != ProductFunction.ExternalCooling && context.Power == ProductFunctionPower.Unknown) return ProductFunctionWait.PowerSource;
            if (function == ProductFunction.Ctgp && context.Power != ProductFunctionPower.Ac) return ProductFunctionWait.Ac;
            var binding = policies.Get(function).Binding;
            if (binding == ProductFunctionBinding.FollowAutomatic && mode != ProductFunctionFanMode.Automatic) return ProductFunctionWait.Automatic;
            if (binding == ProductFunctionBinding.FollowInternalFans && mode == ProductFunctionFanMode.Firmware) return ProductFunctionWait.InternalFans;
            if (function == ProductFunction.Ctgp && !context.SharedWmiReady) return ProductFunctionWait.SharedWmi;
            return context.OwnPrerequisitesReady.Get(function) ? ProductFunctionWait.None : ProductFunctionWait.OwnController;
        }
        return Array.AsReadOnly(Enum.GetValues<ProductFunction>().Select(function => new ProductFunctionPlan(function, requested.Get(function), Wait(function))).ToArray());
    }
}
