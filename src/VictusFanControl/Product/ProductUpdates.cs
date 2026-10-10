using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VictusFanControl.Product;

public sealed record ProductUpdate(Version Version, Uri Download, long Size, string Sha256, bool IsPrerelease = false);

/// <summary>Public releases with an explicit preview opt-in. No tokens, background installation or hardware IO.</summary>
public static class ProductUpdates
{
    public const string Repository = "Esteban-3055/VictusFanControl";
    public const long MaximumInstallerBytes = 256L * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VictusFanControl-Updater/1.1");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    public static ProductUpdate? ParseRelease(string json, Version current, bool includePrereleases = false)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var prerelease = root.GetProperty("prerelease").GetBoolean();
        if (root.GetProperty("draft").GetBoolean() || (prerelease && !includePrereleases)) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+$")) throw new InvalidDataException("Versión de release no válida.");
        var version = Version.Parse(tag[1..]);
        if (version <= current) return null;
        var name = $"VictusFanControl-{version}-Setup-win-x64.exe";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new InvalidDataException("La release no tiene un instalador único compatible.");
        var asset = assets[0];
        var size = asset.GetProperty("size").GetInt64();
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var expected = $"https://github.com/{Repository}/releases/download/{tag}/{name}";
        if (asset.GetProperty("state").GetString() != "uploaded" || asset.GetProperty("browser_download_url").GetString() != expected ||
            size <= 0 || size > MaximumInstallerBytes || !Regex.IsMatch(digest, "^sha256:[0-9a-f]{64}$"))
            throw new InvalidDataException("El instalador no tiene URL, tamaño o SHA-256 válidos en GitHub.");
        return new(version, new Uri(expected), size, digest[7..], prerelease);
    }
    public static ProductUpdate? ParseReleases(string json, Version current, bool includePrereleases = false)
    {
        using var document = JsonDocument.Parse(json);
        // GitHub orders by publication date, which need not match product version order.
        // This product publishes numeric vMajor.Minor.Patch tags, including previews.
        var candidates = document.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && (includePrereleases || !r.GetProperty("prerelease").GetBoolean()))
            .Select(r => new { Release = r, Tag = r.GetProperty("tag_name").GetString() ?? "" })
            .Where(r => Regex.IsMatch(r.Tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+$") && Version.TryParse(r.Tag[1..], out _))
            .Select(r => new { r.Release, Version = Version.Parse(r.Tag[1..]) })
            .Where(r => r.Version > current)
            .OrderByDescending(r => r.Version).ToArray();
        // An invalid newest installer is an error, never a reason to silently offer an older release.
        return candidates.Length == 0 ? null : ParseRelease(candidates[0].Release.GetRawText(), current, includePrereleases);
    }
    public static async Task<ProductUpdate?> CheckAsync(Version current, CancellationToken cancellation, bool includePrereleases = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Client.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=100", HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var memory = new MemoryStream();
        await CopyBoundedAsync(stream, memory, 4 * 1024 * 1024, deadline.Token);
        return ParseReleases(System.Text.Encoding.UTF8.GetString(memory.ToArray()), current, includePrereleases);
    }
    public static async Task<string> DownloadAsync(ProductUpdate update, CancellationToken cancellation, IProgress<long>? progress = null)
    {
        // Validate the public record again before using it as a filesystem/network capability.
        var tag = "v" + update.Version;
        var name = $"VictusFanControl-{update.Version}-Setup-win-x64.exe";
        if (update.Download.AbsoluteUri != $"https://github.com/{Repository}/releases/download/{tag}/{name}" ||
            update.Size <= 0 || update.Size > MaximumInstallerBytes || !Regex.IsMatch(update.Sha256, "^[0-9a-f]{64}$"))
            throw new InvalidDataException("Solicitud de actualización no válida.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        cancellation = deadline.Token;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        try
        {
            using var response = await Client.GetAsync(update.Download, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != update.Size) throw new IOException("Tamaño de descarga incorrecto.");
            await using (var source = await response.Content.ReadAsStreamAsync(cancellation))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await CopyBoundedAsync(source, output, update.Size, cancellation, progress);
            await using var file = File.OpenRead(path);
            if (file.Length != update.Size || Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).ToLowerInvariant() != update.Sha256)
                throw new InvalidDataException("La descarga no coincide con el SHA-256 de GitHub.");
            return path;
        }
        catch { File.Delete(path); Directory.Delete(directory); throw; }
    }
    internal static async Task CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken cancellation, IProgress<long>? progress = null)
    {
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            total += count;
            if (total > maximum) throw new InvalidDataException("La respuesta supera el tamaño permitido.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            progress?.Report(total);
        }
    }
}
