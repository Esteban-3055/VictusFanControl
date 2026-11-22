using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductForm
{
    private bool HandleFunctionCommand(string id)
    {
        if(!id.StartsWith("function-",StringComparison.Ordinal)&&!id.StartsWith("functions-",StringComparison.Ordinal))return false;
        if(_canvas.Busy)return true;
        var policies=_draft.FunctionPolicies();
        switch(id)
        {
            case "functions-base-mode":Change(_draft with{Functions=policies with{ExternalOutputMode=(ProductExternalOutputMode)(((int)policies.ExternalOutputMode+1)%3)}});return true;
            case "functions-base-output":
                using(var dialog=new ProductNumericDialog("Salida fija de la base · 0 detiene; apagar libera al control local",policies.ExternalFixedOutput,0,255,
                    text=>int.TryParse(text,out var duty)&&duty is >=0 and <=255?SetFunctionOutput(duty):"Introduce un número de 0 a 255.",
                    help:"0 solicita parada; Apagar retira el control remoto y la base recupera su control local. Solo cambia el borrador."))dialog.ShowDialog(this);
                return true;
            case "functions-compatible":Change(_draft with{Functions=ProductFunctionPolicies.Legacy(new(_draft.CpuEnabled,_draft.GpuEnabled,_draft.CtgpEnabled,true))});return true;
            case "functions-independent":Change(_draft with{Functions=policies.Independent()});return true;
            case "functions-unlinked":Change(_draft with{Functions=policies.Unlinked()});return true;
            case "functions-performance-fault":Change(_draft with{Functions=policies with{StopAutomaticOnPerformanceFailure=!policies.StopAutomaticOnPerformanceFailure}});return true;
            case "functions-ctgp-fault":Change(_draft with{Functions=policies with{StopAutomaticOnCtgpFailure=!policies.StopAutomaticOnCtgpFailure}});return true;
            case "functions-apply":_ = RunAsync(()=>_runtime?.ConfigureFunctionsAsync(policies)??Task.CompletedTask);return true;
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
}
