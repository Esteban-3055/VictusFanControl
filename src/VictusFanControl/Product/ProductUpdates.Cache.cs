using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VictusFanControl.Product;

public static partial class ProductUpdates
{
    internal static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "updates");
    private const string ReceiptName = "download.json";
    private sealed record CacheReceipt(int Schema, ProductUpdate Update);
    private static string InstallerName(ProductUpdate update) => $"VictusFanControl-{update.Version}-Setup-win-x64.exe";
    private static void ValidateDownload(ProductUpdate update)
    {
        if (update.Version is null || !Regex.IsMatch(update.Version.ToString(), "^[0-9]+\\.[0-9]+\\.[0-9]+$") ||
            update.Download is null || !update.Download.IsAbsoluteUri ||
            update.Download.AbsoluteUri != $"https://github.com/{Repository}/releases/download/v{update.Version}/{InstallerName(update)}" ||
            update.Size <= 0 || update.Size > MaximumInstallerBytes || update.Sha256 is null || !Regex.IsMatch(update.Sha256, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("Solicitud de actualización no válida.");
    }
    private static string CacheDirectory(ProductUpdate update, string root) => Path.Combine(root, $"v{update.Version}-{update.Sha256}");
    private static void RequirePlainPath(string path)
    {
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Carpeta de actualización redirigida.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Archivo de actualización redirigido.");
    }
    // Keep this handle open through Process.Start so a normal writer cannot replace the verified EXE.
    internal static async Task<FileStream> OpenVerifiedInstallerAsync(ProductUpdate update, CancellationToken cancellation, string? root = null)
    {
        ValidateDownload(update);
        var path = Path.Combine(CacheDirectory(update, root ?? CacheRoot), InstallerName(update));
        RequirePlainPath(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (file.Length != update.Size || Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation).ConfigureAwait(false)).ToLowerInvariant() != update.Sha256)
                throw new InvalidDataException("El instalador guardado no coincide con el SHA-256. Descárgalo nuevamente.");
            file.Position = 0; return file;
        }
        catch { await file.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal static async Task<ProductUpdate?> FindCachedAsync(Version current, bool includePrereleases, CancellationToken cancellation, string? root = null)
    {
        root ??= CacheRoot;
        RequirePlainPath(Path.Combine(root, ReceiptName));
        if (!Directory.Exists(root)) return null;
        var candidates = new List<ProductUpdate>();
        foreach (var directory in Directory.EnumerateDirectories(root).Take(128))
        {
            cancellation.ThrowIfCancellationRequested();
            var receipt = Path.Combine(directory, ReceiptName);
            try
            {
                RequirePlainPath(receipt);
                using var file = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (file.Length is <= 0 or > 4096) continue;
                var cached = JsonSerializer.Deserialize<CacheReceipt>(file);
                if (cached is not { Schema: 1 } || cached.Update is null) continue;
                ValidateDownload(cached.Update);
                if (Path.GetFullPath(directory) != Path.GetFullPath(CacheDirectory(cached.Update, root)) || !IsNewer(cached.Update.Version,cached.Update.IsPrerelease,current) || cached.Update.IsPrerelease && !includePrereleases) continue;
                candidates.Add(cached.Update);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException) { /* An incomplete receipt never grants install admission. */ }
        }
        foreach (var update in candidates.OrderByDescending(x => x.Version))
        {
            try { await using var file = await OpenVerifiedInstallerAsync(update, cancellation, root).ConfigureAwait(false); return update; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
    internal static async Task<string> DownloadCachedAsync(ProductUpdate update, CancellationToken cancellation, IProgress<long>? progress = null,
        string? root = null, Func<ProductUpdate, Stream, CancellationToken, IProgress<long>?, Task>? fetch = null)
    {
        ValidateDownload(update); root ??= CacheRoot;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(10)); cancellation = deadline.Token;
        var directory = CacheDirectory(update, root);
        var path = Path.Combine(directory, InstallerName(update));
        RequirePlainPath(path); Directory.CreateDirectory(directory);
        var lockPath = Path.Combine(directory, "download.lock"); RequirePlainPath(lockPath);
        // Serialize duplicate downloads across processes without waiting on the UI thread.
        using var downloadLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var partial = path + ".partial-" + Guid.NewGuid().ToString("N");
        var receipt = Path.Combine(directory, ReceiptName); var temporaryReceipt = receipt + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var valid = false;
            try { await using var existing = await OpenVerifiedInstallerAsync(update, cancellation, root).ConfigureAwait(false); valid = true; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
            if (!valid)
            {
                // v1.1.4 used random directories. Adopt a complete old download after verifying GitHub metadata.
                foreach (var oldDirectory in Directory.EnumerateDirectories(root).Take(128))
                {
                    if (!Regex.IsMatch(Path.GetFileName(oldDirectory), "^[0-9a-f]{32}$")) continue;
                    var oldPath = Path.Combine(oldDirectory, InstallerName(update)); RequirePlainPath(oldPath);
                    if (!File.Exists(oldPath)) continue;
                    await using var old = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                    if (old.Length != update.Size || Convert.ToHexString(await SHA256.HashDataAsync(old, cancellation).ConfigureAwait(false)).ToLowerInvariant() != update.Sha256) continue;
                    old.Position = 0;
                    await using var copy = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await old.CopyToAsync(copy, cancellation).ConfigureAwait(false); copy.Flush(true); break;
                }
                if (!File.Exists(partial))
                {
                    await using var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await (fetch ?? FetchInstallerAsync)(update, output, cancellation, progress).ConfigureAwait(false); output.Flush(true);
                }
                await using (var downloaded = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
                    if (downloaded.Length != update.Size || Convert.ToHexString(await SHA256.HashDataAsync(downloaded, cancellation).ConfigureAwait(false)).ToLowerInvariant() != update.Sha256)
                        throw new InvalidDataException("La descarga no coincide con el SHA-256 de GitHub.");
                cancellation.ThrowIfCancellationRequested(); RequirePlainPath(path); File.Move(partial, path, true);
            }
            cancellation.ThrowIfCancellationRequested(); RequirePlainPath(receipt);
            using (var file = new FileStream(temporaryReceipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, new CacheReceipt(1, update)); file.Flush(true); }
            File.Move(temporaryReceipt, receipt, true);
            progress?.Report(update.Size); return path;
        }
        finally
        {
            // Never delete a verified installer because handoff/recovery/UAC failed.
            foreach (var temporary in new[] { partial, temporaryReceipt })
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
