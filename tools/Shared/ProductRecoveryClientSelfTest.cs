using System.Text.Json;

namespace VictusFanControl.Recovery;

internal static class ProductRecoveryClientSelfTest
{
    internal static void Run(Action<bool, string> require)
    {
        ProductRecoveryInventorySelfTest.Run();
        var directory = Path.Combine(Path.GetTempPath(), "vfc-recovery-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cpu = Guid.NewGuid(); var gpu = Guid.NewGuid();
            var cpuPath = Path.Combine(directory, "cpu-power-session.json"); var gpuPath = Path.Combine(directory, "gpu-clock-session.json");
            void Write(string path, Guid id, string target = ProductRecoverySelection.Target) => File.WriteAllText(path, JsonSerializer.Serialize(new { SchemaVersion = 1, TargetProfileId = target, SessionId = id }));
            require(!ProductRecoverySelection.Read(directory).Pending, "Empty directory generated a recovery request.");
            Write(cpuPath, cpu); var one = ProductRecoverySelection.Read(directory);
            require(one.CpuSession == cpu && one.GpuSession == Guid.Empty, "CPU-only recovery invented a GPU session.");
            Write(gpuPath, gpu); var both = ProductRecoverySelection.Read(directory);
            require(both.CpuSession == cpu && both.GpuSession == gpu, "Recovery selected other session IDs.");
            File.Delete(cpuPath); require(ProductRecoverySelection.Read(directory).CpuSession == Guid.Empty, "GPU-only recovery invented a CPU session.");
            Write(cpuPath, cpu, "other-target"); bool refused = false;
            try { ProductRecoverySelection.Read(directory); } catch (InvalidDataException) { refused = true; }
            require(refused, "Wrong recovery target accepted.");
            Write(cpuPath, Guid.Empty); refused = false;
            try { ProductRecoverySelection.Read(directory); } catch (InvalidDataException) { refused = true; }
            require(refused, "Empty journal identity accepted.");
            File.WriteAllText(cpuPath, "{ invalid"); refused = false;
            try { ProductRecoverySelection.Read(directory); } catch (JsonException) { refused = true; }
            require(refused, "Malformed recovery record accepted.");
            JsonDocument Report(Guid id, bool automatic = false) => JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                kind = "VictusFanControl.ExplicitGuiPerformanceRecovery", target = ProductRecoverySelection.Target, CpuSession = id, GpuSession = Guid.Empty,
                failure = (string?)null, result = new { Succeeded = true, Cpu = new { Disposition = "ClearedAlreadyReleased" }, Gpu = "NO_JOURNAL" },
                automaticStarted = automatic, fanHardwareWrites = false
            }));
            using (var report = Report(cpu))
            {
                require(ProductRecoveryClient.EvaluateReport(report.RootElement, one, false, "fixture-evidence").Succeeded, "Confirmed recovery report rejected.");
                require(!ProductRecoveryClient.EvaluateReport(report.RootElement, one, true, "fixture-evidence").Succeeded, "Remaining journal was called resolved.");
            }
            using (var report = Report(Guid.NewGuid())) require(!ProductRecoveryClient.EvaluateReport(report.RootElement, one, false, "fixture-evidence").Succeeded, "Other session report accepted.");
            using (var report = Report(cpu, true)) require(!ProductRecoveryClient.EvaluateReport(report.RootElement, one, false, "fixture-evidence").Succeeded, "Recovery report transferred Automatic authority.");
            var owner = new ProductRecoveryOwner(123, 456, "S-1-5-21-fixture");
            var start = ProductRecoveryClient.BuildStart("guardian.exe", "C:\\module path", one, "C:\\evidence path", owner);
            require(start.ArgumentList.Count == 19 && start.ArgumentList[8] == cpu.ToString("D") && start.ArgumentList[10] == Guid.Empty.ToString("D") && !start.UseShellExecute, "Recovery argument identity/quoting changed.");
            require(ProductRecoveryOwner.Matches(owner, 123, 456, owner.Sid, "VictusSetup") && ProductRecoveryOwner.Matches(owner, 123, 456, owner.Sid, "VictusFanControl.App"), "Exact coordinator rejected.");
            require(!ProductRecoveryOwner.Matches(owner, 123, 457, owner.Sid, "VictusSetup") && !ProductRecoveryOwner.Matches(owner, 124, 456, owner.Sid, "VictusSetup") &&
                !ProductRecoveryOwner.Matches(owner, 123, 456, "other-user", "VictusSetup") && !ProductRecoveryOwner.Matches(owner, 123, 456, owner.Sid, "VictusFanControl.PerformanceGuardian"), "Reused PID, other user/process granted an exception.");
            if (OperatingSystem.IsWindows())
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                if (process.MainModule?.FileVersionInfo.FileDescription is "VictusSetup" or "VictusFanControl.App") ProductRecoveryOwner.Capture().Validate();
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Guided recovery client: PASS (CPU/GPU-only, exact IDs, malformed target refusal, arguments and exact coordinator; no hardware IO).");
    }
}
