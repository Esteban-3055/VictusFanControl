using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private void Updates(Graphics g)
    {
        DrawText(g,"Actualizaciones",320,88,31,null,1300,true);
        Card(g,new(310,153,1337,176));
        DrawText(g,"Versión instalada",334,177,24,Muted,550);
        DrawText(g,"v"+ProductRelease.Version,334,218,39,Blue,550,true);
        DrawText(g,Profiles.IncludePrereleaseUpdates?"Estables + preliminares · GitHub":"Canal estable · GitHub",900,178,25,null,725,true);
        DrawText(g,ProductUpdates.Repository,900,218,20,Muted,725);
        Button(g,"updates-prerelease-toggle",Profiles.IncludePrereleaseUpdates?"✓  Incluir versiones preliminares":"○  Incluir versiones preliminares",new(900,265,716,43),Profiles.IncludePrereleaseUpdates,!UpdateBusy);
        Card(g,new(310,349,1337,299));
        DrawText(g,AvailableUpdate is { } update?"Disponible: v"+update.Version+(update.IsPrerelease?" · Preliminar":" · Estable"):"Comprobador de actualizaciones",334,373,28,null,1250,true);
        DrawText(g,UpdateStatus,334,427,23,UpdateBusy?Yellow:AvailableUpdate is null?Muted:Green,1260,height:75);
        DrawText(g,UpdateCheckedAt is { } at?"Última comprobación: "+at.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"):"Todavía no se ha consultado GitHub.",334,511,19,Muted,1250);
        if(UpdateProgressPercent is { } progress)Bar(g,new(334,549,1280,10),progress,100,Blue);
        Button(g,"updates-check",UpdateBusy?"Operación en curso…":"Buscar actualizaciones",new(334,579,470,48),false,!UpdateBusy);
        Button(g,"updates-install","Descargar e instalar",new(846,579,770,48),true,!UpdateBusy&&AvailableUpdate is { } available&&(!available.IsPrerelease||Profiles.IncludePrereleaseUpdates)&&!Dirty);
        Card(g,new(310,668,1337,155));
        DrawText(g,"Tus perfiles y preferencias se conservan",334,689,26,null,1250,true);
        DrawText(g,Dirty?"Guarda o descarta los cambios antes de instalar la actualización.":"Al instalar, se verificará la descarga y se liberarán ventiladores y límites CPU/GPU antes de cerrar. Windows solicitará permiso para el instalador.",334,738,22,Dirty?Yellow:Muted,1260,height:70);
    }
}
