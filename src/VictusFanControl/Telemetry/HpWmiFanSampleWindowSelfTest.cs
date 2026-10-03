using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

/// <summary>Fake clock/transport only. No hardware or physical authorization.</summary>
internal static class HpWmiFanSampleWindowSelfTest
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(TextWriter output)
    {
        var passed = 0;
        async Task Test(string name, Func<Task> test)
        {
            await test();
            await output.WriteLineAsync($"PASS: window {name}");
            passed++;
        }
        Task Sync(Action action) { action(); return Task.CompletedTask; }
        try
        {
            await Test("empty, partial, full and bounded rollover retain acquisition metadata", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                Check(window.ReadSnapshot(0) is null, "empty window produced RPM");
                for (var i = 1; i <= 8; i++)
                {
                    var sample = Sample((byte)i, (byte)(10 + i), i * 100);
                    Check(window.TryAppend(sample, i, HpWmiFanAcquisitionPurpose.Periodic, i * 100), "append failed");
                    var view = View(window, i * 100 + 25);
                    Check(view.WindowCount == Math.Min(i, 5) && ReferenceEquals(view.RawLatest, sample) &&
                        view.LatestAgeMilliseconds == 25 && view.RawLatest.SampledAtUtc == sample.SampledAtUtc,
                        "count/raw acquisition metadata changed");
                }
                Check(View(window, 800).Entries.Select(entry => entry.PublicationSequence).SequenceEqual(new long[] { 4, 5, 6, 7, 8 }),
                    "rollover retained wrong acquisitions");
            }));

            await Test("independent CPU/GPU medians reject a valid outlier without changing raw", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                var cpu = new byte[] { 30, 31, 90, 32, 33 };
                var gpu = new byte[] { 44, 43, 0, 42, 41 };
                for (var i = 0; i < 5; i++) window.TryAppend(Sample(cpu[i], gpu[i], i * 100), i + 1, HpWmiFanAcquisitionPurpose.Control, i * 100);
                var view = View(window, 400);
                Check(view.StableCpuRpm == 3200 && view.StableGpuRpm == 4200 &&
                    view.RawLatest.CpuNominalRpm == 3300 && view.RawLatest.GpuNominalRpm == 4100,
                    "median contaminated raw or combined fan channels");
            }));

            await Test("partial even median preserves half-step precision and zero", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                window.TryAppend(Sample(0, 100, 0), 1, HpWmiFanAcquisitionPurpose.Periodic, 0);
                Check(View(window, 0).StableCpuRpm == 0, "Firmware zero rejected");
                window.TryAppend(Sample(1, 99, 1), 2, HpWmiFanAcquisitionPurpose.Periodic, 1);
                Check(View(window, 1) is { StableCpuRpm: 50, StableGpuRpm: 9950, WindowCount: 2 }, "even median rounded to a raw byte");
            }));

            await Test("duplicate, delayed, future, stale and invalid acquisitions cannot enter", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                var sample = Sample(30, 31, 100);
                window.TryAppend(sample, 2, HpWmiFanAcquisitionPurpose.Control, 100);
                Check(!window.TryAppend(sample, 2, HpWmiFanAcquisitionPurpose.Control, 101) &&
                    !window.TryAppend(sample, 1, HpWmiFanAcquisitionPurpose.Control, 101) &&
                    !window.TryAppend(Sample(30, 31, 99), 3, HpWmiFanAcquisitionPurpose.Control, 101) &&
                    !window.TryAppend(Sample(30, 31, 102), 3, HpWmiFanAcquisitionPurpose.Control, 101) &&
                    !window.TryAppend(Sample(30, 31, 100), 3, HpWmiFanAcquisitionPurpose.Control, 3100) &&
                    !window.TryAppend(Sample(255, 31, 101), 3, HpWmiFanAcquisitionPurpose.Control, 101) &&
                    !window.TryAppend(Sample(30, 101, 101), 3, HpWmiFanAcquisitionPurpose.Control, 101),
                    "invalid/replayed sample entered history");
                Check(View(window, 101).WindowCount == 1, "rejection changed good history");
                Check(window.TryAppend(Sample(32, 33, 100), 3, HpWmiFanAcquisitionPurpose.Control, 101),
                    "distinct query with equal monotonic tick rejected");
            }));

            await Test("latest age uses original start and expires exactly at 3000 ms", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                for (var i = 0; i < 5; i++) window.TryAppend(Sample(30, 31, i * 100), i + 1, HpWmiFanAcquisitionPurpose.Periodic, i * 100);
                var view = View(window, 3399);
                Check(view.LatestAgeMilliseconds == 2999 && view.WindowCount == 5, "history lost real newest age");
                Check(window.ReadSnapshot(3400) is null && window.ReadSnapshot(3401) is null, "five old samples fabricated freshness");
                Check(window.TryAppend(Sample(40, 41, 3401), 6, HpWmiFanAcquisitionPurpose.Periodic, 3401) &&
                    View(window, 3401).WindowCount == 1, "recovery reused expired history");
            }));

            await Test("freshness gap and clock reversal clear rather than bridge history", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                window.TryAppend(Sample(30, 31, 100), 1, HpWmiFanAcquisitionPurpose.Periodic, 100);
                window.TryAppend(Sample(40, 41, 3100), 2, HpWmiFanAcquisitionPurpose.Control, 3100);
                Check(View(window, 3100).WindowCount == 1, "unobserved expiry gap bridged");
                Check(window.ReadSnapshot(3099) is null, "negative newest age accepted");
                Check(!window.TryAppend(Sample(40, 41, 3100), 2, HpWmiFanAcquisitionPurpose.Control, 3100), "clear reset replay watermark");
            }));

            await Test("snapshot copies remain immutable after rollover and clear", () => Sync(() =>
            {
                var window = new HpWmiFanSampleWindow();
                window.TryAppend(Sample(30, 31, 0), 1, HpWmiFanAcquisitionPurpose.Periodic, 0);
                var held = View(window, 0);
                for (var i = 2; i <= 7; i++) window.TryAppend(Sample(40, 41, i), i, HpWmiFanAcquisitionPurpose.Control, i);
                window.Clear();
                Check(held.WindowCount == 1 && held.RawLatest.CpuNominalRpm == 3000 && held.LatestAgeMilliseconds == 0,
                    "later writes mutated held snapshot");
                var list = (IList<HpWmiFanWindowEntry>)held.Entries;
                Check(list.IsReadOnly, "snapshot exposes mutable entries");
                try { list.Clear(); throw new Exception("entries were mutable"); }
                catch (NotSupportedException) { }
            }));

            await Test("Periodic and Control fill one window, cached reads add no samples or I/O", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                var calls = 0;
                HpBiosResponse Send(HpBiosRequest _) { Interlocked.Increment(ref calls); return new(0, [30, 31]); }
                using var reader = Reader(slot, () => Interlocked.Read(ref clock), Send);
                reader.ReadCached();
                await reader.PendingQuery.WaitAsync(Deadline);
                var proof = new HpWmiFanProofReader(Send, slot, () => Interlocked.Read(ref clock), Deadline);
                HpWmiFanProofSample? previous = null;
                for (var i = 1; i <= 6; i++)
                {
                    Interlocked.Exchange(ref clock, i * 100);
                    var fresh = await proof.ReadFreshAsync(CancellationToken.None);
                    Check(fresh.Sequence > (previous?.Sequence ?? 0), "proof identity reused");
                    previous = fresh;
                    if (i == 1)
                        Check(reader.ReadWindowCached()?.Entries.Select(entry => entry.Purpose)
                            .SequenceEqual(new[] { HpWmiFanAcquisitionPurpose.Periodic, HpWmiFanAcquisitionPurpose.Control }) == true,
                            "Periodic and Control did not share a window");
                    Check(ReferenceEquals(reader.ReadWindowCached()?.RawLatest, fresh.Speeds) &&
                        ReferenceEquals(reader.ReadCached(), fresh.Speeds), "raw acquisition replaced by filter");
                }
                var view = reader.ReadWindowCached() ?? throw new Exception("window missing");
                Check(view.WindowCount == 5 && calls == 7 && view.Entries.All(entry => entry.Purpose == HpWmiFanAcquisitionPurpose.Control), "shared history/eviction incorrect");
                for (var i = 0; i < 20; i++) { reader.ReadWindowCached(); reader.ReadCached(); }
                Check(calls == 7 && reader.ReadWindowCached()?.WindowCount == 5, "cached views became physical acquisitions");
                Interlocked.Exchange(ref clock, 3600);
                Check(reader.ReadWindowCached() is null, "history hid 3000-ms raw expiry");
            });

            await Test("failure clears history, recovery starts at one real acquisition", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                var calls = 0;
                using var reader = Reader(slot, () => Interlocked.Read(ref clock), _ =>
                    Interlocked.Increment(ref calls) == 2 ? new(7, []) : new(0, [30, 31]));
                reader.ReadCached(); await reader.PendingQuery.WaitAsync(Deadline);
                Check(reader.ReadWindowCached()?.WindowCount == 1, "first sample absent");
                Interlocked.Exchange(ref clock, 1000);
                reader.ReadCached(); await reader.PendingQuery.WaitAsync(Deadline);
                Check(reader.ReadWindowCached() is null && reader.ReadCached() is null, "failed response retained history");
                Interlocked.Exchange(ref clock, 6000);
                reader.ReadCached(); await reader.PendingQuery.WaitAsync(Deadline);
                Check(reader.ReadWindowCached()?.WindowCount == 1 && reader.Recoveries == 1, "history mixed pre-failure epoch");
            });

            await Test("pause, dispose and replacement reject pre-boundary publication", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var reader = Reader(slot, () => 0, _ => new(0, [30, 31]));
                reader.ReadCached(); await reader.PendingQuery.WaitAsync(Deadline);
                var sink = HpWmiFanSamplePublication.Capture(slot);
                reader.Pause(); sink(Sample(40, 41, 0));
                Check(reader.ReadWindowCached() is null && reader.ReadCached() is null, "pause retained old window");
                using var replacement = Reader(slot, () => 0, _ => new(0, [30, 31]));
                sink(Sample(40, 41, 0));
                Check(replacement.ReadWindowCached() is null, "replacement inherited pre-registration sample");
                replacement.ReadCached(); await replacement.PendingQuery.WaitAsync(Deadline);
                Check(replacement.ReadWindowCached()?.WindowCount == 1, "replacement not independent");
                var disposeSink = HpWmiFanSamplePublication.Capture(slot);
                replacement.Dispose(); disposeSink(Sample(40, 41, 0));
                Check(replacement.ReadWindowCached() is null, "dispose resurrected history");
            });
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"FAIL: HP WMI sample window: {ex}");
            return 43;
        }
        await output.WriteLineAsync($"HP WMI sample window: PASS ({passed} cases; no hardware access).");
        return 0;
    }

    private static HpWmiFanTelemetrySample Sample(byte cpu, byte gpu, long started) =>
        new(cpu, gpu, DateTimeOffset.UnixEpoch.AddMilliseconds(started), started);
    private static HpWmiFanWindowSnapshot View(HpWmiFanSampleWindow window, long now) =>
        window.ReadSnapshot(now) ?? throw new Exception("expected window missing");
    private static HpWmiFanTelemetryReader Reader(SemaphoreSlim slot, Func<long> clock,
        Func<HpBiosRequest, HpBiosResponse> send) => new(send, clock, () => DateTimeOffset.UnixEpoch, slot);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
