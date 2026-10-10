using System.Diagnostics;
using System.Text.Json;

namespace VictusFanControl.Recovery;

internal static class ProductRecoveryFansClient
{
    internal static ProcessStartInfo BuildStart(string guardian, string modules, string request, string requestHash, string evidence, ProductRecoveryOwner owner, int index = 0)
    {
        var start = new ProcessStartInfo(guardian) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in new[] { "--recover-product-fans", "--request", request, "--request-sha", requestHash,
            "--modules", modules, "--output", evidence, "--owner-pid", owner.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--owner-start", owner.StartUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "--owner-sid", owner.Sid, "--confirm-release-only", "--record-index", index.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(arg);
        return start;
    }
    internal static async Task<ProductRecoveryResult> RunAsync(string appDirectory, string modules, IReadOnlyList<ProductRecoveryRecord> selected)
    {
        var root = Path.Combine(ProductRecoveryInventory.LocalRoot, "Recovery", Guid.NewGuid().ToString("N"));
        ProductRecoveryInventory.EnsurePlainPath(Path.Combine(root, "check")); Directory.CreateDirectory(root);
        var request = Path.Combine(root, "fan-request.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(selected);
        ProductRecoveryInventory.WriteDurable(request, bytes);
        for (var index = 0; index < selected.Count; index++)
        {
            if (!ProductRecoveryInventory.Exists(selected[index].Path)) continue; // Another normal release may already have completed.
            var evidence = Path.Combine(root, "fans-" + index);
            using var child = Process.Start(BuildStart(Path.Combine(appDirectory, "performance-guardian", "VictusFanControl.PerformanceGuardian.exe"),
                modules, request, ProductRecoveryInventory.Hash(bytes), evidence, ProductRecoveryOwner.Capture(), index)) ?? throw new IOException("No se pudo abrir la recuperación de ventiladores.");
            var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync(); // A hardware release is never forcibly terminated.
            var log = (await output) + (await error);
            var file = Path.Combine(evidence, "fan-recovery-report.json");
            if (child.ExitCode != 0 || !ProductRecoveryInventory.Exists(file)) return new(false, root, "Recuperación de ventiladores no completada. " + log + "\nRespaldos: " + root);
            using var report = JsonDocument.Parse(ProductRecoveryInventory.ReadBytes(file));
            var result = Evaluate(report.RootElement, selected, ProductRecoveryInventory.Exists(selected[index].Path), root);
            if (!result.Succeeded || report.RootElement.GetProperty("recordIndex").GetInt32() != index) return result with { Succeeded = false };
        }
        return new(ProductRecoveryInventory.ReadFans().Count == 0, root, "Sesiones de ventiladores recuperadas. Respaldos conservados; sin Automático.");
    }
    internal static ProductRecoveryResult Evaluate(JsonElement r, IReadOnlyList<ProductRecoveryRecord> expected, bool pending, string evidence)
    {
        var selected = r.GetProperty("selected").Deserialize<ProductRecoveryRecord[]>();
        var ok = r.GetProperty("kind").GetString() == "VictusFanControl.ProductFanRecovery" && r.GetProperty("target").GetString() == ProductRecoveryInventory.Target &&
            r.GetProperty("succeeded").GetBoolean() && !r.GetProperty("automaticStarted").GetBoolean() && !r.GetProperty("normalFanTargets").GetBoolean() &&
            r.GetProperty("failure").ValueKind == JsonValueKind.Null && selected is not null && ProductRecoveryInventory.Same(expected, selected) && !pending;
        return new(ok, evidence, ok ? "Sesiones de ventiladores recuperadas. Respaldos conservados; sólo liberación, sin Automático." : "No se confirmó la recuperación de ventiladores; conserva la evidencia.");
    }
}
