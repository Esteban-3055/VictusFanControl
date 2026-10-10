using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Nvidia;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;
using VictusFanControl.Recovery;

namespace VictusFanControl.PerformanceGuardian;

/// <summary>Explicit release-only entry. Never reached by normal GUI startup.</summary>
internal static class PerformanceGuiRecovery
{
    internal sealed record Options(string Module, Guid CpuSession, Guid GpuSession, string Output, ProductRecoveryOwner? Owner = null);
    internal sealed record Result(CpuPowerRecoveryExecutionResult? Cpu, string Gpu, bool Succeeded);

    internal static Options Parse(string[] args)
    {
        if (args.Length is not (13 or 19) || args[0] != "--recover-gui-session" ||
            args[1] != "--confirm-target" || args[2] != CpuPowerProductDefaults.TargetProfileId ||
            args[3] != "--confirm-cpu-hardware-writes" || args[4] != "--confirm-exclusive-gpu-controller" ||
            args[5] != "--module" || args[7] != "--cpu-session" || args[9] != "--gpu-session" || args[11] != "--output-directory" ||
            !Guid.TryParse(args[8], out var cpu) || !Guid.TryParse(args[10], out var gpu) ||
            cpu == Guid.Empty && gpu == Guid.Empty)
            throw new ArgumentException("Explicit recovery requires exact target, CPU release authorization, exclusive GPU control, expected session IDs, module and a new evidence directory.");
        ProductRecoveryOwner? owner = null;
        if (args.Length == 19)
        {
            if (args[13] != "--owner-pid" || args[15] != "--owner-start" || args[17] != "--owner-sid" ||
                !int.TryParse(args[14], out var pid) || pid <= 0 || !long.TryParse(args[16], out var ticks) || ticks <= 0 || string.IsNullOrWhiteSpace(args[18]))
                throw new ArgumentException("Invalid recovery coordinator identity.");
            owner = new(pid, ticks, args[18]);
        }
        return new Options(Path.GetFullPath(args[6]), cpu, gpu, Path.GetFullPath(args[12]), owner);
    }

