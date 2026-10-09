using VictusFanControl.Performance;

namespace VictusFanControl.App;

/// <summary>Read-only journal preflight. Never restores hardware, deletes records or invents missing session IDs.</summary>
internal sealed record PerformanceRecoveryPreview(bool Pending, string Detail, Guid? CpuSession=null, Guid? GpuSession=null)
{
    internal static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VictusFanControl","Performance",CpuPowerProductDefaults.TargetProfileId);
    internal static PerformanceRecoveryPreview Read(string? directory=null)
    {
        directory??=DirectoryPath;
        var cpuPath=Path.Combine(directory,"cpu-power-session.json"); var gpuPath=Path.Combine(directory,"gpu-clock-session.json");
        if(!File.Exists(cpuPath)&&!File.Exists(gpuPath))return new(false,"Sin registros pendientes.");
        try
        {
            var cpu=new JsonCpuPowerSessionJournal(cpuPath,CpuPowerProductDefaults.TargetProfileId).Load();
            var gpu=new JsonGpuClockSessionJournal(gpuPath,CpuPowerProductDefaults.TargetProfileId).Load();
            return new(true,"Hay registros CPU/GPU pendientes de recuperación. No se inicia otra sesión ni se sobrescriben. Usa Ver recuperación en Rendimiento → Guardián.",cpu?.SessionId,gpu?.SessionId);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        { return new(true,"Registro de recuperación ilegible o inválido. Conserva los archivos y exporta el diagnóstico. "+ex.Message); }
    }
    internal string Instructions()
    {
        var text=Detail+$"\n\nCPU: {CpuSession?.ToString()??"sin identificador válido"}\nGPU: {GpuSession?.ToString()??"sin identificador válido"}";
        if(CpuSession is not {} cpu || cpu==Guid.Empty || GpuSession is not {} gpu || gpu==Guid.Empty)
            return text+"\n\nNo se genera un comando con identificadores incompletos. Conserva los registros y el diagnóstico para resolver la recuperación.";
        string? script=null;
        for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
        {
            var path=Path.Combine(directory.FullName,"Start-ProductGui.ps1");
            if(File.Exists(path)){script=path;break;}
        }
        var launcher=script is null ? ".\\Start-ProductGui.ps1" : "& '"+script.Replace("'","''")+"'";
        return text+"\n\nCierra VictusFanControl y los demás controladores normalmente. En PowerShell como administrador, desde la carpeta del paquete:\n\n"+
            launcher+" -Mode RecoverPerformance -ExpectedCpuSession '"+cpu+"' -ExpectedGpuSession '"+gpu+"' -ConfirmExclusiveGpuController"+
            "\n\nCPU restaura solo campos aún propios; GPU solicita un Reset por NVML, sin lectura independiente exacta del rango. Guarda el directorio de evidencia. Después abre una sesión nueva en Firmware.";
    }
}
