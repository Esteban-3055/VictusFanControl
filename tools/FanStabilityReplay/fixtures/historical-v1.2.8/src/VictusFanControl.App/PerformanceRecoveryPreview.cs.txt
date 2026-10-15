using VictusFanControl.Performance;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

/// <summary>Read-only journal preflight. Never restores hardware, deletes records or invents missing session IDs.</summary>
internal sealed record PerformanceRecoveryPreview(bool Pending, string Detail, Guid? CpuSession=null, Guid? GpuSession=null, IReadOnlyList<ProductRecoveryRecord>? FanRecords=null)
{
    internal bool Actionable => CpuSession.HasValue || GpuSession.HasValue || FanRecords?.Count > 0;
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VictusFanControl","Performance",CpuPowerProductDefaults.TargetProfileId);
    internal static PerformanceRecoveryPreview Read(string? directory=null)
    {
        var includeFans=directory is null;
        directory??=DirectoryPath;
        var cpuPath=Path.Combine(directory,"cpu-power-session.json"); var gpuPath=Path.Combine(directory,"gpu-clock-session.json");
        try
        {
            var fans=includeFans?ProductRecoveryInventory.ReadFans(ignoreCurrentOwner:true):null;
            if(!ProductRecoveryInventory.Exists(cpuPath)&&!ProductRecoveryInventory.Exists(gpuPath)&&fans?.Count is not >0)return new(false,"Sin registros pendientes.");
            var cpu=new JsonCpuPowerSessionJournal(cpuPath,CpuPowerProductDefaults.TargetProfileId).Load();
            var gpu=new JsonGpuClockSessionJournal(gpuPath,CpuPowerProductDefaults.TargetProfileId).Load();
            return new(true,"Hay sesiones pendientes. Usa Recuperar sesiones en Rendimiento → Guardián o Configuración; se guardan respaldos antes de liberar.",cpu?.SessionId,gpu?.SessionId,fans);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return new(true,"Registro de recuperación ilegible o inválido. Conserva los archivos y exporta el diagnóstico. "+ex.Message); }
    }
    internal string Instructions()
    {
        var text=Detail+$"\n\nCPU: {CpuSession?.ToString()??"sin identificador válido"}\nGPU: {GpuSession?.ToString()??"sin identificador válido"}";
        if(FanRecords?.Count>0) return text+"\n\n"+string.Join("\n",FanRecords.Select(x=>x.Path+": "+(x.Problem??x.Kind)))+
            "\n\nUsa Recuperar sesiones. Sólo se libera a Firmware y se conservan respaldos. Si una llamada nativa quedó incierta, usa Reiniciar Windows y vuelve a recuperar. Un archivo dañado o de otro equipo se conserva para diagnóstico; no se inventa propiedad ni se activa Automático.";
        if(CpuSession is null && GpuSession is null || CpuSession == Guid.Empty || GpuSession == Guid.Empty)
            return text+"\n\nNo se genera un comando con registros inválidos. Conserva los registros y el diagnóstico para resolver la recuperación.";
        var cpu=CpuSession??Guid.Empty;var gpu=GpuSession??Guid.Empty;
        string? script=null;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var path=Path.Combine(directory.FullName,"Start-ProductGui.ps1");
            if(File.Exists(path)){script=path;break;}
        }
        var launcher=script is null ? ".\\Start-ProductGui.ps1" : "& '"+script.Replace("'","''")+"'";
        return text+"\n\nPuedes usar Recuperar sesiones desde esta ventana. La alternativa manual requiere cerrar VictusFanControl y los demás controladores normalmente. En PowerShell como administrador, desde la carpeta del paquete:\n\n"+
            launcher+" -Mode RecoverPerformance -ExpectedCpuSession '"+cpu+"' -ExpectedGpuSession '"+gpu+"' -ConfirmExclusiveGpuController"+
            "\n\nCPU restaura solo campos aún propios; GPU solicita un Reset por NVML, sin lectura independiente exacta del rango. Guarda el directorio de evidencia. Después abre una sesión nueva en Firmware.";
    }
}
