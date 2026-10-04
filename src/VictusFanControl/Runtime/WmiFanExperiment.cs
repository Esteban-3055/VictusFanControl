using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using System.Management;
using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Runtime;

internal sealed record WmiFanExperimentOptions(string Directory, string Modules, int Seconds, bool Control, int? GuardianPid)
{
    internal static WmiFanExperimentOptions Parse(string[] args)
    {
        string? directory = null;
        var modules = Path.Combine(Environment.CurrentDirectory, "modules");
        var seconds = 300;
        var control = false;
        int? guardian = null;
        for (var i = 1; i < args.Length; i++)
        {
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException("Missing experiment argument value.");
            switch (args[i])
            {
                case "--session-dir": directory = Path.GetFullPath(Value()); break;
                case "--modules-dir": modules = Path.GetFullPath(Value()); break;
                case "--duration-seconds": seconds = int.Parse(Value()); break;
                case "--control": control = true; break;
                case "--guardian-pid": guardian = int.Parse(Value()); break;
                default: throw new ArgumentException("Unsupported/mixed experiment argument: " + args[i]);
            }
        }
        if (directory is null || seconds is < 30 or > 600 || guardian is <= 0)
            throw new ArgumentException("Experiment requires --session-dir and duration 30..600 seconds.");
        return new(directory, modules, seconds, control, guardian);
    }
}

/// <summary>
/// Opt-in supervised experiment, deliberately independent of the EC backend,
/// its ownership protocol and production automatic authorization.
/// </summary>
internal static class WmiFanExperiment
{
    private sealed record AcpiEvent(long RecordId, int Id, DateTime Utc, string Xml);
    internal static bool CanRetireLease(bool noWriteIntent, bool releaseAccepted, bool nativeUnknown, bool workerExited) =>
        workerExited && !nativeUnknown && (noWriteIntent || releaseAccepted);
    internal static string ReadWorkerStopReason(string json, int expectedPid, int exitCode)
    {
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement;
        if (result.GetProperty("Pid").GetInt32() != expectedPid ||
            result.GetProperty("ExitCode").GetInt32() != exitCode ||
            !result.GetProperty("NormalPhaseEnded").GetBoolean())
            throw new InvalidOperationException("Worker result identity/exit status mismatch.");
        if (exitCode == 0 && !result.GetProperty("SamplesAdmitted").GetBoolean())
            throw new InvalidOperationException("Worker completed without admitted telemetry.");
        return result.GetProperty("StopReason").GetString() is { Length: > 0 } reason
            ? reason : throw new InvalidOperationException("Worker result lacks stop reason.");
    }

    internal static async Task<TelemetrySnapshot> AcquireSnapshotAsync(
        HpWmiFanProofReader fans, Func<TelemetrySnapshot> readSnapshot,
        Action ensureNormal, CancellationToken cancellationToken)
    {
        ensureNormal();
        // One bounded fresh 2D acquisition publishes to the registered reader.
        // No stale-cache fallback or repeated attempts after a failed query.
        EcWmiInvestigationTrace.Record(0, "experiment.acquisition.begin", "fresh-RPM-before-CPU-GPU");
        var fresh = await fans.ReadFreshAsync(cancellationToken);
        EcWmiInvestigationTrace.Record(0, "experiment.acquisition.end", $"sequence={fresh.Sequence};sampledAt={fresh.Speeds.SampledAtUtc:O}");
        ensureNormal();
        var snapshot = readSnapshot(); // CPU/GPU epochs begin AFTER waiting for WMI.
        ensureNormal();
        return snapshot;
    }
    internal static AdaptiveFanPolicyConfig CreatePolicy()
    {
        var candidate = Hp8C40AdaptiveCandidateV1.Create();
        IReadOnlyList<AdaptiveFanCurvePoint> Clamp(IReadOnlyList<AdaptiveFanCurvePoint> points) =>
            points.Select(p => p with { Level = Math.Clamp(p.Level, 30, 50) }).ToArray();
        return candidate with { MinimumLevel = 30,
            CpuTemperatureCurve = Clamp(candidate.CpuTemperatureCurve), GpuTemperatureCurve = Clamp(candidate.GpuTemperatureCurve),
            CpuPowerCurve = Clamp(candidate.CpuPowerCurve), GpuPowerCurve = Clamp(candidate.GpuPowerCurve),
            CpuLoadCurve = Clamp(candidate.CpuLoadCurve), GpuLoadCurve = Clamp(candidate.GpuLoadCurve) };
    }
    internal static string LeasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl", "WmiFanExperiment", "lease.json");

