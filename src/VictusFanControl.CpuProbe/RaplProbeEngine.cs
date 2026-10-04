using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VictusFanControl.Hardware.Intel;

namespace VictusFanControl.CpuProbe;

internal readonly record struct CpuObservation(
    ulong RawLimit, double? PowerW, double? TemperatureC, double? LoadPercent, bool AcOnline);

internal interface IRaplProbeHardware : IDisposable
{
    ulong UnitsRaw { get; }
    ulong PowerInfoRaw { get; }
    ulong ReadLimit();
    void WriteLimit(ulong value);
    CpuObservation Sample();
}

internal sealed record ProbeTiming(int BaselineSamples, int LimitedSamples, int RestoredSamples, int IntervalMs)
{
    internal static ProbeTiming Physical(int seconds) => new(10, seconds, 10, 1000);
}

internal sealed record ProbeResult(
    string Result, string RestoreResult, bool WriteAttempted,
    string? BaselineRaw, string? RequestedRaw, string? LastRaw,
    double? BaselineAveragePowerW, double? LimitedAveragePowerW,
    double? BaselineAverageLoadPercent, double? LimitedAverageLoadPercent,
    string EnforcementAssessment, string? Error);

internal sealed class ProbeEvidence : IDisposable
{
    private readonly StreamWriter _events;
    private readonly StreamWriter _csv;
    private readonly string _directory;
    private readonly string? _activeJournal;

    internal ProbeEvidence(string directory, string? activeJournal)
    {
        _directory = directory;
        _activeJournal = activeJournal;
        _events = Open("events.txt");
        _csv = Open("samples.csv");
        _csv.WriteLine("utc,phase,raw610,pl1W,pl2W,lock,cpuPowerW,cpuTempC,cpuLoadPct,acOnline");
    }

