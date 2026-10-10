using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace VictusFanControl.Recovery;

internal sealed record ProductRecoveryRecord(string Path, string Kind, string Sha256, string? Problem = null)
{
    internal bool Recoverable => Problem is null && Kind is "FanGui" or "FanExperiment" or "LegacyFan" or "OrphanWmi";
}

/// <summary>Discover retained authority, including older locations. Unknown records are never treated as absent.</summary>
internal static class ProductRecoveryInventory
{
    internal const string Target = "HP-8C40-9D0R1LA-F18";
    internal static string CommonRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VictusFanControl");
    internal static string LocalRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl");
    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    internal static void EnsurePlainPath(string path)
    {
        for (var p = new DirectoryInfo(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!); p is not null; p = p.Parent)
            if (p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Ruta de recuperación redirigida: " + p.FullName);
        if (Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Registro de recuperación redirigido: " + path);
    }
    internal static byte[] ReadBytes(string path)
    {
        EnsurePlainPath(path);
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length is <= 0 or > 1024 * 1024) throw new InvalidDataException("Registro vacío o demasiado grande: " + path);
        var bytes = new byte[checked((int)f.Length)]; f.ReadExactly(bytes); return bytes;
    }
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static ProductRecoveryRecord Inspect(string path, string commonRoot)
    {
        byte[] bytes;
        try { bytes = ReadBytes(path); }
        catch (Exception ex) { return new(path, "Unknown", "", ex.Message); }
        var hash = Hash(bytes);
        try
        {
            using var document = JsonDocument.Parse(bytes); var r = document.RootElement;
            var relative = System.IO.Path.GetRelativePath(commonRoot, path).Replace('\\', '/');
            if (relative == "WmiFanGui/lease.json")
            {
                if (r.GetProperty("OwnerPid").GetInt32() <= 0 || r.GetProperty("OwnerStartUtcTicks").GetInt64() <= 0 ||
                    r.GetProperty("GuardianPid").GetInt32() <= 0 || !r.GetProperty("DirectEcProhibited").GetBoolean() ||
                    string.IsNullOrWhiteSpace(r.GetProperty("SessionDirectory").GetString())) throw new InvalidDataException("Identidad WMI GUI inválida.");
                return new(path, "FanGui", hash);
            }
            if (relative == "WmiFanExperiment/lease.json")
            {
                if (r.GetProperty("Pid").GetInt32() <= 0 || string.IsNullOrWhiteSpace(r.GetProperty("Directory").GetString()) ||
                    r.GetProperty("Utc").GetDateTimeOffset() == default || r.GetProperty("FirmwareRestorationVerified").GetBoolean())
                    throw new InvalidDataException("Identidad de ensayo WMI inválida.");
                _ = r.GetProperty("Control").GetBoolean(); return new(path, "FanExperiment", hash);
            }
            if (relative is "WatchdogM4/state/lease.json" or "Watchdog/state/lease.json" && r.TryGetProperty("SchemaVersion", out var schema) && schema.GetInt32() == 2 &&
                r.GetProperty("TargetProfileId").GetString() == Target && r.GetProperty("SessionId").GetGuid() != Guid.Empty)
                return new(path, "LegacyFan", hash);
            throw new InvalidDataException("Registro antiguo, de otro equipo o sin contrato reconocido. No se autoriza escribir hardware.");
        }
        catch (Exception ex) { return new(path, "Unknown", hash, ex.Message); }
    }
    private static IEnumerable<string> Walk(string root, int depth = 0)
    {
        if (!Exists(root)) yield break;
        if (depth > 10) throw new IOException("Inventario de recuperación demasiado profundo.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("Carpeta de recuperación redirigida: " + root);
        foreach (var file in Directory.EnumerateFiles(root, "lease.json")) yield return System.IO.Path.GetFullPath(file);
        foreach (var dir in Directory.EnumerateDirectories(root)) foreach (var file in Walk(dir, depth + 1)) yield return file;
    }
    internal static IReadOnlyList<ProductRecoveryRecord> ReadFans(string? commonRoot = null, string? localRoot = null, bool ignoreCurrentOwner = false)
    {
        commonRoot ??= CommonRoot; localRoot ??= LocalRoot;
        var result = new List<ProductRecoveryRecord>();
        var referenced=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Walk(commonRoot))
        {
            if (result.Count >= 100) throw new IOException("Demasiados registros pendientes; conserva el diagnóstico.");
            var entry = Inspect(path, commonRoot);
            string? session=null;
            if(entry.Kind is "FanGui" or "FanExperiment" && entry.Problem is null)
            {
                using var doc=JsonDocument.Parse(ReadBytes(path));session=System.IO.Path.GetFullPath(doc.RootElement.GetProperty(entry.Kind=="FanGui"?"SessionDirectory":"Directory").GetString()!);referenced.Add(session);
            }
            if (ignoreCurrentOwner && entry.Kind == "FanGui" && entry.Problem is null)
            {
                using var j = JsonDocument.Parse(ReadBytes(path)); using var me = Process.GetCurrentProcess();
                if (j.RootElement.GetProperty("OwnerPid").GetInt32() == me.Id && j.RootElement.GetProperty("OwnerStartUtcTicks").GetInt64() == me.StartTime.ToUniversalTime().Ticks &&
                    session is not null && !Exists(System.IO.Path.Combine(session,"native-uncertain.signal"))) continue;
            }
            result.Add(entry);
        }
        var guiSessions=System.IO.Path.Combine(localRoot,"FanWmi","gui");
        if(Exists(guiSessions))foreach(var directory in Directory.EnumerateDirectories(guiSessions))
        {
            EnsurePlainPath(System.IO.Path.Combine(directory,"check"));if(referenced.Contains(System.IO.Path.GetFullPath(directory)))continue;
            var marker=System.IO.Path.Combine(directory,"native-inflight.json");
            if(!Exists(marker))marker=System.IO.Path.Combine(directory,"native-uncertain.signal");
            if(!Exists(marker))continue;
            if(ignoreCurrentOwner && System.IO.Path.GetFileName(marker)=="native-inflight.json")
            {
                using var j=JsonDocument.Parse(ReadBytes(marker));using var me=Process.GetCurrentProcess();
                if(j.RootElement.GetProperty("Pid").GetInt32()==me.Id && File.GetCreationTimeUtc(marker)>=me.StartTime.ToUniversalTime())continue;
            }
            result.Add(new(marker,"OrphanWmi",Hash(ReadBytes(marker)),Exists(System.IO.Path.Combine(directory,"write-intent.json"))?
                "Hay intención de escritura sin lease. Falta evidencia de propiedad; conserva el diagnóstico y no se aplicará una liberación automática.":null));
        }
        // Journals for another target cannot be silently hidden by the current-target GUI.
        var performance = System.IO.Path.Combine(localRoot, "Performance");
        if (Exists(performance))
            foreach (var directory in Directory.EnumerateDirectories(performance))
            {
                EnsurePlainPath(System.IO.Path.Combine(directory, "check"));
                if (System.IO.Path.GetFileName(directory) == Target) continue;
                foreach (var name in new[] { "cpu-power-session.json", "gpu-clock-session.json" })
                {
                    var file = System.IO.Path.Combine(directory, name);
                    if (Exists(file)) result.Add(new(file, "Unknown", Hash(ReadBytes(file)), "Registro de rendimiento de otro equipo. Conservado para diagnóstico; no se modifica."));
                }
            }
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    internal static bool Same(IEnumerable<ProductRecoveryRecord> a, IEnumerable<ProductRecoveryRecord> b) =>
        a.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).SequenceEqual(b.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase));
    internal static bool AllowsRemaining(IEnumerable<ProductRecoveryRecord>? selected, IEnumerable<ProductRecoveryRecord>? current) =>
        (current ?? []).All(x => (selected ?? []).Contains(x));
    internal static void WriteDurable(string path, byte[] bytes)
    {
        EnsurePlainPath(path);
        using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        f.Write(bytes); f.Flush(true);
    }
}