    internal static void WriteJson(string path, object value)
    {
        // Atomic publication, durable intent before dispatch; no partially-written heartbeat.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, value);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = WmiFanExperimentOptions.Parse(args);
            Directory.CreateDirectory(options.Directory);
            // Identity check occurs before WMI connection or telemetry initialization.
            var identity = HardwareIdentityReader.ReadCurrent();
            if (HpHardwareTargetResolver.Resolve(identity, out var reason) != Hp8C40TargetProfile.Instance)
                throw new InvalidOperationException("WMI fan experiment requires exact HP 8C40/F.18: " + reason);
            WmiFanExperimentBoundary.Enable(options.Directory, options.Control);
            return options.GuardianPid.HasValue
                ? await WorkerAsync(options, identity)
                : await GuardianAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("WMI fan experiment failed: " + ex);
            return 1;
        }
        finally { _ = EcWmiInvestigationTrace.StopAndFlush(); }
    }

    private static async Task<int> WorkerAsync(WmiFanExperimentOptions o, HardwareIdentity identity)
    {
        using var guardian = Process.GetProcessById(o.GuardianPid!.Value);
        var guardianPath = Path.Combine(o.Directory, "guardian.json");
        using (var info = JsonDocument.Parse(File.ReadAllText(guardianPath)))
        {
            if (info.RootElement.GetProperty("Pid").GetInt32() != guardian.Id ||
                info.RootElement.GetProperty("StartTicks").GetInt64() != guardian.StartTime.ToUniversalTime().Ticks ||
                info.RootElement.GetProperty("Control").GetBoolean() != o.Control ||
                info.RootElement.GetProperty("Mvid").GetString() != typeof(WmiFanExperiment).Module.ModuleVersionId.ToString())
                throw new InvalidOperationException("Experiment guardian identity mismatch.");
        }
        using var reader = new HardwareTelemetryReader(o.Modules, schedulePeriodicFanReads: false);
        if (!reader.BackendsInitialized) throw new InvalidOperationException("Required telemetry backends unavailable.");
        var fans = new HpWmiFanProofReader(); // RPM only; never proof of setpoint/ownership.
        var client = o.Control ? new HpOmenBiosWmiClient() : null;
        var session = client is null ? null : new WmiFanSession(client.Send, level =>
            WriteJson(Path.Combine(o.Directory, "write-intent.json"), new { Level = level,
                Pid = Environment.ProcessId, Utc = DateTimeOffset.UtcNow, RestoreRequired = true }));
        var config = CreatePolicy();
        WriteJson(Path.Combine(o.Directory, "policy.json"), config);
        WriteJson(Path.Combine(o.Directory, "inertia.json"), WmiFanInertiaPolicy.Settings);
        WriteJson(Path.Combine(o.Directory, "final-demand-filter.json"), WmiFinalDemandFilter.Settings);
        var engine = new WmiFanInertiaPolicy(config);
        await using var csv = new CsvTelemetryLogger(Path.Combine(o.Directory, "telemetry.csv"));
        await csv.WriteHeaderAsync(CancellationToken.None);
        using var decisions = new StreamWriter(Path.Combine(o.Directory, "decisions.ndjson")) { AutoFlush = true };
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed;
        var admitted = false;
        var code = 0;
        string stopReason = "duration";
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        void EnsureNormal()
        {
            if (File.Exists(WmiFanExperimentBoundary.StopPath))
            {
                stopReason = "stop-signal";
                cts.Cancel();
            }
            cts.Token.ThrowIfCancellationRequested();
            if (guardian.HasExited) throw new InvalidOperationException("Experiment guardian exited.");
        }
        WriteJson(Path.Combine(o.Directory, "ready.json"), new { Pid = Environment.ProcessId,
            DirectEcProhibited = true, o.Control, ProductionAuthorized = false, MinimumLevel = 30, MaximumLevel = 50,
            FanAcquisition = "fresh-2D-before-snapshot;no-periodic-query-on-snapshot" });
        EcWmiInvestigationTrace.Record(0, "experiment.ready", $"control={o.Control};no-direct-EC");
        try
        {
            while (clock.Elapsed.TotalSeconds < o.Seconds && !cts.IsCancellationRequested)
            {
                if (File.Exists(WmiFanExperimentBoundary.StopPath)) { stopReason = "stop-signal"; break; }
                if (guardian.HasExited) throw new InvalidOperationException("Experiment guardian exited.");
                var gap = clock.Elapsed - previous;
                previous = clock.Elapsed;
                if (gap > TimeSpan.FromSeconds(3)) throw new InvalidOperationException("Timing/suspend gap; normal control stopped.");
                var snapshot = await AcquireSnapshotAsync(fans, reader.ReadSnapshot, EnsureNormal, cts.Token);
                await csv.WriteAsync(snapshot, CancellationToken.None);
                var evaluatedAt = DateTimeOffset.UtcNow;
                var safety = SafetyGate.Evaluate(identity, SystemState.Healthy, snapshot, evaluatedAt, fanWritePathPresent: true);
                // CPU power/load counters may initially lack a baseline. No writes during warmup.
                if (!safety.CustomControlPermitted)
                {
                    if (admitted || clock.Elapsed > TimeSpan.FromSeconds(15))
                    {
                        WriteJson(Path.Combine(o.Directory, "telemetry-fault.json"), new { EvaluatedAtUtc = evaluatedAt,
                            snapshot.Timestamp, snapshot.FanSampledAtUtc, snapshot.FanSampleAgeMilliseconds,
                            snapshot.FanAgeCapturedAtUtc, snapshot.FanTelemetrySource,
                            snapshot.CpuTemperatureC, snapshot.CpuCoreMaxTemperatureC,
                            snapshot.CpuControlTemperatureC, snapshot.GpuTemperatureC, safety.Reasons });
                        throw new InvalidOperationException("Telemetry admission lost: " + string.Join("; ", safety.Reasons));
                    }
                    await Task.Delay(1000, cts.Token);
                    continue;
                }
                admitted = true;
                var decision = engine.Evaluate(new(snapshot.Timestamp, snapshot.CpuControlTemperatureC!.Value,
                    snapshot.CpuPackagePowerW!.Value, snapshot.CpuLoadPercent!.Value, snapshot.GpuTemperatureC!.Value,
                    snapshot.GpuPowerW!.Value, snapshot.GpuLoadPercent!.Value));
                if (!decision.Accepted || !decision.EqualFanLevel.HasValue)
                    throw new InvalidOperationException("Adaptive policy rejected telemetry: " + decision.Detail);
                // Re-evaluate freshness immediately before dispatch (after logger/policy work).
                safety = SafetyGate.Evaluate(identity, SystemState.Healthy, snapshot, DateTimeOffset.UtcNow, true);
                WmiFanExperimentBoundary.SetAdmission(snapshot);
                EnsureNormal();
                var sent = session?.Apply(decision.EqualFanLevel.Value, safety.CustomControlPermitted) ?? false;
                decisions.WriteLine(JsonSerializer.Serialize(new { Utc = DateTimeOffset.UtcNow,
                    decision.EqualFanLevel, decision.RawDemandLevel, decision.SmoothedDemandLevel, decision.ThermalOverride,
                    decision.Detail, Sent = sent, o.Control,
                    WindowsPower = SystemPowerStatusReader.Read(),
                    snapshot.CpuControlTemperatureC, snapshot.GpuTemperatureC,
                    snapshot.CpuFanRpm, snapshot.GpuFanRpm, snapshot.FanSampledAtUtc,
                    snapshot.FanSampleAgeMilliseconds, snapshot.FanAgeCapturedAtUtc, SetpointReadback = false }));
                WriteJson(Path.Combine(o.Directory, "heartbeat.json"), new { Pid = Environment.ProcessId,
                    Utc = DateTimeOffset.UtcNow, ElapsedMs = clock.ElapsedMilliseconds, Level = decision.EqualFanLevel });
                Console.WriteLine($"{DateTimeOffset.UtcNow:O} {(o.Control ? "WMI CONTROL" : "SHADOW")} level={decision.EqualFanLevel}; demand={decision.RawDemandLevel:0.0} smooth={decision.SmoothedDemandLevel:0.0} thermalOverride={decision.ThermalOverride}; CPU={snapshot.CpuControlTemperatureC:0}C GPU={snapshot.GpuTemperatureC:0}C; RPM={snapshot.CpuFanRpm}/{snapshot.GpuFanRpm}");
                await Task.Delay(1000, cts.Token);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { if (stopReason == "duration") stopReason = "cancel"; }
        catch (Exception ex) { code = 1; stopReason = ex.Message; Console.Error.WriteLine(ex); }
        finally
        {
            // Parent performs recovery after worker exit. No re-entry to normal control.
            WmiFanExperimentBoundary.BeginRecovery();
            await csv.FlushAsync(CancellationToken.None);
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(drain.Token); }
            catch (OperationCanceledException) { code = 1; stopReason += "; native WMI did not drain"; }
            if (guardian.HasExited && session?.MayHaveWritten == true)
            {
                try { session.Recover(); stopReason += "; local release requests accepted after guardian loss"; }
                catch (Exception ex) { code = 1; stopReason += "; local recovery: " + ex.Message; }
                // Retained parent lease is reviewed separately; local acceptance is not ownership proof.
            }
            if (!admitted)
            {
                code = 1;
                stopReason += "; no telemetry sample admitted";
            }
            WriteJson(Path.Combine(o.Directory, "worker-result.json"), new { Pid = Environment.ProcessId, ExitCode = code,
                StopReason = stopReason, SamplesAdmitted = admitted, NormalPhaseEnded = true,
                DirectEcProhibited = true, FirmwareRestorationVerified = false });
            EcWmiInvestigationTrace.Record(0, "experiment.normal-ended", stopReason);
        }
        return code;
    }

    private static async Task<int> GuardianAsync(WmiFanExperimentOptions o)
    {
        if (File.Exists(Path.Combine(o.Directory, "guardian.json")))
            throw new InvalidOperationException("Session directory was already used; choose a new directory.");
        EnsureProcessIsolation();
        Directory.CreateDirectory(Path.GetDirectoryName(LeasePath)!);
        // Exclusive creation also rejects a retained/malformed lease. Held through recovery.
        using var lease = new FileStream(LeasePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(lease, new { Pid = Environment.ProcessId, Directory = o.Directory,
            Utc = DateTimeOffset.UtcNow, o.Control, FirmwareRestorationVerified = false });
        lease.Flush(flushToDisk: true);
        using var current = Process.GetCurrentProcess();
        WriteJson(Path.Combine(o.Directory, "guardian.json"), new { Pid = current.Id,
            StartTicks = current.StartTime.ToUniversalTime().Ticks, o.Control,
            Mvid = typeof(WmiFanExperiment).Module.ModuleVersionId.ToString() });
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Process? worker = null;
        Task? stdout = null, stderr = null;
        var releaseAccepted = false;
        var noWriteIntent = false;
        var code = 1;
        var events = new Dictionary<long, AcpiEvent>();
        long baseline = 0;
        var started = DateTimeOffset.UtcNow;
        string stopReason = "duration";
        string? workerStopReason = null;
        try
        {
            baseline = LatestSystemRecord(); // Failure rejects before spawning a writer.
            started = DateTimeOffset.UtcNow;
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // Support dotnet <assembly.dll> and the native Windows apphost.
            if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(WmiFanExperiment).Assembly.Location);
            foreach (var a in new[] { "--wmi-fan-experiment", "--session-dir", o.Directory,
                "--modules-dir", o.Modules, "--duration-seconds", o.Seconds.ToString(), "--guardian-pid", current.Id.ToString() })
                start.ArgumentList.Add(a);
            if (o.Control) start.ArgumentList.Add("--control");
            worker = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
            stdout = PumpAsync(worker.StandardOutput, Path.Combine(o.Directory, "worker-stdout.txt"), Console.Out);
            stderr = PumpAsync(worker.StandardError, Path.Combine(o.Directory, "worker-stderr.txt"), Console.Error);
            var clock = Stopwatch.StartNew();
            var lastHeartbeat = TimeSpan.Zero;
            long lastElapsed = -1;
            while (!worker.HasExited && !cts.IsCancellationRequested && clock.Elapsed.TotalSeconds < o.Seconds + 20)
            {
                foreach (var e in ReadAcpiEvents(baseline, started)) events[e.RecordId] = e;
                if (events.Count > 0) { stopReason = "new-ACPI-13-or-15"; break; }
                var heartbeat = Path.Combine(o.Directory, "heartbeat.json");
                if (File.Exists(heartbeat))
                {
                    using var h = JsonDocument.Parse(File.ReadAllText(heartbeat));
                    if (h.RootElement.GetProperty("Pid").GetInt32() != worker.Id) throw new InvalidOperationException("Wrong heartbeat PID.");
                    var elapsed = h.RootElement.GetProperty("ElapsedMs").GetInt64();
                    if (elapsed > lastElapsed) { lastElapsed = elapsed; lastHeartbeat = clock.Elapsed; }
                }
                if (clock.Elapsed - lastHeartbeat > TimeSpan.FromSeconds(lastElapsed < 0 ? 30 : 8))
                { stopReason = "heartbeat-timeout"; break; }
                if (File.Exists(WmiFanExperimentBoundary.StopPath)) { stopReason = "stop-signal"; break; }
                await Task.Delay(500);
            }
            if (cts.IsCancellationRequested) stopReason = "cancel";
            File.WriteAllText(WmiFanExperimentBoundary.StopPath, stopReason);
            if (!worker.HasExited)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await worker.WaitForExitAsync(closing.Token); }
                catch (OperationCanceledException)
                { throw new InvalidOperationException("Worker still running; do not kill an in-flight native call. Lease retained."); }
            }
            await Task.WhenAll(stdout, stderr);
            code = worker.ExitCode;
            workerStopReason = ReadWorkerStopReason(File.ReadAllText(Path.Combine(o.Directory, "worker-result.json")), worker.Id, code);
            if (stopReason == "duration") stopReason = workerStopReason;
            if (events.Count > 0 || stopReason == "heartbeat-timeout") code = 1;
        }
        catch (Exception ex) { stopReason = ex.Message; Console.Error.WriteLine(ex); code = 1; }
        finally
        {
            File.WriteAllText(WmiFanExperimentBoundary.StopPath, stopReason);
            WmiFanExperimentBoundary.BeginRecovery();
            noWriteIntent = !File.Exists(Path.Combine(o.Directory, "write-intent.json"));
            if (!noWriteIntent)
            {
                try
                {
                    // Recover only after worker exit; native exceptions/deaths retain the in-flight marker.
                    if (worker is null || !worker.HasExited) throw new InvalidOperationException("Worker exit not confirmed; recovery request withheld.");
                    var client = new HpOmenBiosWmiClient();
                    var recovery = new WmiFanSession(client.Send, _ => throw new InvalidOperationException("Guardian cannot write normal levels."));
                    recovery.Recover();
                    releaseAccepted = recovery.Phase == WmiFanSessionPhase.ReleaseAccepted;
                    Console.WriteLine("WMI release sequence accepted. Firmware restoration is NOT independently verified.");
                }
                catch (Exception ex) { code = 1; stopReason += "; recovery: " + ex.Message; Console.Error.WriteLine(ex); }
            }
            // Observe recovery events too. An observation failure invalidates the comparison.
            try
            {
                await Task.Delay(2000);
                // Snapshot covers the whole attempt; phase is available in process chronologies.
                foreach (var e in ReadAcpiEvents(baseline, started)) events[e.RecordId] = e;
            }
            catch (Exception ex) { code = 1; stopReason += "; post-recovery observation: " + ex.Message; }
            if (events.Count > 0) code = 1;
            var nativeUnknown = File.Exists(Path.Combine(o.Directory, "native-inflight.json")) ||
                File.Exists(Path.Combine(o.Directory, "native-uncertain.signal"));
            var retireLease = CanRetireLease(noWriteIntent, releaseAccepted, nativeUnknown, worker is null || worker.HasExited);
            if (!retireLease) code = 1;
            WriteJson(Path.Combine(o.Directory, "summary.json"), new { ExitCode = code, StopReason = stopReason,
                WorkerStopReason = workerStopReason,
                o.Control, DirectEcProhibited = true, ProductionAuthorized = false,
                NoFanWriteIntent = noWriteIntent, ReleaseRequestsAccepted = releaseAccepted,
                FirmwareRestorationVerified = false, LeaseRetained = !retireLease,
                AcpiEvents = events.Values, DeniedEc = WmiOnlyInvestigationPolicy.DeniedEcAccesses,
                NativeCompletionUnknown = nativeUnknown });
            lease.Dispose();
            if (retireLease) File.Delete(LeasePath);
            else Console.Error.WriteLine("RECOVERY INCOMPLETE: preserve " + LeasePath);
            worker?.Dispose();
        }
        return code;
    }

    private static async Task PumpAsync(StreamReader source, string path, TextWriter console)
    {
        using var output = new StreamWriter(path) { AutoFlush = true };
        while (await source.ReadLineAsync() is { } line)
        { await output.WriteLineAsync(line); console.WriteLine(line); }
    }

    private static long LatestSystemRecord()
    {
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName) { ReverseDirection = true });
        using var latest = reader.ReadEvent();
        return latest?.RecordId ?? throw new InvalidOperationException("System event cursor unavailable.");
    }

    private static IEnumerable<AcpiEvent> ReadAcpiEvents(long baseline, DateTimeOffset started)
    {
        if (LatestSystemRecord() < baseline) throw new InvalidOperationException("System event log reset; comparison invalid.");
        var query = $"*[System[Provider[@Name='ACPI'] and (EventID=13 or EventID=15) and EventRecordID > {baseline}]]";
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, query));
        while (reader.ReadEvent() is { } item)
        {
            using (item)
            {
                if (item.TimeCreated?.ToUniversalTime() >= started.UtcDateTime)
                    yield return new AcpiEvent(item.RecordId ?? throw new InvalidOperationException("ACPI event missing record ID."),
                        item.Id, item.TimeCreated!.Value.ToUniversalTime(), item.ToXml());
            }
        }
    }

    private static void EnsureProcessIsolation()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(LeasePath))!;
        if (Directory.Exists(root))
            foreach (var path in Directory.EnumerateFiles(root, "lease.json", SearchOption.AllDirectories))
                throw new InvalidOperationException("Pending lease blocks experiment; do not delete it: " + path);
        using var services = new ManagementObjectSearcher("SELECT Name,State FROM Win32_Service WHERE Name LIKE 'VictusFanControl%'");
        using var serviceRows = services.Get();
        foreach (ManagementObject row in serviceRows)
            using (row)
                if (!string.Equals(Convert.ToString(row["State"]), "Stopped", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Active VFC service blocks isolation: " + row["Name"]);
        using var processes = new ManagementObjectSearcher("SELECT Name,ProcessId FROM Win32_Process WHERE Name LIKE 'VictusFanControl%'");
        using var processRows = processes.Get();
        foreach (ManagementObject row in processRows)
            using (row)
                if (Convert.ToInt32(row["ProcessId"]) != Environment.ProcessId)
                    throw new InvalidOperationException("Another VFC process blocks experiment: " + row["Name"]);
    }
}
