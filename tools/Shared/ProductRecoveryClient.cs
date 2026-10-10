using System.Diagnostics;
using System.Text.Json;

namespace VictusFanControl.Recovery;

internal sealed record ProductRecoverySelection(Guid CpuSession, Guid GpuSession)
{
    internal const string Target = "HP-8C40-9D0R1LA-F18";
    internal static string JournalDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "Performance", Target);
    internal bool Pending => CpuSession != Guid.Empty || GpuSession != Guid.Empty;
    internal string Summary => "CPU: " + (CpuSession == Guid.Empty ? "sin registro pendiente" : "restaurar sólo los límites aún propios de la sesión") +
        "\nGPU: " + (GpuSession == Guid.Empty ? "sin registro pendiente" : "solicitar el reset de frecuencias de NVIDIA");
    internal static ProductRecoverySelection Read(string? directory = null)
    {
        directory ??= JournalDirectory;
        return new(ReadId(Path.Combine(directory, "cpu-power-session.json")), ReadId(Path.Combine(directory, "gpu-clock-session.json")));
    }
    private static Guid ReadId(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return Guid.Empty; }
        catch (DirectoryNotFoundException) { return Guid.Empty; }
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Registro de recuperación demasiado grande. Conserva el diagnóstico.");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = json.RootElement;
        if (root.GetProperty("SchemaVersion").GetInt32() != 1 || root.GetProperty("TargetProfileId").GetString() != Target ||
            !Guid.TryParse(root.GetProperty("SessionId").GetString(), out var id) || id == Guid.Empty)
            throw new InvalidDataException("Registro de recuperación inválido. Conserva los archivos y exporta el diagnóstico.");
        return id;
    }
}

internal sealed record ProductRecoveryResult(bool Succeeded, string EvidenceDirectory, string Detail);

internal static class ProductRecoveryClient
{
    internal static ProcessStartInfo BuildStart(string guardian, string modules, ProductRecoverySelection selection, string evidence, ProductRecoveryOwner owner)
    {
        if (!selection.Pending) throw new InvalidOperationException("No hay una sesión pendiente seleccionada.");
        var start = new ProcessStartInfo(guardian) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--recover-gui-session", "--confirm-target", ProductRecoverySelection.Target,
            "--confirm-cpu-hardware-writes", "--confirm-exclusive-gpu-controller", "--module", Path.Combine(modules, "IntelMSR.bin"),
            "--cpu-session", selection.CpuSession.ToString("D"), "--gpu-session", selection.GpuSession.ToString("D"), "--output-directory", evidence,
            "--owner-pid", owner.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "--owner-start", owner.StartUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture), "--owner-sid", owner.Sid })
            start.ArgumentList.Add(arg);
        return start;
    }
    internal static async Task<ProductRecoveryResult> RunAsync(string appDirectory, string modules, ProductRecoverySelection selection)
    {
        var guardian = Path.Combine(appDirectory, "performance-guardian", "VictusFanControl.PerformanceGuardian.exe");
        if (!File.Exists(guardian)) throw new IOException("Falta Performance Guardian en esta instalación. Utiliza un paquete completo.");
        var evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "Recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        var start = BuildStart(guardian, modules, selection, evidence, ProductRecoveryOwner.Capture());
        using var process = Process.Start(start) ?? throw new IOException("No se pudo abrir la recuperación.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Do not kill a recovery process during a hardware release or durable write.
        await process.WaitForExitAsync();
        var log = (await output) + (await error);
        var reportPath = Path.Combine(evidence, "recovery-report.json");
        if (process.ExitCode != 0 || !File.Exists(reportPath))
            return new(false, evidence, "Recuperación no completada. Cierra normalmente las otras aplicaciones de VictusFanControl y conserva los registros.\n" + log);
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
        return EvaluateReport(report.RootElement, selection, ProductRecoverySelection.Read().Pending, evidence);
    }
    internal static ProductRecoveryResult EvaluateReport(JsonElement r, ProductRecoverySelection selection, bool stillPending, string evidence)
    {
        var success = r.GetProperty("kind").GetString() == "VictusFanControl.ExplicitGuiPerformanceRecovery" &&
            r.GetProperty("target").GetString() == ProductRecoverySelection.Target && r.GetProperty("CpuSession").GetGuid() == selection.CpuSession &&
            r.GetProperty("GpuSession").GetGuid() == selection.GpuSession && r.GetProperty("failure").ValueKind == JsonValueKind.Null &&
            r.GetProperty("result").GetProperty("Succeeded").GetBoolean() && !r.GetProperty("automaticStarted").GetBoolean() &&
            !r.GetProperty("fanHardwareWrites").GetBoolean() && !stillPending;
        var detail = "No se confirmó la recuperación completa. Conserva la evidencia y los registros pendientes.";
        if (success)
        {
            var result = r.GetProperty("result");
            var cpu = result.GetProperty("Cpu");
            var cpuText = cpu.ValueKind == JsonValueKind.Null ? "CPU: sin registro pendiente." : cpu.GetProperty("Disposition").GetString() switch
            {
                "ClearedAlreadyReleased" => "CPU: límites originales ya presentes; no se escribió hardware.",
                "ClearedExternalPreserved" => "CPU: configuración externa conservada.",
                _ => "CPU: límites de la sesión restaurados."
            };
            var gpuText = result.GetProperty("Gpu").GetString() == "NO_JOURNAL" ? "GPU: sin registro pendiente." : "GPU: reset aceptado por NVIDIA; el rango exacto no es observable.";
            detail = "Recuperación completada. " + cpuText + " " + gpuText + " Respaldos conservados.";
        }
        return new(success, evidence, detail);
    }
}