    internal static int Run(string[] args)
    {
        var options = Parse(args);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        // Synchronous scope: Windows mutex ownership must stay on its acquiring thread.
        using var mutex = new Mutex(false, PerformanceGuardianHost.ProductionMutexName(CpuPowerProductDefaults.TargetProfileId));
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(0); } catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new InvalidOperationException("A Performance Guardian still owns the target; recovery refused.");
            EnsureNoOtherVictusProcesses(options.Owner);
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Recovery requires an elevated terminal.");
            if (!Hp8C40TargetProfile.Matches(HardwareIdentityReader.ReadCurrent(), out var reason))
                throw new InvalidOperationException("Recovery target mismatch: " + reason);
            var cpuName = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString();
            if (cpuName?.Contains("i7-13700H", StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException("Recovery requires i7-13700H.");
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.Module))).ToLowerInvariant() !=
                "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f")
                throw new InvalidOperationException("IntelMSR module identity mismatch.");
            if (Directory.Exists(options.Output) || File.Exists(options.Output))
                throw new InvalidOperationException("Use a new recovery evidence directory.");
            Directory.CreateDirectory(options.Output);
            Result? result = null;
            string? failure = null;
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VictusFanControl", "Performance", CpuPowerProductDefaults.TargetProfileId);
                var cpuJournal = new JsonCpuPowerSessionJournal(Path.Combine(directory, "cpu-power-session.json"), CpuPowerProductDefaults.TargetProfileId);
                var gpuJournal = new JsonGpuClockSessionJournal(Path.Combine(directory, "gpu-clock-session.json"), CpuPowerProductDefaults.TargetProfileId);
                var cpuRecord = cpuJournal.Load();
                var gpuRecord = gpuJournal.Load();
                ValidateSessions(cpuRecord, gpuRecord, options.CpuSession, options.GpuSession);
                Backup(cpuJournal.Path, options.Output);
                Backup(gpuJournal.Path, options.Output);
                // Backends are opened only after exact journal validation and durable backups.
                using var cpuBackend = cpuRecord is not null ? new PawnIoCpuPowerLimitBackend(options.Module, hardwareWritesAuthorized: true) : null;
                using var nvml = gpuRecord is not null ? new NvmlClient("NVIDIA GeForce RTX 4060 Laptop GPU", requirePreferredDevice: true) : null;
                var gpuBackend = nvml is not null ? new NvmlGpuClockLimitBackend(nvml, hardwareWritesAuthorized: true) : null;
                EnsureNoOtherVictusProcesses(options.Owner);
                if (cpuJournal.Load() != cpuRecord || gpuJournal.Load() != gpuRecord)
                    throw new IOException("Journal changed after evidence capture; no recovery authorized.");
                result = Execute(cpuJournal, gpuJournal, options.CpuSession, options.GpuSession, cpuBackend, gpuBackend);
            }
            catch (Exception ex) { failure = ex.ToString(); }
            var report = new { kind = "VictusFanControl.ExplicitGuiPerformanceRecovery", capturedUtc = DateTimeOffset.UtcNow,
                target = CpuPowerProductDefaults.TargetProfileId, options.CpuSession, options.GpuSession, coordinator = options.Owner, result, failure,
                automaticStarted = false, fanHardwareWrites = false, gpuExactRangeReadback = false };
            var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
            jsonOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            WriteDurable(Path.Combine(options.Output, "recovery-report.json"), JsonSerializer.SerializeToUtf8Bytes(report, jsonOptions));
            Console.WriteLine(JsonSerializer.Serialize(report, jsonOptions));
            return failure is null && result is { Succeeded: true } ? 0 : 4;
        }
        finally { if (ownsMutex) mutex.ReleaseMutex(); }
    }

    internal static void ValidateSessions(CpuPowerSessionJournalRecord? cpu, GpuClockSessionJournalRecord? gpu, Guid expectedCpu, Guid expectedGpu)
    {
        if (expectedCpu == Guid.Empty && expectedGpu == Guid.Empty ||
            (cpu is not null && cpu.SessionId != expectedCpu) || (gpu is not null && gpu.SessionId != expectedGpu))
            throw new InvalidOperationException("Recovery journal session differs from the explicitly selected session; no writes authorized.");
    }

    internal static Result Execute(ICpuPowerSessionJournal cpuJournal, IGpuClockSessionJournal gpuJournal,
        Guid expectedCpu, Guid expectedGpu, ICpuPowerLimitBackend? cpuBackend, IGpuClockLimitBackend? gpuBackend)
    {
        var cpu = cpuJournal.Load();
        var gpu = gpuJournal.Load();
        ValidateSessions(cpu, gpu, expectedCpu, expectedGpu);
        if (cpu is not null && (cpuBackend is null || !cpuBackend.IsSupported))
            throw new InvalidOperationException("CPU recovery backend unavailable.");
        if (gpu is not null && (gpuBackend is null || !gpuBackend.Capabilities.ResetLockedGraphicsClocksExportAvailable || !gpuBackend.Capabilities.HardwareWritesAuthorized))
            throw new InvalidOperationException("GPU reset unavailable or unauthorized.");
        CpuPowerRecoveryExecutionResult? cpuResult = null;
        if (cpu is not null)
        {
            cpuResult = new CpuPowerRecoveryExecutor(cpuBackend!, cpuJournal).Execute();
            if (cpuResult.Value.JournalRetained) return new Result(cpuResult, "NOT_ATTEMPTED_CPU_UNRESOLVED", false);
        }
        if (gpu is null) return new Result(cpuResult, "NO_JOURNAL", true);
        if (gpuJournal.Load() != gpu) throw new InvalidOperationException("GPU journal changed before release; no reset issued.");
        // RecoveryRequired accepts both committed and possibly pending ranges,
        // retaining the original evidence even if the first Set was interrupted.
        var armed = gpu with { Generation = checked(gpu.Generation + 1), Phase = GpuClockJournalPhase.RecoveryRequired,
            RecoveryReason = "EXPLICIT_GUI_RELEASE_RESET_ARMED", UpdatedAtUtc = DateTimeOffset.UtcNow };
        gpuJournal.Store(armed);
        if (gpuJournal.Load() != armed) throw new InvalidOperationException("GPU release journal changed; no reset issued.");
        // NVML cannot prove locked-range ownership. Caller explicitly confirms exclusivity.
        // One Reset, no Set or retry. A failed/unconfirmed call leaves the armed journal.
        var reset = gpuBackend!.ResetLockedGraphicsClocks();
        if (!reset.Succeeded) return new Result(cpuResult, "RESET_UNCONFIRMED_JOURNAL_RETAINED: " + reset.Status, false);
        if (gpuJournal.Load() != armed) return new Result(cpuResult, "RESET_ACK_JOURNAL_CHANGED_RETAINED", false);
        gpuJournal.Delete();
        return new Result(cpuResult, "RESET_ACCEPTED_BY_NVML_NO_EXACT_RANGE_READBACK", true);
    }

    internal static void Backup(string path, string directory)
    {
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        WriteDurable(Path.Combine(directory, Path.GetFileName(path) + ".before.json"), bytes);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            throw new IOException("Journal changed during evidence capture; recovery refused.");
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void EnsureNoOtherVictusProcesses(ProductRecoveryOwner? owner)
    {
        owner?.Validate();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && process.Id != owner?.Pid && process.ProcessName.StartsWith("VictusFanControl", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Close other VictusFanControl applications normally before recovery. Active process: " + process.Id);
            }
        }
    }
}
