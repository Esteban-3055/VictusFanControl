using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

/// <summary>
/// Read-only sensor characterization for the HP 8C40/i7-13700H target.
/// It never acquires Custom fan authority and never sends HP fan commands.
///
/// One representative logical processor per physical core is loaded for a
/// short bounded interval while the normal telemetry path records package and
/// all physical-core temperatures. This lets us determine whether the Intel
/// DTS readings behave independently or are shared/clustered on hybrid cores.
/// </summary>
public static class CpuCoreThermalCharacterizationTest
{
    private static readonly TimeSpan PerCoreLoadDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMilliseconds(1250);
    private static readonly TimeSpan SampleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan IncompleteRetryDelay = TimeSpan.FromMilliseconds(100);
    private const int MaximumConsecutiveIncompleteSnapshots = 3;

    // Stay below the SafetyGate emergency handoff point. This is a sensor
    // characterization, not a thermal stress test.
    private const double AbortCpuTemperatureC = 90.0;

    public static async Task<int> RunAsync(
        HardwareTelemetryReader reader,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("CPU physical-core thermal characterization (READ-ONLY fan path)");
        Console.WriteLine("Target: HP 8C40 / Intel Core i7-13700H");
        Console.WriteLine("Load: one representative logical processor per physical core, 4 s each.");
        Console.WriteLine($"Abort threshold: effective CPU >= {AbortCpuTemperatureC:0} C.");
        Console.WriteLine("No fan command or EC register-value write is performed.");
        Console.WriteLine();

        if (reader.TargetProfile?.Id != Hp8C40TargetProfile.Instance.Id)
        {
            Console.Error.WriteLine(
                "Thermal characterization refused: exact HP 8C40 target was not resolved.");
            return 61;
        }

        if (!reader.BackendsInitialized)
        {
            Console.Error.WriteLine(
                "Thermal characterization refused: telemetry backends are not initialized.");
            foreach (var line in reader.GetBackendDiagnostics())
            {
                Console.Error.WriteLine(line);
            }

            return 62;
        }

        // Prime differential counters (RAPL/load), then establish a complete
        // baseline before creating any deliberate CPU work.
        _ = reader.ReadSnapshot();
        await Task.Delay(750, cancellationToken).ConfigureAwait(false);

        var baseline = await ReadCompleteSnapshotWithRetryAsync(
            reader,
            "initial baseline",
            cancellationToken).ConfigureAwait(false);

        if (baseline is null)
        {
            return 63;
        }

        var cores = baseline.CpuCoreTemperatures
            .OrderBy(core => core.CoreIndex)
            .ToArray();

        var pCores = cores.Count(core => core.CoreType == "Performance");
        var eCores = cores.Count(core => core.CoreType == "Efficiency");

        Console.WriteLine(
            $"Topology: {cores.Length}/{baseline.CpuExpectedPhysicalCoreCount} physical cores; " +
            $"P={pCores}, E={eCores}.");

        if (cores.Length != 14 || pCores != 6 || eCores != 8)
        {
            Console.Error.WriteLine(
                "Thermal characterization refused: expected 14 physical cores " +
                "(6 Performance + 8 Efficiency) on the validated i7-13700H target.");
            return 64;
        }

        Console.WriteLine(
            $"Initial: package={baseline.CpuTemperatureC:0.0} C, " +
            $"core-max={baseline.CpuCoreMaxTemperatureC:0.0} C, " +
            $"effective={baseline.CpuControlTemperatureC:0.0} C.");
        Console.WriteLine();

        var results = new List<CoreThermalResult>(cores.Length);
        var eCoreFrames = new List<Dictionary<int, double>>();
        CaptureECoreFrame(baseline, eCoreFrames);

        foreach (var core in cores)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = await ReadCompleteSnapshotWithRetryAsync(
                reader,
                $"C{core.CoreIndex} pre-load",
                cancellationToken).ConfigureAwait(false);

            if (before is null)
            {
                return 65;
            }

            CaptureECoreFrame(before, eCoreFrames);

            if (before.CpuControlTemperatureC >= AbortCpuTemperatureC)
            {
                Console.Error.WriteLine(
                    $"Characterization aborted before C{core.CoreIndex}: effective CPU " +
                    $"{before.CpuControlTemperatureC:0.0} C >= {AbortCpuTemperatureC:0} C.");
                return 66;
            }

            var baselineMap = before.CpuCoreTemperatures.ToDictionary(
                sample => sample.CoreIndex,
                sample => sample.TemperatureC);

            var peakMap = new Dictionary<int, double>(baselineMap);
            var packagePeak = before.CpuTemperatureC!.Value;
            var effectivePeak = before.CpuControlTemperatureC!.Value;
            var sampleCount = 0;

            Console.Write(
                $"C{core.CoreIndex:00} {ShortType(core.CoreType)} " +
                $"LP{core.LogicalProcessorIndex:00}: " +
                $"before={baselineMap[core.CoreIndex]:0.0} C ... ");

            using var loadCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var loadTask = StartPinnedLoadAsync(
                core.LogicalProcessorIndex,
                PerCoreLoadDuration,
                loadCts.Token);

            try
            {
                while (!loadTask.IsCompleted)
                {
                    var sample = await ReadCompleteSnapshotWithRetryAsync(
                        reader,
                        $"C{core.CoreIndex} active load",
                        cancellationToken,
                        compact: true).ConfigureAwait(false);

                    if (sample is null)
                    {
                        loadCts.Cancel();
                        await IgnoreCancellationAsync(loadTask).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.Error.WriteLine(
                            $"C{core.CoreIndex} telemetry had " +
                            $"{MaximumConsecutiveIncompleteSnapshots} consecutive incomplete snapshots.");
                        return 67;
                    }

                    sampleCount++;
                    UpdatePeaks(sample, peakMap, ref packagePeak, ref effectivePeak);
                    CaptureECoreFrame(sample, eCoreFrames);

                    if (sample.CpuControlTemperatureC >= AbortCpuTemperatureC)
                    {
                        loadCts.Cancel();
                        await IgnoreCancellationAsync(loadTask).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.Error.WriteLine(
                            $"Thermal abort on C{core.CoreIndex}: effective CPU reached " +
                            $"{sample.CpuControlTemperatureC:0.0} C.");
                        return 68;
                    }

                    await Task.Delay(SampleDelay, cancellationToken).ConfigureAwait(false);
                }

                await loadTask.ConfigureAwait(false);

                // One immediate post-load sample catches a delayed DTS/package
                // peak without materially extending the load interval.
                var post = await ReadCompleteSnapshotWithRetryAsync(
                    reader,
                    $"C{core.CoreIndex} post-load",
                    cancellationToken).ConfigureAwait(false);

                if (post is null)
                {
                    Console.WriteLine();
                    return 69;
                }

                UpdatePeaks(post, peakMap, ref packagePeak, ref effectivePeak);
                CaptureECoreFrame(post, eCoreFrames);
            }
            finally
            {
                loadCts.Cancel();
            }

            var targetPeak = peakMap[core.CoreIndex];
            var targetDelta = targetPeak - baselineMap[core.CoreIndex];

            var hottestOtherDelta = peakMap
                .Where(pair => pair.Key != core.CoreIndex)
                .Select(pair => pair.Value - baselineMap[pair.Key])
                .DefaultIfEmpty(0)
                .Max();

            results.Add(new CoreThermalResult(
                core.CoreIndex,
                core.LogicalProcessorIndex,
                core.CoreType,
                baselineMap[core.CoreIndex],
                targetPeak,
                targetDelta,
                hottestOtherDelta,
                packagePeak,
                effectivePeak,
                sampleCount));

            Console.WriteLine(
                $"peak={targetPeak:0.0} C  dT={targetDelta:+0.0;-0.0;0.0} C  " +
                $"other-max-dT={hottestOtherDelta:+0.0;-0.0;0.0} C  " +
                $"pkg-peak={packagePeak:0.0} C");

            await Task.Delay(CooldownDuration, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("Summary:");
        Console.WriteLine(
            "Core Type LP  Before  Peak   dT    OtherMaxdT  PackagePeak  EffectivePeak");

        foreach (var result in results)
        {
            Console.WriteLine(
                $"C{result.CoreIndex:00}  {ShortType(result.CoreType),1}  " +
                $"{result.LogicalProcessorIndex,2}  " +
                $"{result.BeforeC,6:0.0}  {result.PeakC,5:0.0}  " +
                $"{result.DeltaC,5:+0.0;-0.0;0.0}  " +
                $"{result.OtherMaxDeltaC,10:+0.0;-0.0;0.0}  " +
                $"{result.PackagePeakC,11:0.0}  {result.EffectivePeakC,13:0.0}");
        }

        Console.WriteLine();
        PrintECoreSimilarity(eCoreFrames, cores);

        Console.WriteLine();
        Console.WriteLine(
            "RESULT: PASS (characterization completed; E-core grouping below is " +
            "readout-behavior evidence, not proof of a unique physical sensor per core).");

        return 0;
    }


    private static async Task<TelemetrySnapshot?> ReadCompleteSnapshotWithRetryAsync(
        HardwareTelemetryReader reader,
        string context,
        CancellationToken cancellationToken,
        bool compact = false)
    {
        for (var attempt = 1;
             attempt <= MaximumConsecutiveIncompleteSnapshots;
             attempt++)
        {
            var snapshot = reader.ReadSnapshot();

            if (ValidateCompleteSnapshot(snapshot, out _))
            {
                return snapshot;
            }

            var missing = DescribeMissingTelemetry(snapshot);
            var diagnostics = reader.GetReadDiagnostics()
                .Where(line => line.Contains("FAILED:", StringComparison.Ordinal))
                .ToArray();

            if (compact)
            {
                Console.Write(
                    $"[telemetry retry {attempt}/{MaximumConsecutiveIncompleteSnapshots}: " +
                    $"{missing}] ");
            }
            else
            {
                Console.WriteLine(
                    $"{context}: transient incomplete telemetry " +
                    $"({attempt}/{MaximumConsecutiveIncompleteSnapshots}); missing={missing}");

                foreach (var diagnostic in diagnostics)
                {
                    Console.WriteLine($"  {diagnostic}");
                }
            }

            if (attempt < MaximumConsecutiveIncompleteSnapshots)
            {
                await Task.Delay(
                    IncompleteRetryDelay,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        Console.Error.WriteLine(
            $"{context}: telemetry did not recover after " +
            $"{MaximumConsecutiveIncompleteSnapshots} consecutive incomplete snapshots.");
        return null;
    }

    private static string DescribeMissingTelemetry(TelemetrySnapshot snapshot)
    {
        var missing = new List<string>();

        if (!snapshot.CpuTemperatureC.HasValue) missing.Add("cpu_package_temp");
        if (!snapshot.CpuCoreTelemetryComplete)
        {
            missing.Add(
                $"cpu_core_temps({snapshot.CpuCoreTemperatures.Count}/" +
                $"{snapshot.CpuExpectedPhysicalCoreCount?.ToString() ?? "?"})");
        }

        if (!snapshot.CpuPackagePowerW.HasValue) missing.Add("cpu_package_power");
        if (!snapshot.CpuLoadPercent.HasValue) missing.Add("cpu_load");
        if (!snapshot.GpuTemperatureC.HasValue) missing.Add("gpu_temp");
        if (!snapshot.GpuPowerW.HasValue) missing.Add("gpu_power");
        if (!snapshot.GpuLoadPercent.HasValue) missing.Add("gpu_load");
        if (!snapshot.CpuFanRpm.HasValue) missing.Add("cpu_fan_rpm");
        if (!snapshot.GpuFanRpm.HasValue) missing.Add("gpu_fan_rpm");

        return missing.Count == 0
            ? "unknown_component"
            : string.Join(",", missing);
    }

    private static void CaptureECoreFrame(
        TelemetrySnapshot snapshot,
        ICollection<Dictionary<int, double>> frames)
    {
        var frame = snapshot.CpuCoreTemperatures
            .Where(core => core.CoreType == "Efficiency")
            .ToDictionary(core => core.CoreIndex, core => core.TemperatureC);

        if (frame.Count > 0)
        {
            frames.Add(frame);
        }
    }

    private static void PrintECoreSimilarity(
        IReadOnlyList<Dictionary<int, double>> frames,
        IReadOnlyList<CpuCoreTemperatureSample> cores)
    {
        var eCoreIds = cores
            .Where(core => core.CoreType == "Efficiency")
            .Select(core => core.CoreIndex)
            .OrderBy(index => index)
            .ToArray();

        Console.WriteLine("E-core same-snapshot readout similarity:");
        Console.WriteLine(
            $"Frames analysed: {frames.Count}. Cell = mean absolute difference C / exact-match %.");

        Console.Write("      ");
        foreach (var id in eCoreIds)
        {
            Console.Write($" C{id:00}      ");
        }

        Console.WriteLine();

        var candidateEdges = new Dictionary<int, HashSet<int>>();
        foreach (var id in eCoreIds)
        {
            candidateEdges[id] = new HashSet<int>();
        }

        foreach (var left in eCoreIds)
        {
            Console.Write($"C{left:00}  ");

            foreach (var right in eCoreIds)
            {
                if (left == right)
                {
                    Console.Write(" 0.00/100 ");
                    continue;
                }

                var comparable = frames
                    .Where(frame => frame.ContainsKey(left) && frame.ContainsKey(right))
                    .ToArray();

                if (comparable.Length == 0)
                {
                    Console.Write("   n/a    ");
                    continue;
                }

                var meanAbsoluteDifference = comparable
                    .Average(frame => Math.Abs(frame[left] - frame[right]));
                var exactMatches = comparable.Count(
                    frame => frame[left] == frame[right]);
                var exactMatchPercent =
                    100.0 * exactMatches / comparable.Length;

                Console.Write(
                    $"{meanAbsoluteDifference,5:0.00}/{exactMatchPercent,3:0} ");

                // Deliberately conservative: only call a pair a candidate
                // shared/clustered readout if it stays effectively identical
                // across nearly the entire multi-load run.
                if (meanAbsoluteDifference <= 0.25 &&
                    exactMatchPercent >= 95.0)
                {
                    candidateEdges[left].Add(right);
                }
            }

            Console.WriteLine();
        }

        var groups = BuildConnectedGroups(eCoreIds, candidateEdges)
            .Where(group => group.Count > 1)
            .ToArray();

        Console.WriteLine();
        if (groups.Length == 0)
        {
            Console.WriteLine(
                "Candidate shared/clustered E-core readout groups: none at the strict threshold.");
        }
        else
        {
            Console.WriteLine(
                "Candidate shared/clustered E-core readout groups (strict similarity threshold):");

            foreach (var group in groups)
            {
                Console.WriteLine(
                    "  " + string.Join(
                        ", ",
                        group.OrderBy(index => index).Select(index => $"C{index:00}")));
            }
        }

        Console.WriteLine(
            "These groups describe observed temperature-readout behavior only; " +
            "they do not prove Intel exposes one physical DTS sensor for the entire group.");
    }

    private static IReadOnlyList<IReadOnlyList<int>> BuildConnectedGroups(
        IReadOnlyList<int> nodes,
        IReadOnlyDictionary<int, HashSet<int>> edges)
    {
        var visited = new HashSet<int>();
        var groups = new List<IReadOnlyList<int>>();

        foreach (var start in nodes)
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var group = new List<int>();
            var queue = new Queue<int>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                group.Add(current);

                foreach (var next in edges[current])
                {
                    if (visited.Add(next))
                    {
                        queue.Enqueue(next);
                    }
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static bool ValidateCompleteSnapshot(
        TelemetrySnapshot snapshot,
        out string error)
    {
        if (!snapshot.IsComplete)
        {
            error = "base telemetry snapshot is incomplete.";
            return false;
        }

        if (!snapshot.CpuCoreTelemetryComplete)
        {
            error =
                $"physical-core telemetry is incomplete " +
                $"({snapshot.CpuCoreTemperatures.Count}/" +
                $"{snapshot.CpuExpectedPhysicalCoreCount?.ToString() ?? "unknown"}).";
            return false;
        }

        if (!snapshot.CpuControlTemperatureC.HasValue)
        {
            error = "effective CPU temperature is unavailable.";
            return false;
        }

        error = "complete";
        return true;
    }

    private static void UpdatePeaks(
        TelemetrySnapshot snapshot,
        IDictionary<int, double> peakMap,
        ref double packagePeak,
        ref double effectivePeak)
    {
        packagePeak = Math.Max(packagePeak, snapshot.CpuTemperatureC!.Value);
        effectivePeak = Math.Max(effectivePeak, snapshot.CpuControlTemperatureC!.Value);

        foreach (var sample in snapshot.CpuCoreTemperatures)
        {
            if (!peakMap.TryGetValue(sample.CoreIndex, out var previous) ||
                sample.TemperatureC > previous)
            {
                peakMap[sample.CoreIndex] = sample.TemperatureC;
            }
        }
    }

    private static Task StartPinnedLoadAsync(
        int logicalProcessor,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        return Task.Factory.StartNew(
            () =>
            {
                var thread = GetCurrentThread();
                var requestedMask = new UIntPtr(1UL << logicalProcessor);
                var previousMask = SetThreadAffinityMask(thread, requestedMask);

                if (previousMask == UIntPtr.Zero)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"SetThreadAffinityMask failed for LP{logicalProcessor}.");
                }

                try
                {
                    var started = Stopwatch.GetTimestamp();
                    var accumulator = 0.123456789;

                    while (!cancellationToken.IsCancellationRequested &&
                           Stopwatch.GetElapsedTime(started) < duration)
                    {
                        // Deliberately computation-heavy and allocation-free.
                        // One logical processor only; this is not an all-core
                        // stress workload.
                        accumulator =
                            Math.Sqrt(accumulator * accumulator + 1.0000001);

                        if (accumulator > 1_000_000)
                        {
                            accumulator = 0.123456789;
                        }
                    }

                    GC.KeepAlive(accumulator);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (SetThreadAffinityMask(thread, previousMask) == UIntPtr.Zero)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            "Failed to restore characterization worker affinity.");
                    }
                }
            },
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected while enforcing the thermal/caller cancellation boundary.
        }
    }

    private static string ShortType(string coreType) =>
        coreType switch
        {
            "Performance" => "P",
            "Efficiency" => "E",
            _ => "?"
        };

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(
        IntPtr hThread,
        UIntPtr dwThreadAffinityMask);

    private sealed record CoreThermalResult(
        int CoreIndex,
        int LogicalProcessorIndex,
        string CoreType,
        double BeforeC,
        double PeakC,
        double DeltaC,
        double OtherMaxDeltaC,
        double PackagePeakC,
        double EffectivePeakC,
        int Samples);
}
