using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private void RetentionPanel(Graphics g)
    {
        DrawText(g,"Retención TZ01 / DTT3 · experimental",342,275,27,null,750,true);
        Button(g,"retention-toggle",Profiles.ExperimentalPlatformRetention?"✓  Preparar retención en la próxima sesión AC":"○  Retención desactivada",new(342,330,751,53),Profiles.ExperimentalPlatformRetention);
        DrawText(g,"Máximo: +2 niveles de demanda durante 60 s. Nunca supera el último nivel aplicado ni sustituye CPU/GPU. Un único filtro conserva la inercia.",342,406,21,Muted,750,height:92);
        var r=State.PlatformRetention;
        DrawText(g,"Observador: "+(r?.Status??"Sin observador"),342,515,22,r?.Ready==true?Green:Muted,750);
        DrawText(g,$"TZ01: {r?.Tz01.Value?.ToString("0.0")??"—"} °C · DTT3: {r?.Dtt3.Value?.ToString("0.0")??"—"} °C",342,563,22,null,750);
        DrawText(g,$"Demanda auxiliar: {r?.SourceDemand?.ToString("0.0")??"—"} · demanda acotada: {r?.SupplementalDemand?.ToString("0.0")??"—"}",342,607,21,Muted,750);
        DrawText(g,"Son fuentes ACPI/DTT; no se les asigna una ubicación física no comprobada. Vigencia <3 s y dos adquisiciones distintas durante ≥1 s.",342,654,20,Muted,750,height:77);
        Button(g,"retention-quiet-candidate","Restaurar curva predeterminada AC v1.0",new(342,761,751,48));
        DrawText(g,"Prueba explícita desde Firmware",1178,275,26,null,438,true);
        DrawText(g,"Editar prepara el borrador. Guarda y activa Automatic desde Firmware con AC en el programa. Cambiar la opción no altera una sesión activa.",1178,342,21,Muted,438,height:150);
        DrawText(g,"La retención puede prolongar el ruido. La candidata reduce la demanda intermedia y acelera el descenso; temperatura comparable y menos ruido siguen pendientes de medir.",1178,518,21,Yellow,438,height:148);
        DrawText(g,"Al perder frescura, continuidad o AC, se interrumpe la sesión y se solicita Firmware. No se rearma sola.",1178,686,20,Muted,438,height:83);
        Button(g,"save","Guardar configuración",new(1178,777,438,40));
    }
}

internal sealed partial class ProductForm
{
    private ProductProfiles? _quietCandidateDraft;
    private bool HandleRetentionCommand(string id)
    {
        if(!id.StartsWith("retention-"))return false;
        if(_canvas.Busy||_closing)return true;
        if(id=="retention-toggle")
        {
            Change(_draft with{ExperimentalPlatformRetention=!_draft.ExperimentalPlatformRetention});
            _canvas.Notice="Opción preparada para la próxima activación desde Firmware. Guardar conserva la preferencia.";
        }
        else if(id=="retention-quiet-candidate")
        {
            if(_quietCandidateDraft is not null && _quietCandidateDraft.Ac.Fan==_draft.Ac.Fan) { _canvas.Notice="La candidata ya está preparada; no se acumula otra reducción.";_canvas.Invalidate();return true; }
            Change(_draft with{Ac=_draft.Ac with{Fan=ProductProfiles.DefaultProfile(ProductPowerProfile.Ac).Fan}});
            _quietCandidateDraft=_draft;
            _canvas.Notice="Candidata AC preparada; conserva tus límites CPU/GPU y Batería. Guarda y activa desde Firmware. Menos ruido aún requiere medición.";
        }
        _canvas.Invalidate();return true;
    }
}
