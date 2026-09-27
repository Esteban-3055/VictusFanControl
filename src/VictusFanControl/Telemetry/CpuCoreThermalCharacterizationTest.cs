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

        var baseline = reader.ReadSnapshot();
        if (!ValidateCompleteSnapshot(baseline, out var baselineError))
        {
            Console.Error.WriteLine($"Baseline refused: {baselineError}");
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

        foreach (var core in cores)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var before = reader.ReadSnapshot();
            if (!ValidateCompleteSnapshot(before, out var beforeError))
            {
                Console.Error.WriteLine(
                    $"C{core.CoreIndex} pre-load telemetry failed: {beforeError}");
                return 65;
            }

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
                    var sample = reader.ReadSnapshot();
                    sampleCount++;

                    if (!ValidateCompleteSnapshot(sample, out var sampleError))
                    {
                        loadCts.Cancel();
                        await IgnoreCancellationAsync(loadTask).ConfigureAwait(false);
                        Console.WriteLine();
                        Console.Error.WriteLine(
                            $"C{core.CoreIndex} telemetry became incomplete: {sampleError}");
                        return 67;
                    }

                    UpdatePeaks(sample, peakMap, ref packagePeak, ref effectivePeak);

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
                var post = reader.ReadSnapshot();
                if (!ValidateCompleteSnapshot(post, out var postError))
                {
                    Console.WriteLine();
                    Console.Error.WriteLine(
                        $"C{core.CoreIndex} post-load telemetry failed: {postError}");
                    return 69;
                }

                UpdatePeaks(post, peakMap, ref packagePeak, ref effectivePeak);
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
        Console.WriteLine(
            "RESULT: PASS (characterization completed; sensor independence/shared-cluster " +
            "interpretation requires reviewing the deltas above).");

        return 0;
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
