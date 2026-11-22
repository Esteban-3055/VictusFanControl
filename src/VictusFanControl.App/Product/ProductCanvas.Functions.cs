using VictusFanControl.Product;

namespace VictusFanControl.App;

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
    private void Functions(Graphics g)
    {
        DrawText(g,"Funciones",310,85,32,Ink,750,true);
        DrawText(g,"Encender y apagar actúa ahora. Los vínculos se editan, se aplican y se guardan por separado.",310,132,20,Muted,1320);
        var policies=Profiles.FunctionPolicies();
        Button(g,"functions-base-mode","Base: "+(policies.ExternalOutputMode switch{ProductExternalOutputMode.Fixed=>"Salida fija",ProductExternalOutputMode.TemperatureCurve=>"Curva por temperatura",_=>"Seguir nivel interno"}),new(992,78,440,42));
        Button(g,"functions-base-output","Fija: "+policies.ExternalFixedOutput+" / 255",new(1444,78,196,42),false,!Busy&&policies.ExternalOutputMode==ProductExternalOutputMode.Fixed);
        foreach(var function in Enum.GetValues<ProductFunction>())
        {
            var index=(int)function;var y=172+index*128;var policy=policies.Get(function);var wanted=State.FunctionRequests.Get(function);
            var legacyBase=function==ProductFunction.ExternalCooling&&!State.FunctionCoordinationExplicit;
            Card(g,new(310,y,1330,116));
            DrawText(g,FunctionName(function),332,y+12,25,Ink,275,true);
            var actual=function switch{ProductFunction.CpuLimits=>State.CpuState,ProductFunction.GpuClocks=>State.GpuState,
                ProductFunction.Ctgp=>State.CtgpState,_=>ExternalFunctionStatus};
            var shown=FunctionStatusText(actual);
            DrawText(g,legacyBase?"Seguimiento heredado":wanted?"Encendido solicitado":"Apagado solicitado",332,y+50,18,Muted,280,height:27);
            DrawText(g,shown+(State.FunctionFaults.Get(function)?" · revisar":""),332,y+80,18,State.FunctionFaults.Get(function)?Yellow:Muted,280,height:29);
            Button(g,$"function-{index}-on","Encender",new(622,y+13,145,39),wanted,!Busy);
            Button(g,$"function-{index}-off","Apagar",new(779,y+13,135,39),!wanted&&!legacyBase,!Busy);
            Button(g,$"function-{index}-binding",BindingName(policy.Binding),new(936,y+13,675,39),false,!Busy);
            Button(g,$"function-{index}-linked",(policy.SelectedForLinkedStart?"✓ ":"○ ")+"Seleccionar para inicio vinculado",new(622,y+65,468,36),policy.SelectedForLinkedStart,!Busy);
            Button(g,$"function-{index}-startup",(policy.StartWithApplication?"✓ ":"○ ")+"Iniciar con la aplicación",new(1108,y+65,503,36),policy.StartWithApplication,!Busy);
        }
        Button(g,"functions-compatible","Comportamiento anterior",new(310,700,410,40),false,!Busy);
        Button(g,"functions-independent","Independientes",new(734,700,285,40),false,!Busy);
        Button(g,"functions-unlinked","Sin vínculos",new(1033,700,270,40),false,!Busy);
        Button(g,"functions-apply","Aplicar vínculos",new(1317,700,323,40),true,!Busy);
        Button(g,"functions-performance-fault",(policies.StopAutomaticOnPerformanceFailure?"✓ ":"○ ")+"Fallo CPU/GPU detiene Automático",new(310,753,654,36),policies.StopAutomaticOnPerformanceFailure,!Busy);
        Button(g,"functions-ctgp-fault",(policies.StopAutomaticOnCtgpFailure?"✓ ":"○ ")+"Fallo cTGP detiene Automático",new(980,753,660,36),policies.StopAutomaticOnCtgpFailure,!Busy);
        DrawText(g,"Seguir también detiene la función al salir del modo. Iniciar con solo la enciende y después la conserva.",310,807,19,Muted,1010);
        Button(g,"save","Guardar preferencias",new(1330,800,310,40),true,!Busy);
    }
    internal string ExternalFunctionStatus {get;set;}="Sin conexión";
}
