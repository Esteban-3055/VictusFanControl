using VictusFanControl.Product;

namespace VictusFanControl.App;

internal sealed partial class ProductCanvas
{
    private void Updates(Graphics g)
    {
        DrawText(g,"Actualizaciones",320,88,31,null,1300,true);
        Card(g,new(310,153,1337,176));
        DrawText(g,"Versión instalada",334,177,24,Muted,550);
        DrawText(g,"v"+ProductRelease.DisplayVersion,334,218,39,Accent,550,true);
        DrawText(g,Profiles.IncludePrereleaseUpdates?"Estables + preliminares · GitHub":"Canal estable · GitHub",900,178,25,null,725,true);
        DrawText(g,ProductUpdates.Repository,900,218,20,Muted,725);
        Button(g,"updates-prerelease-toggle",Profiles.IncludePrereleaseUpdates?"✓  Incluir versiones preliminares":"○  Incluir versiones preliminares",new(900,265,716,43),Profiles.IncludePrereleaseUpdates,!UpdateBusy);
        Card(g,new(310,349,1337,299));
        DrawText(g,AvailableUpdate is { } update?"Disponible: v"+update.Version+(update.IsPrerelease?" · Preliminar":" · Estable"):"Comprobador de actualizaciones",334,373,28,null,1250,true);
        DrawText(g,UpdateStatus,334,427,23,UpdateBusy?Yellow:AvailableUpdate is null?Muted:Green,1260,height:75);
        DrawText(g,UpdateCheckedAt is { } at?"Última comprobación: "+at.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"):"Todavía no se ha consultado GitHub.",334,511,19,Muted,1250);
        if(UpdateProgressPercent is { } progress)Bar(g,new(334,549,1280,10),progress,100,Accent);
        Button(g,"updates-check",UpdateBusy?"Operación en curso…":"Buscar actualizaciones",new(334,579,400,48),false,!UpdateBusy);
        Button(g,"updates-download",AvailableUpdate is { } ready&&DownloadedUpdate==ready?"Descarga guardada":"Descargar",new(750,579,390,48),false,!UpdateBusy&&AvailableUpdate is { } available&&(!available.IsPrerelease||Profiles.IncludePrereleaseUpdates)&&DownloadedUpdate!=available);
        Button(g,"updates-install",DownloadedUpdate is { } cached?"Instalar v"+cached.Version:"Instalar",new(1156,579,460,48),true,!UpdateBusy&&DownloadedUpdate is { } downloaded&&(!downloaded.IsPrerelease||Profiles.IncludePrereleaseUpdates)&&!Dirty);
        Card(g,new(310,668,1337,155));
        if(State.PerformanceRecovery is {Pending:true} recovery)
            Button(g,"recovery-run","Recuperar sesiones",new(1120,689,496,43),true,!UpdateBusy&&recovery.Actionable);
        DrawText(g,"La descarga se conserva hasta que puedas instalar",334,689,24,null,780,true);
        DrawText(g,Dirty?"Guarda o descarta los cambios antes de instalar la actualización.":State.PerformanceRecovery is {Pending:true}?"Recupera la sesión anterior y vuelve a pulsar Instalar. El archivo descargado se conserva.":"Puedes descargar mientras usas el programa. Instalar comprobará la release de GitHub y el archivo guardado, y liberará ventiladores y límites CPU/GPU antes de cerrar.",334,738,22,Dirty?Yellow:Muted,1260,height:70);
    }
}
