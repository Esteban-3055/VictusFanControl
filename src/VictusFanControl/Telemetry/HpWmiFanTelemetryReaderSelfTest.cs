using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;

namespace VictusFanControl.Telemetry;

/// <summary>Fake transport + monotonic clock only; no WMI, PawnIO or fan writes.</summary>
public static class HpWmiFanTelemetryReaderSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var passed = 0;
        async Task Test(string name, Func<Task> test)
        {
            await test();
            await output.WriteLineAsync($"PASS: {name}");
            passed++;
        }

        try
        {
            await Test("read-only envelope, quantization, asymmetry and cadence", async () =>
            {
                var clock = new FakeClock();
                using var slot = new SemaphoreSlim(1, 1);
                var calls = 0;
                using var reader = Create(clock, slot, request =>
                {
                    Check(request.Command == 0x20008 && request.CommandType == 0x2D &&
                        request.OutputSize == 128 && request.Payload.SequenceEqual(new byte[4]), "read envelope changed");
                    Interlocked.Increment(ref calls);
                    return new(0, [26, 24]);
                });
                reader.ReadCached();
                await Drain(reader);
                var sample = reader.ReadCached();
                Check(sample is { CpuNominalRpm: 2600, GpuNominalRpm: 2400 } &&
                    sample.CpuSpeedLevel == 26 && sample.GpuSpeedLevel == 24, "quantized speed incorrect");
                clock.Set(999);
                reader.ReadCached();
                Check(calls == 1, "cadence was exceeded");
                clock.Set(1000);
                reader.ReadCached();
                await Drain(reader);
                Check(calls == 2, "due poll missing");
            });

            await Test("response validation including zero, high RPM and 0xFF", () =>
            {
                var date = DateTimeOffset.UtcNow;
                foreach (var bytes in new byte[][] { [0, 0], [10, 50], [100, 99] })
                    HpWmiFanTelemetryReader.Decode(new(0, bytes), date, 0);
                foreach (var response in new HpBiosResponse[]
                    { new(1, [26, 24]), new(0, []), new(0, [26]), new(0, [255, 24]), new(0, [26, 101]) })
                {
                    try
                    {
                        HpWmiFanTelemetryReader.Decode(response, date, 0);
                        throw new Exception("invalid response accepted");
                    }
                    catch (InvalidDataException) { }
                }
                return Task.CompletedTask;
            });

            await Test("query failure invalidates cached pair, backoff, recovery", async () =>
            {
                var clock = new FakeClock();
                using var slot = new SemaphoreSlim(1, 1);
                var calls = 0;
                using var reader = Create(clock, slot, _ =>
                    Interlocked.Increment(ref calls) == 2 ? new(7, []) : new(0, [30, 29]));
                reader.ReadCached();
                await Drain(reader);
                clock.Set(1000);
                reader.ReadCached();
                await Drain(reader);
                Check(reader.ReadCached() is null, "failed query retained healthy cached fans");
                clock.Set(5999);
                reader.ReadCached();
                Check(calls == 2, "failure backoff missing");
                clock.Set(6000);
                reader.ReadCached();
                await Drain(reader);
                Check(reader.ReadCached() is not null && reader.Recoveries == 1, "recovery not observable");
            });

            await Test("age is measured from query start; expiry makes snapshot incomplete", async () =>
            {
                var clock = new FakeClock();
                using var slot = new SemaphoreSlim(1, 1);
                using var reader = Create(clock, slot, _ => { clock.Set(2500); return new(0, [26, 24]); });
                reader.ReadCached();
                await Drain(reader);
                clock.Set(2999);
                Check(reader.ReadCached() is { StartedAtMilliseconds: 0 }, "query latency disguised sample age");
                clock.Set(3000);
                Check(reader.ReadCached() is null, "sample accepted at expiry boundary");
                var snapshot = new TelemetrySnapshot(DateTimeOffset.UtcNow, "CPU", 40, 10, 5,
                    "GPU", 40, 10, 5, 2600, 2400)
                {
                    CpuCoreTemperatures = [new(0, 0, "Performance", 40)],
                    CpuExpectedPhysicalCoreCount = 1,
                    FanTelemetrySource = "HP-WMI-ACPI-2D",
                    FanSampledAtUtc = DateTimeOffset.UtcNow,
                    FanRpmResolution = 100,
                    FanSampleAgeMilliseconds = 2999
                };
                Check(snapshot.IsComplete && !(snapshot with { FanSampleAgeMilliseconds = 3000 }).IsComplete,
                    "snapshot freshness boundary incorrect");
            });

            await Test("SafetyGate rejects fan acquisition that expires while a snapshot is held", () =>
            {
                var timestamp = DateTimeOffset.UtcNow;
                var snapshot = new TelemetrySnapshot(timestamp, "CPU", 40, 10, 5,
                    Hp8C40TargetProfile.ExpectedGpuName, 40, 10, 5, 2600, 2400)
                {
                    CpuCoreTemperatures = Enumerable.Range(0, 14)
                        .Select(i => new CpuCoreTemperatureSample(i, i, "Performance", 40)).ToArray(),
                    CpuExpectedPhysicalCoreCount = 14,
                    FanTelemetrySource = "HP-WMI-ACPI-2D",
                    FanSampledAtUtc = timestamp - TimeSpan.FromMilliseconds(2500),
                    FanRpmResolution = 100,
                    FanSampleAgeMilliseconds = 2500
                };
                var hardware = new HardwareIdentity(
                    Hp8C40TargetProfile.BoardManufacturer, Hp8C40TargetProfile.BoardProduct,
                    Hp8C40TargetProfile.BoardVersion, Hp8C40TargetProfile.SystemManufacturer,
                    Hp8C40TargetProfile.SystemProductName, Hp8C40TargetProfile.SystemSkuPrefix,
                    Hp8C40TargetProfile.ValidatedBiosVersion);
                var fresh = SafetyGate.Evaluate(hardware, SystemState.Healthy, snapshot,
                    timestamp + TimeSpan.FromMilliseconds(499), fanWritePathPresent: true);
                var expired = SafetyGate.Evaluate(hardware, SystemState.Healthy, snapshot,
                    timestamp + TimeSpan.FromMilliseconds(500), fanWritePathPresent: true);
                Check(fresh.CustomControlPermitted && snapshot.IsComplete &&
                    !expired.SnapshotFresh && !expired.CustomControlPermitted &&
                    expired.Reasons.Any(r => r.Contains("fan acquisition expired", StringComparison.Ordinal)),
                    "fresh CPU/GPU snapshot renewed expired fan acquisition");
                return Task.CompletedTask;
            });

            await Test("ded92d capture: sampling duration is not counted twice in fan age", () =>
            {
                var started = DateTimeOffset.Parse("2026-10-04T05:03:57.5890563Z");
                var captured = DateTimeOffset.Parse("2026-10-04T05:03:58.0939804Z");
                var snapshot = new TelemetrySnapshot(started, "CPU", 54, 16.854, 8.775,
                    Hp8C40TargetProfile.ExpectedGpuName, 55, 1.2, 0, 2600, 2400)
                {
                    CpuCoreTemperatures = Enumerable.Range(0, 14)
                        .Select(i => new CpuCoreTemperatureSample(i, i, "Performance", 55)).ToArray(),
                    CpuExpectedPhysicalCoreCount = 14,
                    FanTelemetrySource = "HP-WMI-ACPI-2D", FanRpmResolution = 100,
                    FanSampledAtUtc = DateTimeOffset.Parse("2026-10-04T05:03:55.5666476Z"),
                    FanSampleAgeMilliseconds = 2531, FanAgeCapturedAtUtc = captured
                };
                var hardware = new HardwareIdentity(
                    Hp8C40TargetProfile.BoardManufacturer, Hp8C40TargetProfile.BoardProduct,
                    Hp8C40TargetProfile.BoardVersion, Hp8C40TargetProfile.SystemManufacturer,
                    Hp8C40TargetProfile.SystemProductName, Hp8C40TargetProfile.SystemSkuPrefix,
                    Hp8C40TargetProfile.ValidatedBiosVersion);
                var now = captured.AddMilliseconds(2);
                Check(!SafetyGate.Evaluate(hardware, SystemState.Healthy, snapshot with { FanAgeCapturedAtUtc = null }, now, true).CustomControlPermitted,
                    "Fixture must reproduce the old false expiry.");
                Check(SafetyGate.Evaluate(hardware, SystemState.Healthy, snapshot, now, true).CustomControlPermitted,
                    "Sampling duration was added to an age already measured after sampling.");
                Check(snapshot.IsFanTelemetryFreshAt(captured.AddMilliseconds(468)) &&
                    !snapshot.IsFanTelemetryFreshAt(captured.AddMilliseconds(469)),
                    "Corrected epoch must still expire exactly at 3000 ms.");
                Check(!snapshot.IsFanTelemetryFreshAt(captured.AddMilliseconds(-1)) &&
                    !(snapshot with { FanAgeCapturedAtUtc = started.AddMilliseconds(-1) }).IsFanTelemetryFreshAt(now),
                    "Future or inconsistent age capture epochs may not be accepted.");
                var oldCpu = snapshot with { Timestamp = captured.AddSeconds(-4), FanSampleAgeMilliseconds = 0 };
                Check(!SafetyGate.Evaluate(hardware, SystemState.Healthy, oldCpu, captured, true).CustomControlPermitted,
                    "Fresh fan epoch may not renew old CPU/GPU telemetry.");
                return Task.CompletedTask;
            });

            await Test("slow completion is discarded even without a foreground timeout check", async () =>
            {
                foreach (var late in new long[] { 3000, 5000 })
                {
                    var clock = new FakeClock();
                    using var slot = new SemaphoreSlim(1, 1);
                    using var reader = Create(clock, slot, _ => { clock.Set(late); return new(0, [26, 24]); });
                    reader.ReadCached();
                    await Drain(reader);
                    Check(reader.ReadCached() is null, "late completion published");
                    Check(reader.ReadWindowCached() is null, "late completion entered history");
                }
            });

            await Test("blocked query never blocks snapshots or overlaps after timeout/recreation", async () =>
            {
                var clock = new FakeClock();
                using var slot = new SemaphoreSlim(1, 1);
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var reader = Create(clock, slot, _ =>
                {
                    entered.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(5)), "test release timed out");
                    return new(0, [26, 24]);
                });
                reader.ReadCached();
                Check(entered.Wait(TimeSpan.FromSeconds(3)), "test worker did not start");
                try
                {
                    clock.Set(5000);
                    for (var i = 0; i < 100; i++) Check(reader.ReadCached() is null, "timeout served healthy RPM");
                    Check(reader.Diagnostic.Contains("timed out", StringComparison.Ordinal), "timeout not observable");
                    reader.Dispose();
                    var replacementCalls = 0;
                    using var replacement = Create(clock, slot, _ => { replacementCalls++; return new(0, [30, 30]); });
                    replacement.ReadCached();
                    Check(replacementCalls == 0 && replacement.PendingQuery.IsCompleted, "reader recreation bypassed pending slot");
                    release.Set();
                    await Drain(reader);
                    Check(reader.ReadCached() is null, "disposed reader republished late result");
                    clock.Set(6000);
                    replacement.ReadCached();
                    await Drain(replacement);
                    Check(replacementCalls == 1 && replacement.ReadCached() is not null, "replacement never recovered");
                }
                finally { release.Set(); await Drain(reader); }
            });

            await Test("suspend invalidates in-flight results and quiescence tracks WMI", async () =>
            {
                var clock = new FakeClock();
                using var slot = new SemaphoreSlim(1, 1);
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                using var reader = Create(clock, slot, _ =>
                {
                    entered.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(5)), "test release timed out");
                    return new(0, [26, 24]);
                });
                reader.ReadCached();
                Check(entered.Wait(TimeSpan.FromSeconds(3)), "test worker did not start");
                try
                {
                    reader.Pause();
                    using var cts = new CancellationTokenSource();
                    var wait = reader.WaitForQuiescenceAsync(cts.Token);
                    Check(!wait.IsCompleted, "quiescence ignored pending WMI call");
                    cts.Cancel();
                    try { await wait; throw new Exception("cancelled quiescence accepted"); }
                    catch (OperationCanceledException) { }
                    release.Set();
                    await Drain(reader);
                    Check(reader.ReadCached() is null, "pre-suspend result crossed epoch");
                    Check(reader.ReadWindowCached() is null, "pre-suspend history crossed epoch");
                    await reader.WaitForQuiescenceAsync(CancellationToken.None);
                }
                finally { release.Set(); await Drain(reader); }
            });

            await output.WriteLineAsync($"HP WMI fan telemetry: {passed} cases PASS (no hardware access).");
            return 0;
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"FAIL: HP WMI fan telemetry: {ex}");
            return 40;
        }
    }

    private static HpWmiFanTelemetryReader Create(FakeClock clock, SemaphoreSlim slot,
        Func<HpBiosRequest, HpBiosResponse> send) =>
        new(send, () => clock.Now, () => DateTimeOffset.UnixEpoch.AddMilliseconds(clock.Now), slot);

    private static Task Drain(HpWmiFanTelemetryReader reader) =>
        reader.PendingQuery.WaitAsync(TimeSpan.FromSeconds(4));

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeClock
    {
        private long _now;
        public long Now => Interlocked.Read(ref _now);
        public void Set(long now) => Interlocked.Exchange(ref _now, now);
    }
}
