using VictusFanControl.Product;

namespace VictusFanControl.App;

internal enum ProductFunctionChooser { None, Binding, ExternalMode }

internal sealed partial class ProductCanvas
{
    internal static string FunctionName(ProductFunction function)=>function switch
    {ProductFunction.CpuLimits=>"Límites CPU",ProductFunction.GpuClocks=>"Límites GPU",ProductFunction.Ctgp=>"cTGP HP",_=>"Base externa"};
    internal static string BindingName(ProductFunctionBinding binding)=>binding switch
    {
        ProductFunctionBinding.Independent=>"Sin vínculo",
        ProductFunctionBinding.StartWithAutomatic=>"Iniciar con Automático",
        ProductFunctionBinding.FollowAutomatic=>"Seguir Automático",
        ProductFunctionBinding.FollowInternalFans=>"Seguir ventiladores",
        _=>"Iniciar con ventiladores"
    };
    private static string FunctionStatusText(string status)=>status switch
    {"Disabled"=>"Desactivado","Active"=>"Activo","ActiveUnverified"=>"Solicitud aceptada","Recovering"=>"Comprobando",
        "Applying"=>"Aplicando","Releasing"=>"Restaurando","Restored"=>"Restaurado","Unsupported"=>"No compatible",
        "Contested"=>"Otro programa cambió el límite","ReacquiredPendingStability"=>"Comprobando estabilidad",
        "Yielded"=>"Control cedido","SwitchingPreset"=>"Cambiando perfil",
        "Failed" or "Faulted"=>"Fallo","RecoveryRequired"=>"Recuperación pendiente","AcOnly"=>"Espera de cargador",
        "Waiting"=>"En espera","Unknown"=>"Sin confirmar",_=>status};
    private static string FanAuthorityText(string status)=>status switch
    {"Custom"=>"Control propio","Firmware"=>"Control del sistema","Faulted"=>"Sin confirmar",_=>status};
    private string FanModeText=>State.FanMode=="Automatic"?"Automático":State.FanMode;
    private ProductFunction _selectedFunction=ProductFunction.CpuLimits;
    internal ProductFunction SelectedFunction {get=>_selectedFunction;set{_selectedFunction=value;CloseFunctionChooser();}}
    private bool _functionAdvanced;
    internal bool FunctionAdvanced {get=>_functionAdvanced;set{_functionAdvanced=value;CloseFunctionChooser();}}
    internal ProductFunctionChooser FunctionChooser {get;private set;}
    internal void CloseFunctionChooser()=>FunctionChooser=ProductFunctionChooser.None;
    internal static string ExternalModeName(ProductExternalOutputMode mode)=>mode switch
    {ProductExternalOutputMode.Fixed=>"Velocidad fija",ProductExternalOutputMode.TemperatureCurve=>"Curva por temperatura",_=>"Seguir ventiladores"};

