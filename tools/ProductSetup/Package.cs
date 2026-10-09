using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VictusSetup;

internal static class Package
{
    internal const string Version = "1.1.0";
    internal static void Extract(Stream source, string destination)
    {
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName;
            if (path.Length == 0 || path.Length > 240 || path.Split('/').Any(p => p.Length == 0 || p is "." or ".." ||
                p.EndsWith(' ') || p.EndsWith('.') || Regex.IsMatch(p, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\\.|$)", RegexOptions.IgnoreCase)) ||
                path.Any(c => c < 32 || "\\:*?\"<>|".Contains(c)) || !names.Add(path) ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || entry.Length > 128L * 1024 * 1024)
                throw new InvalidDataException("Ruta de paquete no válida: " + path);
            total += entry.Length;
            if (total > 512L * 1024 * 1024) throw new InvalidDataException("Paquete demasiado grande.");
        }
        var manifestEntry = archive.GetEntry("PRODUCT-GUI-MANIFEST.json") ?? throw new InvalidDataException("Falta el manifiesto.");
        if (manifestEntry.Length > 1024 * 1024) throw new InvalidDataException("Manifiesto demasiado grande.");
        using var manifestStream = manifestEntry.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var root = manifest.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("kind").GetString() != "VictusFanControl.ProductGuiRelease" ||
            root.GetProperty("version").GetString() != Version || root.GetProperty("appDirectory").GetString() != "app" ||
            !root.GetProperty("finalReleaseReady").GetBoolean() || !Regex.IsMatch(root.GetProperty("sourceHead").GetString() ?? "", "^[0-9a-f]{40}$"))
            throw new InvalidDataException("Identidad de release incorrecta.");
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestEntry.FullName };
        foreach (var file in root.GetProperty("files").EnumerateArray())
        {
            var path = file.GetProperty("path").GetString() ?? "";
            if (!listed.Add(path)) throw new InvalidDataException("Archivo duplicado en el manifiesto.");
            var entry = archive.GetEntry(path) ?? throw new InvalidDataException("Falta " + path);
            using var input = entry.Open();
            if (entry.Length != file.GetProperty("size").GetInt64() || Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant() != file.GetProperty("sha256").GetString())
                throw new InvalidDataException("Integridad incorrecta: " + path);
        }
        if (!names.SetEquals(listed) || !listed.Contains("Install-VictusFanControl.ps1") || !listed.Contains("Start-ProductGui.ps1") ||
            !listed.Contains("app/VictusFanControl.App.exe")) throw new InvalidDataException("Contenido de paquete incompleto o adicional.");
        if (Directory.Exists(destination)) throw new IOException("La carpeta de preparación ya existe.");
        Directory.CreateDirectory(destination);
        foreach (var entry in archive.Entries)
        {
            var target = Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew);
            input.CopyTo(output);
        }
    }
}
