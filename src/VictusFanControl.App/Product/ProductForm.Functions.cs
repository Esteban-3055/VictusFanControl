using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool HandleFunctionCommand(string id)
    {
        if(!id.StartsWith("function-",StringComparison.Ordinal)&&!id.StartsWith("functions-",StringComparison.Ordinal))return false;
        if(_canvas.Busy)return true;
        if(_canvas.HandleFunctionEditorCommand(id,Change))return true;
        var policies=_draft.FunctionPolicies();
        switch(id)
        {
            case "functions-advanced":_canvas.FunctionAdvanced=!_canvas.FunctionAdvanced;_canvas.Invalidate();return true;
            case "functions-base-output":
                using(var dialog=new ProductNumericDialog("Velocidad fija de la base (%) · 0 solicita parada",(int)Math.Round(policies.ExternalFixedOutput*100.0/255),0,100,
                    text=>int.TryParse(text,out var duty)&&duty is >=0 and <=100?SetFunctionOutput((int)Math.Round(duty*255.0/100)):"Introduce un porcentaje de 0 a 100.",
                    help:"0 solicita parada; Apagar retira el control remoto y la base recupera su control local. Solo cambia el borrador."))dialog.ShowDialog(this);
                return true;
            case "functions-compatible":Change(_draft with{Functions=ProductFunctionPolicies.Legacy(new(_draft.CpuEnabled,_draft.GpuEnabled,_draft.CtgpEnabled,true))});return true;
            case "functions-independent":Change(_draft with{Functions=policies.Independent()});return true;
            case "functions-unlinked":Change(_draft with{Functions=policies.Unlinked()});return true;
            case "functions-performance-fault":Change(_draft with{Functions=policies with{StopAutomaticOnPerformanceFailure=!policies.StopAutomaticOnPerformanceFailure}});return true;
            case "functions-ctgp-fault":Change(_draft with{Functions=policies with{StopAutomaticOnCtgpFailure=!policies.StopAutomaticOnCtgpFailure}});return true;
            case "functions-apply":_ = RunAsync(()=>_runtime?.ConfigureFunctionsAsync(policies)??Task.CompletedTask);return true;
            case "functions-apply-save":_ = ApplyAndSaveFunctionRulesAsync();return true;
        }
        var parts=id.Split('-');
        if(parts.Length!=3||!int.TryParse(parts[1],out var value)||!Enum.IsDefined((ProductFunction)value))return true;
        var function=(ProductFunction)value;var policy=policies.Get(function);
        if(parts[2] is "on" or "off")
        {
            var enabled=parts[2]=="on";var owner=_runtime;var profiles=Draft;
            _startupAutomatic?.Cancel("Solicitud de función cambiada por el usuario.");
            _ = RunAsync(()=>owner is null?Task.CompletedTask:function switch
            {
                ProductFunction.CpuLimits=>owner.SetPerformanceDomainAsync(true,enabled,profiles),
                ProductFunction.GpuClocks=>owner.SetPerformanceDomainAsync(false,enabled,profiles),
                ProductFunction.Ctgp=>owner.SetCtgpAsync(enabled),
                _=>owner.SetExternalCoolingRequestedAsync(enabled)
            });return true;
        }
        policy=parts[2] switch
        {
            "binding"=>policy with{Binding=(ProductFunctionBinding)(((int)policy.Binding+1)%Enum.GetValues<ProductFunctionBinding>().Length)},
            "linked"=>policy with{SelectedForLinkedStart=!policy.SelectedForLinkedStart},
            "startup"=>policy with{StartWithApplication=!policy.StartWithApplication},_=>policy
        };
        Change(_draft with{Functions=policies.With(function,policy)});return true;
    }
    private string? SetFunctionOutput(int duty)
    {Change(_draft with{Functions=_draft.FunctionPolicies() with{ExternalFixedOutput=duty}});return null;}
    private Task ApplyAndSaveFunctionRulesAsync()=>RunAsync(async()=>
    {
        var owner=_runtime??throw new InvalidOperationException("No hay un controlador disponible para aplicar reglas.");
        var policies=Draft.FunctionPolicies();
        // Save only these rules. Curves, caps and other unsaved drafts retain
        // their own editing and Apply actions.
        ProductProfiles settings;string? issue=null;
        try{settings=await ProductFunctionRuleEditing.ApplyAndSaveAsync(_saved,policies,owner.ConfigureFunctionsAsync,
            next=>ProductProfilesStore.Save(next,_profilesPath));}
        catch(ProductFunctionRulesSavedWithIssuesException error){settings=error.Settings;issue=error.Message;}
        _saved=settings;_hasSavedBaseline=true;
        _canvas.Dirty=ProductProfilesStore.Serialize(Draft)!=ProductProfilesStore.Serialize(_saved);
        _canvas.Notice=issue??"Reglas aplicadas y guardadas. Las solicitudes actuales se muestran por separado.";
    });
}
