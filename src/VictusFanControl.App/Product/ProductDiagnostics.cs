using System.IO.Compression;
using System.Text.Json;
using VictusFanControl.Product;

namespace VictusFanControl.App;

/// <summary>Read-only diagnostic export. No recovery journal/lease enumeration or process control.</summary>
internal static class ProductDiagnostics
{
    internal static void Export(string path,ProductRuntimeState state,ProductProfiles draft,string? logPath)
    {
        var full=Path.GetFullPath(path);var temporary=full+"."+Guid.NewGuid().ToString("N")+".tmp";
        var maximumLogBytes=state.AutomaticReviewMaximumSeconds == ProductAutomaticReview.ExtendedMaximumSeconds
            ? AppLog.ExtendedReviewTailBytes : AppLog.DefaultTailBytes;
        var summary=new{diagnosticMaximumBytesPerStream=maximumLogBytes,kind="VictusFanControl.GuiDiagnostic",capturedUtc=DateTimeOffset.UtcNow,version=typeof(ProductDiagnostics).Assembly.GetName().Version?.ToString(),sessionId=AppLog.SessionId,sessionStartedUtc=AppLog.SessionStartedUtc,
            state.Hardware,state.Target,state.Source,state.Runtime,state.FanMode,state.FanAuthority,state.FanLevel,state.LifecycleBlocked,state.LifecycleBlockReason,
            state.AutomaticReview,state.AutomaticReviewMaximumSeconds,state.AutomaticReviewRemainingSeconds,state.AutomaticCpuSpikeRemainingMilliseconds,state.AutomaticDecision,state.AppliedAutomaticConfiguration,state.AppliedFanProfile,
            state.AutomaticPreparing,state.AutomaticSessionId,state.AutomaticDecisionSnapshot,state.AutomaticInterruptionSnapshot,state.PerformanceActive,state.PerformanceProcessPresent,state.PerformanceUpdating,state.AppliedPerformance,
            state.CpuState,state.GpuState,state.CpuStatus,state.GpuStatus,state.GuardianState,state.AppliedPerformanceSource,state.Message,state.Failure,
            snapshot=state.Snapshot,physicalQualification="not-established-by-this-export"};
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,leaveOpen:true))
                {
                    void Write(string name,string text){using var writer=new StreamWriter(zip.CreateEntry(name).Open());writer.Write(text);}
                    // Keep the triggering evidence exportable even when the rejected sample contains NaN/Infinity.
                    Write("gui-state.json",JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true,
                        NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
                    Write("session.json",JsonSerializer.Serialize(AppLog.SessionIdentity,new JsonSerializerOptions{WriteIndented=true}));
                    Write("profiles-draft.json",ProductProfilesStore.Serialize(draft));
                    Write("README.txt","Estado observado y preferencias en edición. No es prueba de ownership, reset ni cualificación física. Solo configuración; importar no aplica hardware. Journals y leases originales no se alteran. Los registros pertenecen solo a esta apertura de la aplicación; eventos y telemetría contienen hasta los últimos "+(maximumLogBytes/(1024*1024))+" MiB de cada flujo. Las activaciones Automatic tienen su propio identificador. El log puede contener rutas locales; revisar antes de compartir.");
                    void IncludeLog(string entry,string source)
                    {
                        if(!File.Exists(source)&&!File.Exists(source+".1"))return;
                        try{Write(entry,AppLog.ReadTail(source,maximumLogBytes));}
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Write(entry+"-unavailable.txt",ex.Message);}
                    }
                    IncludeLog("events-tail.log",logPath??AppLog.CurrentLogPath);
                    IncludeLog("telemetry-tail.jsonl",AppLog.TelemetryLogPath);
                }
                stream.Flush(flushToDisk:true);
            }
            File.Move(temporary,full,overwrite:true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
