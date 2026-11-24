using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed class ProductFunctionPoliciesCommittedException(Exception inner)
    : IOException("Reglas aplicadas; una función requiere revisión: "+inner.Message,inner);
internal sealed class ProductFunctionRulesSavedWithIssuesException(ProductProfiles settings,Exception inner)
    : IOException("Reglas aplicadas y guardadas. Revisa la función pendiente: "+inner.Message,inner)
{internal ProductProfiles Settings {get;}=settings;}

// Shared by the inline choices and the isolated editor tests. No controller,
// registration, device discovery or ownership is constructed here.
internal static class ProductFunctionRuleEditing
{
    internal static ProductProfiles SelectBinding(ProductProfiles draft,ProductFunction function,ProductFunctionBinding binding)
    {
        if(!Enum.IsDefined(function)||!Enum.IsDefined(binding))throw new ArgumentOutOfRangeException(nameof(binding));
        var policies=draft.FunctionPolicies();
        return draft with{Functions=policies.With(function,policies.Get(function) with
            {Binding=binding,SelectedForLinkedStart=binding!=ProductFunctionBinding.Independent,PreserveUnselectedOnLinkedStart=true})};
    }
    internal static ProductProfiles SelectBaseMode(ProductProfiles draft,ProductExternalOutputMode mode)
    {
        if(!Enum.IsDefined(mode))throw new ArgumentOutOfRangeException(nameof(mode));
        return draft with{Functions=draft.FunctionPolicies() with{ExternalOutputMode=mode}};
    }
    internal static async Task<ProductProfiles> ApplyAndSaveAsync(ProductProfiles saved,ProductFunctionPolicies policies,
        Func<ProductFunctionPolicies,Task> apply,Action<ProductProfiles> save)
    {
        policies.Validate();
        var next=ProductProfilesStore.Copy(saved) with{Functions=policies};
        ProductFunctionPoliciesCommittedException? issue=null;
        try{await apply(policies).ConfigureAwait(false);}
        catch(ProductFunctionPoliciesCommittedException error){issue=error;}
        try{await Task.Run(()=>save(next)).ConfigureAwait(false);}
        catch(Exception error)
        {throw new IOException("Reglas aplicadas en la sesión, pero NO guardadas. Puedes reintentar sin cambiar el encendido. "+error.Message,error);}
        if(issue is not null)throw new ProductFunctionRulesSavedWithIssuesException(next,issue.InnerException??issue);
        return next;
    }
}