    private StreamWriter Open(string name) => new(
        new FileStream(Path.Combine(_directory, name), FileMode.CreateNew,
            FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };

    internal void Event(string message)
    {
        // Logging failure must never prevent the finally path from releasing a
        // limit. Durable journal/summary failures still surface to the caller.
        try { _events.WriteLine($"{DateTimeOffset.UtcNow:O} {message}"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    internal void Sample(string phase, CpuObservation sample, IntelRaplUnits units)
    {
        var decoded = IntelRaplCodec.DecodePackagePowerLimit(sample.RawLimit, units);
        static string F(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
        _csv.WriteLine($"{DateTimeOffset.UtcNow:O},{phase},0x{sample.RawLimit:X16}," +
            $"{F(decoded.Pl1.PowerWatts)},{F(decoded.Pl2.PowerWatts)},{(decoded.Locked ? 1 : 0)}," +
            $"{F(sample.PowerW)},{F(sample.TemperatureC)},{F(sample.LoadPercent)},{sample.AcOnline}");
    }

    internal void Arm(object snapshot)
    {
        DurableJson(Path.Combine(_directory, "write-journal.json"), snapshot);
        if (_activeJournal is not null) DurableJson(_activeJournal, snapshot);
        Event("WRITE_ARMED: baseline and request persisted before the write attempt.");
    }

    internal void Complete(ProbeResult result)
    {
        DurableJson(Path.Combine(_directory, "summary.json"), result);
        Event($"RESULT: {result.Result}; RESTORE: {result.RestoreResult}; ENFORCEMENT: {result.EnforcementAssessment}");
        if (_activeJournal is not null && result.RestoreResult is "BASELINE_VERIFIED" or "NOT_NEEDED")
            File.Delete(_activeJournal);
    }

    internal static void DurableJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    public void Dispose() { _csv.Dispose(); _events.Dispose(); }
}

internal static class RaplProbeEngine
{
    internal static async Task<ProbeResult> RunAsync(
        IRaplProbeHardware hardware, ProbeEvidence evidence, bool writeTest,
        ProbeTiming timing, Func<bool> keepRunning, CancellationToken cancellationToken)
    {
        ulong? baseline = null;
        ulong? requested = null;
        ulong? last = null;
        var attempted = false;
        var result = "READ_ONLY_COMPLETED";
        var restoreResult = "NOT_NEEDED";
        string? error = null;
        var baselineSamples = new List<CpuObservation>();
        var limitedSamples = new List<CpuObservation>();
        var units = IntelRaplCodec.DecodeUnits(hardware.UnitsRaw);
        var stopwatch = Stopwatch.StartNew();
        // Monotonic deadline and wall-clock deadline both apply. Resume never
        // extends the write lease. Native driver calls cannot be forcibly cancelled.
        var leaseSeconds = ((timing.BaselineSamples + timing.LimitedSamples + timing.RestoredSamples) *
            timing.IntervalMs / 1000.0) + 15;
        var expiresUtc = DateTimeOffset.UtcNow.AddSeconds(leaseSeconds);

        void CheckLease()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!keepRunning()) throw new OperationCanceledException("Parent exited or Stop was requested.");
            if (stopwatch.Elapsed.TotalSeconds > leaseSeconds || DateTimeOffset.UtcNow >= expiresUtc)
                throw new OperationCanceledException("Hard lease expired.");
        }

        async Task<CpuObservation> Next(string phase)
        {
            CheckLease();
            var before = DateTimeOffset.UtcNow;
            await Task.Delay(timing.IntervalMs, cancellationToken);
            CheckLease();
            if ((DateTimeOffset.UtcNow - before).TotalMilliseconds > timing.IntervalMs + 2500)
                throw new OperationCanceledException("Suspend or excessive sampling gap detected.");
            var sample = hardware.Sample();
            evidence.Sample(phase, sample, units);
            last = sample.RawLimit;
            CheckLease();
            if (!sample.AcOnline) throw new OperationCanceledException("AC disconnected or power status unavailable.");
            if (sample.TemperatureC is null or < 10 or > 125 || (writeTest && sample.TemperatureC >= 90))
                throw new InvalidDataException("Temperature unavailable or outside the test envelope (10..89 C).");
            return sample;
        }

        try
        {
            evidence.Event($"UNITS 0x606=0x{hardware.UnitsRaw:X16}; INFO 0x614=0x{hardware.PowerInfoRaw:X16}");
            // Prime differential power and load counters; the first priming sample
            // is never used for stability/enforcement qualification.
            _ = hardware.Sample();
            for (var i = 0; i < timing.BaselineSamples; i++)
            {
                baselineSamples.Add(await Next("baseline"));
                if ((i + 1) % 10 == 0) evidence.Event($"BASELINE_OBSERVATION {i + 1}/{timing.BaselineSamples} samples");
            }
            baseline = baselineSamples[0].RawLimit;
            var decoded = IntelRaplCodec.DecodePackagePowerLimit(baseline.Value, units);
            evidence.Event($"BASELINE 0x{baseline:X16}; PL1={decoded.Pl1.PowerWatts} W; " +
                $"PL2={decoded.Pl2.PowerWatts} W; Tau1={decoded.Pl1.TimeWindowSeconds} s; " +
                $"Tau2={decoded.Pl2.TimeWindowSeconds} s; Locked={decoded.Locked}");

            if (!writeTest)
            {
                result = baselineSamples.Any(s => (s.RawLimit & (1UL << 63)) != 0)
                    ? "READ_ONLY_LOCK_OBSERVED"
                    : baselineSamples.Any(s => s.RawLimit != baseline)
                        ? "READ_ONLY_DYNAMIC_LIMITS" : "READ_ONLY_STABLE";
            }
            else
            {
                if (baselineSamples.Any(s => s.RawLimit != baseline))
                    throw new InvalidOperationException("WRITE_REFUSED_DYNAMIC_BASELINE");
                if (baselineSamples.Any(s => s.TemperatureC >= 85))
                    throw new InvalidOperationException("WRITE_REFUSED_BASELINE_TEMPERATURE_85C_OR_MORE");
                requested = RaplWritePolicy.BuildReducedLimit(baseline.Value, units);
                var info = IntelRaplCodec.DecodePackagePowerInfo(hardware.PowerInfoRaw, units);
                var target = IntelRaplCodec.DecodePackagePowerLimit(requested.Value, units);
                if (info.MinimumWatts > 0 && target.Pl1.PowerWatts < info.MinimumWatts)
                    throw new InvalidOperationException("WRITE_REFUSED_BELOW_REPORTED_MINIMUM");
                CheckLease();
                if (hardware.ReadLimit() != baseline)
                    throw new InvalidOperationException("WRITE_REFUSED_BASELINE_CHANGED_BEFORE_ARM");
                evidence.Arm(new {
                    Status = "ARMED", ProcessId = Environment.ProcessId, ExpiresUtc = expiresUtc,
                    BaselineRaw = $"0x{baseline:X16}", RequestedRaw = $"0x{requested:X16}",
                    UnitsRaw = $"0x{hardware.UnitsRaw:X16}", OwnedMask = $"0x{RaplWritePolicy.OwnedMask:X16}" });
                // A second pre-write read avoids applying a stale snapshot after
                // journal I/O. This is not an atomic CAS against OEM writers.
                CheckLease();
                if (hardware.ReadLimit() != baseline)
                    throw new InvalidOperationException("WRITE_REFUSED_BASELINE_CHANGED_AFTER_ARM");
                CheckLease();
                attempted = true; // Set BEFORE ioctl: an error may still have changed hardware.
                evidence.Event($"APPLY_ONCE 0x610=0x{requested:X16}; PL1={target.Pl1.PowerWatts} W; PL2={target.Pl2.PowerWatts} W");
                hardware.WriteLimit(requested.Value);
                last = hardware.ReadLimit();
                if (last != requested) throw new InvalidOperationException("WRITE_REJECTED_OR_IMMEDIATE_OVERRIDE");
                evidence.Event("IMMEDIATE_READBACK_EXACT_MATCH");
                for (var i = 0; i < timing.LimitedSamples; i++)
                {
                    var sample = await Next("limited");
                    limitedSamples.Add(sample);
                    if (sample.RawLimit != requested)
                        throw new InvalidOperationException("LIMIT_CHANGED_EXTERNALLY: no reapply will be attempted.");
                    if ((i + 1) % 10 == 0) evidence.Event($"LIMITED_OBSERVATION {i + 1}/{timing.LimitedSamples}; " +
                        $"CPU={sample.PowerW:0.###} W, {sample.TemperatureC:0.###} C, load={sample.LoadPercent:0.###}%");
                }
                result = "WRITE_ACCEPTED_AND_PERSISTED";
            }
        }
        catch (Exception ex)
        {
            result = ex is OperationCanceledException ? "ABORTED" : attempted ? "WRITE_TEST_FAILED" : "WRITE_REFUSED_OR_READ_FAILED";
            error = $"{ex.GetType().Name}: {ex.Message}";
            evidence.Event(error);
        }
        finally
        {
            // Restoration ignores caller cancellation, lost AC and the temperature
            // gate: those conditions should trigger release, not prevent it.
            if (attempted && baseline.HasValue && requested.HasValue)
            {
                restoreResult = "RESTORE_UNCONFIRMED";
                try
                {
                    var current = hardware.ReadLimit();
                    var plan = RaplWritePolicy.PlanRestore(baseline.Value, requested.Value, current);
                    evidence.Event($"RESTORE_PLAN {plan.Status}; current=0x{current:X16}; next=0x{plan.Value:X16}");
                    if (plan.Value != current)
                    {
                        if (hardware.ReadLimit() != current)
                            throw new InvalidOperationException("RESTORE_REFUSED_CONCURRENT_CHANGE");
                        hardware.WriteLimit(plan.Value);
                    }
                    last = hardware.ReadLimit();
                    restoreResult = last == baseline ? "BASELINE_VERIFIED" : plan.Status;
                    if (last != plan.Value) restoreResult = "RESTORE_READBACK_MISMATCH";
                    evidence.Event($"RESTORE_READBACK 0x{last:X16}; {restoreResult}");
                }
                catch (Exception ex)
                {
                    error = (error is null ? "" : error + " | ") + "Restore: " + ex.Message;
                    evidence.Event("RESTORE_UNCONFIRMED: " + ex.Message);
                }
            }
        }

        if (attempted && restoreResult == "BASELINE_VERIFIED")
        {
            // Additional read-only observation reports whether HP changes the
            // baseline again. It never repeats a restore/write.
            try
            {
                for (var i = 0; i < timing.RestoredSamples; i++)
                {
                    if (!keepRunning()) break;
                    await Task.Delay(timing.IntervalMs, CancellationToken.None);
                    var sample = hardware.Sample();
                    evidence.Sample("restored", sample, units);
                    last = sample.RawLimit;
                    if (last != baseline)
                    {
                        restoreResult = "BASELINE_VERIFIED_THEN_EXTERNAL_CHANGE";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                restoreResult = "BASELINE_VERIFIED_POSTCHECK_FAILED";
                error = (error is null ? "" : error + " | ") + "Postcheck: " + ex.Message;
            }
        }

        static double? Average(IEnumerable<double?> values)
        {
            var valid = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            return valid.Length == 0 ? null : valid.Average();
        }
        var bp = Average(baselineSamples.Select(s => s.PowerW));
        var lp = Average(limitedSamples.TakeLast(Math.Min(10, limitedSamples.Count)).Select(s => s.PowerW));
        var bl = Average(baselineSamples.Select(s => s.LoadPercent));
        var ll = Average(limitedSamples.TakeLast(Math.Min(10, limitedSamples.Count)).Select(s => s.LoadPercent));
        // A drop is observational evidence, not proof of MSR-only enforcement:
        // thermal throttling, OEM/MMIO limits and workload changes can coexist.
        var assessment = attempted && bp.HasValue && lp.HasValue && bl >= 60 && ll >= 60 &&
            Math.Abs(bl.Value - ll.Value) <= 10 && lp < bp * 0.9
                ? "POWER_DROP_OBSERVED_UNDER_COMPARABLE_LOAD__CAUSAL_REVIEW_REQUIRED"
                : "INCONCLUSIVE__REVIEW_SAMPLES_WORKLOAD_AND_TAU";
        var summary = new ProbeResult(result, restoreResult, attempted,
            baseline.HasValue ? $"0x{baseline:X16}" : null,
            requested.HasValue ? $"0x{requested:X16}" : null,
            last.HasValue ? $"0x{last:X16}" : null,
            bp, lp, bl, ll, assessment, error);
        evidence.Complete(summary);
        return summary;
    }
}
