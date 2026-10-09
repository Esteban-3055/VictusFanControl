using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Performance;
using VictusFanControl.Product;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

/// <summary>Only preferences cross the process boundary. Hardware authority never does.</summary>
internal sealed record ProductRestartState
{
    public int SchemaVersion { get; init; } = 1;
    public bool ReleaseCompleted { get; init; }
    public string PreviousSessionId { get; init; } = "";
    public Guid ApplicationMvid { get; init; }
    public Guid CoreMvid { get; init; }
    public string DraftJson { get; init; } = "";
    public string SavedJson { get; init; } = "";
    public bool HasSavedBaseline { get; init; }
    public bool Dirty { get; init; }
    public ProductPage Page { get; init; }
    public ProductPowerProfile Editing { get; init; }
}

internal sealed record ProductRestartRequest(string StatePath,string Modules,ProductAutomaticReviewMode? Review);

internal static class ProductSessionRestart
{
    internal const string StateFileName = "restart-state.json";
    internal static ProductRestartState Capture(ProductProfiles draft,ProductProfiles saved,bool baseline,bool dirty,ProductPage page,ProductPowerProfile editing) => new()
    {
        PreviousSessionId=AppLog.SessionId,ApplicationMvid=typeof(ProductSessionRestart).Assembly.ManifestModule.ModuleVersionId,
        CoreMvid=typeof(ProductProfiles).Assembly.ManifestModule.ModuleVersionId,
        DraftJson=ProductProfilesStore.Serialize(draft),SavedJson=ProductProfilesStore.Serialize(saved),
        HasSavedBaseline=baseline,Dirty=dirty,Page=page,Editing=editing
    };

    internal static void WriteReleasedState(string path,ProductRestartState state)
    {
        // This receipt is created only after all domain cleanup and recovery-record checks succeeded.
        using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(file,state with{ReleaseCompleted=true});file.Flush(flushToDisk:true);
    }

    internal static ProductRestartState ReadReleasedState(string path,string? sessionsRoot=null)
    {
        var full=Path.GetFullPath(path);var root=Path.GetFullPath(sessionsRoot??Path.Combine(AppLog.LogDirectory,"sessions"));
        var relative=Path.GetRelativePath(root,full).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        if(relative.Length!=2||relative[0] is "." or ".."||relative[1]!=StateFileName||new FileInfo(full).Length>1024*1024)
            throw new InvalidDataException("Estado de reinicio fuera de la carpeta de sesiones o demasiado grande.");
        var state=JsonSerializer.Deserialize<ProductRestartState>(File.ReadAllText(full))??throw new InvalidDataException("Estado de reinicio vacío.");
        if(state.SchemaVersion!=1||!state.ReleaseCompleted||state.PreviousSessionId!=relative[0]||
            state.ApplicationMvid!=typeof(ProductSessionRestart).Assembly.ManifestModule.ModuleVersionId||
            state.CoreMvid!=typeof(ProductProfiles).Assembly.ManifestModule.ModuleVersionId||
            !Enum.IsDefined(state.Page)||!Enum.IsDefined(state.Editing))
            throw new InvalidDataException("El reinicio no acredita una liberación completa de esta compilación.");
        ProductProfilesStore.Parse(state.DraftJson).Validate();ProductProfilesStore.Parse(state.SavedJson).Validate();
        return state;
    }

    internal static void EnsureNoRecoveryRecords()
    {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VictusFanControl","Performance",CpuPowerProductDefaults.TargetProfileId);
        EnsureAbsent(new[]{Path.Combine(directory,"cpu-power-session.json"),Path.Combine(directory,"gpu-clock-session.json"),
            WmiFanGuiGuardianHost.LeasePath,WmiFanGuiGuardianHost.LegacyLeasePath,WmiFanExperiment.LeasePath});
    }
    internal static void EnsureAbsent(IEnumerable<string> paths)
    {
        foreach(var path in paths)
        {
            try{_ = File.GetAttributes(path);}
            catch(FileNotFoundException){continue;}
            catch(DirectoryNotFoundException){continue;}
            throw new IOException("Reinicio bloqueado: hay una recuperación pendiente en "+path+". El registro se conserva.");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ProductRestartRequest request,string processPath,string assemblyPath)
    {
        var start=new ProcessStartInfo(processPath){UseShellExecute=false,WorkingDirectory=AppContext.BaseDirectory};
        if(Path.GetFileNameWithoutExtension(processPath).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add("--modules-dir");start.ArgumentList.Add(request.Modules);
        start.ArgumentList.Add("--product-restart-state");start.ArgumentList.Add(request.StatePath);
        if(request.Review is ProductAutomaticReviewMode.Short)start.ArgumentList.Add("--product-automatic-review");
        else if(request.Review is ProductAutomaticReviewMode.Extended)start.ArgumentList.Add("--product-automatic-extended-review");
        else if(request.Review.HasValue)throw new ArgumentOutOfRangeException(nameof(request));
        // Never copy arbitrary CLI flags, minimized startup or an Automatic/Performance selection.
        return start;
    }
    internal static void Launch(ProductRestartRequest request)
    {
        ReadReleasedState(request.StatePath);EnsureNoRecoveryRecords();
        var start=CreateStartInfo(request,Environment.ProcessPath??throw new IOException("Ruta del ejecutable no disponible."),typeof(ProductSessionRestart).Assembly.Location);
        using var child=Process.Start(start)??throw new IOException("No se pudo abrir la nueva sesión.");
        AppLog.Write("PRODUCT SESSION RESTART LAUNCHED: "+child.Id+"; state="+request.StatePath+"; startup=Firmware; no hardware activation.");
    }
}
