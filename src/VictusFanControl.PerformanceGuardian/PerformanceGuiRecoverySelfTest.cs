using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuiRecoverySelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            var cpuId = Guid.NewGuid(); var gpuId = Guid.NewGuid();
            var args = new[] { "--recover-gui-session", "--confirm-target", CpuPowerProductDefaults.TargetProfileId,
                "--confirm-cpu-hardware-writes", "--confirm-exclusive-gpu-controller", "--module", "IntelMSR.bin",
                "--cpu-session", cpuId.ToString(), "--gpu-session", gpuId.ToString(), "--output-directory", "recovery-evidence" };
            Require(PerformanceGuiRecovery.Parse(args).CpuSession == cpuId, "exact explicit options accepted");
            foreach (var index in new[] { 0, 2, 3, 4, 8, 10 })
            {
                var invalid = args.ToArray(); invalid[index] = "invalid";
                Throws(() => PerformanceGuiRecovery.Parse(invalid), "required authorization/identity refused");
            }
            Throws(() => PerformanceGuiRecovery.Parse(args[..^1]), "partial options refused");
            var coordinated = args.Concat(new[] { "--owner-pid", "123", "--owner-start", "456", "--owner-sid", "fixture-user" }).ToArray();
            Require(PerformanceGuiRecovery.Parse(coordinated).Owner?.Pid == 123, "coordinator options accepted");
            foreach (var index in new[] { 13, 14, 15, 16, 17 })
            {
                var invalid = coordinated.ToArray(); invalid[index] = "invalid";
                Throws(() => PerformanceGuiRecovery.Parse(invalid), "partial/invalid coordinator rejected");
            }
            var cpuOnlyArgs = args.ToArray(); cpuOnlyArgs[10] = Guid.Empty.ToString();
            Require(PerformanceGuiRecovery.Parse(cpuOnlyArgs).GpuSession == Guid.Empty, "CPU-only options accepted without invented GPU ID");
            var gpuOnlyArgs = args.ToArray(); gpuOnlyArgs[8] = Guid.Empty.ToString();
            Require(PerformanceGuiRecovery.Parse(gpuOnlyArgs).CpuSession == Guid.Empty, "GPU-only options accepted without invented CPU ID");
            var gpuRecord = new GpuClockSessionJournalRecord(1, CpuPowerProductDefaults.TargetProfileId, gpuId, 2,
                GpuClockJournalPhase.ActiveUnverified, new GpuClockLimitRequest(210, 1802), null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            var cpu = new CpuJournal(); var gpu = new GpuJournal { Record = gpuRecord }; var backend = new GpuBackend(gpu);
            Throws(() => PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, Guid.Empty, null, backend), "new unselected GPU journal refused");
            Throws(() => PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, Guid.NewGuid(), null, backend), "other session refused");
            Require(backend.Resets == 0 && gpu.Stores == 0 && gpu.Deletes == 0, "rejected identity causes no mutations");
            var result = PerformanceGuiRecovery.Execute(cpu, gpu, Guid.Empty, gpuId, null, backend);
            Require(result.Succeeded && backend.Resets == 1 && gpu.Record is null && gpu.Deletes == 1, "one armed reset ACK then clear");
            result = PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, gpuId, null, backend);
            Require(result.Succeeded && backend.Resets == 1, "already resolved retry does not reset again");
            gpu.Record = gpuRecord; backend.Success = false;
            result = PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, gpuId, null, backend);
            Require(!result.Succeeded && gpu.Record?.Phase == GpuClockJournalPhase.RecoveryRequired && backend.Resets == 2, "reset failure retains armed evidence without retry");
            gpu.Record = gpuRecord; backend.Success = true; backend.ChangeJournal = true;
            result = PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, gpuId, null, backend);
            Require(!result.Succeeded && gpu.Record is not null && gpu.Deletes == 1, "changed journal after ACK is retained");
            gpu.Record = gpuRecord; backend.ChangeJournal = false; backend.Throw = true;
            Throws(() => PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, gpuId, null, backend), "transport throw propagated");
            Require(gpu.Record?.Phase == GpuClockJournalPhase.RecoveryRequired, "transport throw retains durable intent");
            var now = DateTimeOffset.UtcNow;
            cpu.Record = new CpuPowerSessionJournalRecord(1, CpuPowerProductDefaults.TargetProfileId, cpuId, 2,
                CpuPowerJournalPhase.Owned, new CpuPowerLimitSnapshot(1, 45, 115, false), new CpuPowerLimitRequest(30, 50),
                2, null, new CpuPowerConflictSnapshot(CpuPowerConflictState.Inactive, 0, 5, false, null, null, 0, 0), null, now, now);
            gpu.Record = gpuRecord; backend.Throw = false;
            var resets = backend.Resets;
            result = PerformanceGuiRecovery.Execute(cpu, gpu, cpuId, gpuId, new UnreadableCpuBackend(), backend);
            Require(!result.Succeeded && result.Cpu?.JournalRetained == true && backend.Resets == resets && gpu.Record == gpuRecord,
                "unresolved CPU blocks GPU release without changing GPU journal");
            var root = Path.Combine(Path.GetTempPath(), "VFC-Recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var source = Path.Combine(root, "journal.json");
                var evidence = Path.Combine(root, "evidence"); Directory.CreateDirectory(evidence);
                var bytes = new byte[] { 239, 187, 191, 123, 13, 10, 125 }; // BOM and exact CRLF preserved.
                File.WriteAllBytes(source, bytes);
                PerformanceGuiRecovery.Backup(source, evidence);
                Require(File.ReadAllBytes(Path.Combine(evidence, "journal.json.before.json")).SequenceEqual(bytes) &&
                    File.ReadAllBytes(source).SequenceEqual(bytes), "backup preserves original bytes and source");
                Throws(() => PerformanceGuiRecovery.Backup(source, evidence), "existing evidence never overwritten");
                var realGpu = new JsonGpuClockSessionJournal(Path.Combine(root, "gpu.json"), CpuPowerProductDefaults.TargetProfileId);
                cpu.Record = null;
                var realBackend = new GpuBackend(realGpu);
                foreach (var record in new[] { gpuRecord, gpuRecord with { Phase = GpuClockJournalPhase.ApplyWriteArmed,
                    CommittedRequest = null, PendingRequest = new GpuClockLimitRequest(210, 1802) } })
                {
                    realGpu.Store(record);
                    result = PerformanceGuiRecovery.Execute(cpu, realGpu, cpuId, gpuId, null, realBackend);
                    Require(result.Succeeded && !File.Exists(realGpu.Path), "real JSON invariant accepts armed reset for committed or pending range");
                }
                realGpu.Store(gpuRecord); realBackend.Success = false;
                result = PerformanceGuiRecovery.Execute(cpu, realGpu, cpuId, gpuId, null, realBackend);
                Require(!result.Succeeded && realGpu.Load()?.RecoveryReason == "EXPLICIT_GUI_RELEASE_RESET_ARMED",
                    "real JSON retains readable release intent on failure");
            }
            finally { Directory.Delete(root, recursive: true); }
            output.WriteLine("GUI release-only recovery: PASS (exact IDs, explicit gates, idempotence, durable intent, failed reset, changed journal, CPU failure; fake IO only).");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("GUI release-only recovery: FAIL " + ex); return 1; }
    }

    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void Throws(Action action, string detail)
    {
        try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; } catch (IOException) { return; }
        throw new InvalidOperationException(detail);
    }
    private sealed class CpuJournal : ICpuPowerSessionJournal
    {
        internal CpuPowerSessionJournalRecord? Record;
        public string Path => "fixture-cpu";
        public string TargetProfileId => CpuPowerProductDefaults.TargetProfileId;
        public CpuPowerSessionJournalRecord? Load() => Record;
        public void Store(CpuPowerSessionJournalRecord record) => Record = record;
        public void Delete() => Record = null;
    }
    private sealed class GpuJournal : IGpuClockSessionJournal
    {
        internal GpuClockSessionJournalRecord? Record;
        internal int Stores, Deletes;
        public string Path => "fixture-gpu";
        public string TargetProfileId => CpuPowerProductDefaults.TargetProfileId;
        public GpuClockSessionJournalRecord? Load() => Record;
        public void Store(GpuClockSessionJournalRecord record) { Stores++; Record = record; }
        public void Delete() { Deletes++; Record = null; }
    }
    private sealed class GpuBackend(IGpuClockSessionJournal journal) : IGpuClockLimitBackend
    {
        internal int Resets;
        internal bool Success = true, ChangeJournal, Throw;
        public GpuClockBackendCapabilities Capabilities => new(true, true, true, false, false, false, true);
        public GpuClockBackendWriteResult SetLockedGraphicsClocks(GpuClockLimitRequest request) => throw new InvalidOperationException("Recovery must never Set.");
        public GpuClockBackendWriteResult ResetLockedGraphicsClocks()
        {
            var armed = journal.Load();
            Require(armed?.Phase == GpuClockJournalPhase.RecoveryRequired && armed.RecoveryReason == "EXPLICIT_GUI_RELEASE_RESET_ARMED", "reset requires durable intent");
            Resets++;
            if (Throw) throw new IOException("fixture transport failure");
            if (ChangeJournal) journal.Store(armed! with { Generation = armed!.Generation + 1 });
            return new(Success, Success ? GpuClockBackendFailureKind.None : GpuClockBackendFailureKind.DriverUnavailable, Success ? 0 : 9, "FIXTURE");
        }
        public GpuClockBackendObservation ReadObservation() => throw new InvalidOperationException("Do not infer locked ownership from observed clocks.");
    }
    private sealed class UnreadableCpuBackend : ICpuPowerLimitBackend
    {
        public bool IsSupported => true;
        public CpuPowerLimitSnapshot Read() => throw new IOException("fixture unavailable");
        public CpuPowerLimitApplyPlan BuildApplyPlan(CpuPowerLimitSnapshot baseline, CpuPowerLimitRequest request) => throw new InvalidOperationException();
        public CpuPowerLimitApplyPlan BuildReacquirePlan(CpuPowerLimitSnapshot baseline, CpuPowerLimitRequest request, CpuPowerLimitSnapshot current) => throw new InvalidOperationException();
        public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(CpuPowerLimitSnapshot baseline, CpuPowerLimitRequest request, CpuPowerLimitSnapshot current) => throw new InvalidOperationException();
        public CpuPowerLimitRestorePlan PlanRestore(CpuPowerLimitSnapshot target, ulong raw, CpuPowerLimitSnapshot current) => throw new InvalidOperationException();
        public bool OwnedFieldsMatch(ulong expected, CpuPowerLimitSnapshot current) => throw new InvalidOperationException();
        public void Write(ulong raw) => throw new InvalidOperationException("Unreadable CPU must never write.");
    }
}
