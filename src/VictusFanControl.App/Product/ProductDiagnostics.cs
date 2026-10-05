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
        var summary=new{kind="VictusFanControl.GuiDiagnostic",capturedUtc=DateTimeOffset.UtcNow,version=typeof(ProductDiagnostics).Assembly.GetName().Version?.ToString(),
            state.Hardware,state.Target,state.Source,state.Runtime,state.FanMode,state.FanAuthority,state.FanLevel,state.LifecycleBlocked,state.LifecycleBlockReason,
            state.AutomaticReview,state.AutomaticReviewRemainingSeconds,state.AutomaticDecision,state.AppliedAutomaticConfiguration,state.AppliedFanProfile,
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
                    Write("gui-state.json",JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true}));
                    Write("profiles-draft.json",ProductProfilesStore.Serialize(draft));
                    Write("README.txt","Estado observado y preferencias en edición. No es prueba de ownership, reset ni cualificación física. Solo configuración; importar no aplica hardware. Journals y leases originales no se alteran. El log puede contener rutas locales; revisar antes de compartir.");
                    if(logPath is not null&&File.Exists(logPath))
                    {
                        try
                        {
                            using var log=new FileStream(logPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
                            var size=(int)Math.Min(log.Length,2*1024*1024);log.Seek(-size,SeekOrigin.End);var bytes=new byte[size];var read=0;
                            while(read<size){var chunk=log.Read(bytes,read,size-read);if(chunk==0)break;read+=chunk;}
                            Write("events-tail.log",System.Text.Encoding.UTF8.GetString(bytes,0,read));
                        }
                        catch(IOException ex){Write("log-unavailable.txt",ex.Message);}
                        catch(UnauthorizedAccessException ex){Write("log-unavailable.txt",ex.Message);}
                    }
                }
                stream.Flush(flushToDisk:true);
            }
            File.Move(temporary,full,overwrite:true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