    // Shared by the form and headless interaction checks. These commands only
    // edit the draft or navigate the panel; they cannot acquire hardware control.
    internal bool HandleFunctionEditorCommand(string id,Action<ProductProfiles> change)
    {
        if(id=="functions-base-mode")
        {
            if(!Busy&&!FunctionAdvanced&&SelectedFunction==ProductFunction.ExternalCooling)
                FunctionChooser=FunctionChooser==ProductFunctionChooser.ExternalMode?ProductFunctionChooser.None:ProductFunctionChooser.ExternalMode;
            Invalidate();return true;
        }
        if(id.StartsWith("functions-base-choice-",StringComparison.Ordinal))
        {
            if(!Busy&&!FunctionAdvanced&&SelectedFunction==ProductFunction.ExternalCooling&&FunctionChooser==ProductFunctionChooser.ExternalMode&&
                int.TryParse(id[22..],out var mode)&&Enum.IsDefined((ProductExternalOutputMode)mode))
            {change(ProductFunctionRuleEditing.SelectBaseMode(Profiles,(ProductExternalOutputMode)mode));CloseFunctionChooser();}
            Invalidate();return true;
        }
        var parts=id.Split('-');
        if(parts.Length<3||parts[0]!="function"||!int.TryParse(parts[1],out var number)||!Enum.IsDefined((ProductFunction)number))return false;
        var function=(ProductFunction)number;
        if(parts.Length==3&&parts[2]=="select")
        {if(!Busy)SelectedFunction=function;Invalidate();return true;}
        if(parts.Length==3&&parts[2]=="rule")
        {
            if(!Busy&&!FunctionAdvanced&&function==SelectedFunction)
                FunctionChooser=FunctionChooser==ProductFunctionChooser.Binding?ProductFunctionChooser.None:ProductFunctionChooser.Binding;
            Invalidate();return true;
        }
        if(parts.Length==4&&parts[2]=="choose")
        {
            if(!Busy&&!FunctionAdvanced&&function==SelectedFunction&&FunctionChooser==ProductFunctionChooser.Binding&&
                int.TryParse(parts[3],out var binding)&&Enum.IsDefined((ProductFunctionBinding)binding))
            {change(ProductFunctionRuleEditing.SelectBinding(Profiles,function,(ProductFunctionBinding)binding));CloseFunctionChooser();}
            Invalidate();return true;
        }
        return false;
    }
    internal Func<ProductFunctionPolicies>? SavedFunctionRules {get;set;}
    internal bool FunctionRulesUnapplied=>Profiles.FunctionPolicies()!=State.FunctionPolicies;
    internal bool FunctionRulesUnsaved=>SavedFunctionRules is not null&&Profiles.FunctionPolicies()!=SavedFunctionRules();
    internal bool FunctionRulesPending=>FunctionRulesUnapplied||FunctionRulesUnsaved;
    internal string FunctionRulesStatus=>FunctionRulesUnapplied?(FunctionRulesUnsaved?"Cambios sin aplicar ni guardar.":"Reglas guardadas; falta aplicarlas."):
        FunctionRulesUnsaved?"Reglas en uso; falta guardarlas.":"Reglas aplicadas y guardadas";
    internal static string FunctionRule(ProductFunctionPolicy p)=>p.Binding switch
    {
        ProductFunctionBinding.Independent=>"Independiente del modo de ventiladores",
        ProductFunctionBinding.StartWithAutomatic=>"Al activar Automático; mantener al desactivarlo",
        ProductFunctionBinding.FollowAutomatic=>"Solo mientras Automático esté activo",
        ProductFunctionBinding.StartWithInternalFans=>"Al activar Manual o Automático; mantener después",
        _=>"Solo mientras Manual o Automático esté activo"
    };
    internal static string FunctionWaitText(ProductRuntimeState s,ProductFunction f,string observed)
    {
        // A failed or active observation must never be hidden by a waiting label.
        if(!s.FunctionRequests.Get(f)||observed is not("Disabled" or "Unknown" or "Waiting" or "AcOnly"))return FunctionStatusText(observed);
        if(s.FunctionFaults.Get(f))return "Requiere revisión";
        if(s.PerformanceLifecycleBlocked)return "En espera del sistema";
        if(f==ProductFunction.Ctgp&&s.Source!="Ac")return s.Source=="Battery"?"En espera del cargador":"Fuente sin confirmar";
        var binding=s.FunctionPolicies.Get(f).Binding;
        if(binding==ProductFunctionBinding.FollowAutomatic&&(s.FanMode!="Automatic"||s.LifecycleBlocked))return "En espera de Automático";
        if(binding==ProductFunctionBinding.FollowInternalFans&&(s.FanMode=="Firmware"||s.LifecycleBlocked))return "En espera de ventiladores";
        return "Esperando sensores o controlador";
    }
    private void Functions(Graphics g)
    {
        DrawText(g,"Funciones",310,82,32,Ink,850,true);
        DrawText(g,"La solicitud actúa ahora. Las reglas futuras se aplican y guardan juntas.",310,130,20,Muted,1320);
        var policies=Profiles.FunctionPolicies();var selected=SelectedFunction;var index=(int)selected;
        var policy=policies.Get(selected);var wanted=State.FunctionRequests.Get(selected);
        var usable=!Busy&&State.Runtime is not("Failed" or "Stopped" or "Unavailable");
        foreach(var f in Enum.GetValues<ProductFunction>())
        {
            var y=180+(int)f*139;
            Button(g,"function-"+(int)f+"-select",FunctionName(f),new(310,y,400,48),f==selected);
            var observed=f switch{ProductFunction.CpuLimits=>State.CpuState,ProductFunction.GpuClocks=>State.GpuState,ProductFunction.Ctgp=>State.CtgpState,_=>ExternalFunctionStatus};
            DrawText(g,FunctionWaitText(State,f,observed),326,y+57,18,State.FunctionFaults.Get(f)?Yellow:Muted,365,height:45);
            DrawText(g,f==ProductFunction.CpuLimits?AppliedCpu():f==ProductFunction.GpuClocks?AppliedGpu():
                f==ProductFunction.Ctgp?"Requiere cargador HP compatible":State.FunctionCoordinationExplicit?"PC / dispositivo":"Seguimiento heredado",326,y+102,16,Muted,365,height:26);
        }
        if(!FunctionAdvanced){
        Card(g,new(736,180,904,540));
        DrawText(g,FunctionName(selected),762,197,27,Ink,840,true);
        var legacyBase=selected==ProductFunction.ExternalCooling&&!State.FunctionCoordinationExplicit;
        Button(g,$"function-{index}-on",selected==ProductFunction.ExternalCooling?"Controlar desde el PC":"Solicitar encendido",new(762,246,400,43),wanted,usable);
        Button(g,$"function-{index}-off",selected==ProductFunction.ExternalCooling?"Volver a control local":"Solicitar apagado",new(1180,246,434,43),!wanted&&!legacyBase,usable);
        DrawText(g,legacyBase?"Seguimiento heredado":wanted?"Encendido solicitado; la confirmación se muestra a la izquierda.":"Apagado solicitado",762,301,18,Muted,840);
        if(FunctionChooser==ProductFunctionChooser.ExternalMode&&selected==ProductFunction.ExternalCooling)
        {
            DrawText(g,"Modo de control de la base",762,345,23,Ink,840,true);
            var row=0;
            foreach(var mode in Enum.GetValues<ProductExternalOutputMode>())
                Button(g,$"functions-base-choice-{(int)mode}",(mode==policies.ExternalOutputMode?"✓ ":"○ ")+ExternalModeName(mode),
                    new(762,394+row++*58,852,46),mode==policies.ExternalOutputMode,!Busy);
            DrawText(g,"La selección modifica el borrador. Usa Aplicar y guardar reglas para ponerla en uso.",762,582,18,Muted,840,height:55);
            Button(g,"functions-base-mode","Volver sin cambiar",new(762,650,852,40),false,!Busy);
        }
        else {
        DrawText(g,"Reglas de encendido",762,345,23,Ink,840,true);
        Button(g,$"function-{index}-rule",FunctionChooser==ProductFunctionChooser.Binding?"Cerrar opciones de encendido  ▴":FunctionRule(policy)+"  ▾",new(762,387,852,44),FunctionChooser==ProductFunctionChooser.Binding,!Busy);
        if(FunctionChooser==ProductFunctionChooser.Binding)
        {
            var row=0;
            foreach(var binding in Enum.GetValues<ProductFunctionBinding>())
                Button(g,$"function-{index}-choose-{(int)binding}",(binding==policy.Binding?"✓ ":"○ ")+FunctionRule(new(binding,true)),
                    new(762,444+row++*48,852,42),binding==policy.Binding,!Busy);
            DrawText(g,"Solo cambia el borrador; después aplica y guarda las reglas.",762,685,16,Muted,840,height:25);
        }
        else {
        DrawText(g,"En uso: "+FunctionRule(State.FunctionPolicies.Get(selected)),762,444,17,Muted,840,height:38);
        Button(g,$"function-{index}-startup",(policy.StartWithApplication?"✓ ":"○ ")+"Solicitar al abrir el programa",new(762,489,852,40),policy.StartWithApplication,!Busy);
        if(selected==ProductFunction.ExternalCooling)
        {
            Button(g,"functions-base-mode","Control: "+ExternalModeName(policies.ExternalOutputMode)+"  ▾",new(762,549,570,40));
            Button(g,"functions-base-output",$"Fija: {policies.ExternalFixedOutput*100.0/255:0} %",new(1348,549,266,40),false,!Busy&&policies.ExternalOutputMode==ProductExternalOutputMode.Fixed);
            DrawText(g,"Control local no significa parada. Una salida fija de 0 % solicita detener la base.",762,608,18,Muted,840,height:64);
        }
        else DrawText(g,policy.Binding==ProductFunctionBinding.Independent?"Cambiar el modo de ventiladores no vuelve a solicitar esta función.":
            !policy.SelectedForLinkedStart?"Personalizado: no se enciende al entrar en el modo. Avanzado conserva tu selección.":
            policy.Binding is ProductFunctionBinding.FollowAutomatic or ProductFunctionBinding.FollowInternalFans?
            "Fuera del modo elegido, la solicitud queda en espera.":"La siguiente entrada en ese modo vuelve a solicitar encendido.",762,550,19,Muted,840,height:85);
        }
        }
        }
        var pending=FunctionRulesPending;
        DrawText(g,FunctionRulesStatus,310,754,19,pending?Yellow:Muted,970,height:40);
        Button(g,"functions-advanced",FunctionAdvanced?"Cerrar Avanzado":"Avanzado: fallos y compatibilidad",new(310,802,660,40),FunctionAdvanced);
        Button(g,"functions-apply-save","Aplicar y guardar reglas",new(1040,802,600,40),true,!Busy);
        if(FunctionAdvanced)
        {
            Card(g,new(736,180,904,540));DrawText(g,"Avanzado · conserva las opciones existentes",762,200,25,Ink,840,true);
            Button(g,"functions-performance-fault",(policies.StopAutomaticOnPerformanceFailure?"✓ ":"○ ")+"Fallo CPU o GPU interrumpe Automático",new(762,254,852,42),policies.StopAutomaticOnPerformanceFailure);
            Button(g,"functions-ctgp-fault",(policies.StopAutomaticOnCtgpFailure?"✓ ":"○ ")+"Fallo cTGP interrumpe Automático",new(762,311,852,42),policies.StopAutomaticOnCtgpFailure);
            Button(g,$"function-{index}-linked",(policy.SelectedForLinkedStart?"✓ ":"○ ")+FunctionName(selected)+": encender al entrar en su modo",new(762,376,852,42),policy.SelectedForLinkedStart);
            Button(g,"functions-compatible","Comportamiento anterior",new(762,449,410,42));
            Button(g,"functions-independent","Con Automático y mantener",new(1190,449,424,42));
            Button(g,"functions-unlinked","Sin encendidos vinculados",new(762,507,852,42));
            DrawText(g,"Los presets cambian reglas, política de fallos y modo de la base en el borrador. Revisa antes de aplicar. Las protecciones térmicas permanecen activas.",762,579,18,Muted,840,height:98);
        }
    }
    internal string ExternalFunctionStatus {get;set;}="Sin conexión";
}
