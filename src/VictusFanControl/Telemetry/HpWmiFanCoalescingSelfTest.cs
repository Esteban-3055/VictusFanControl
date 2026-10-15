using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

/// <summary>Concurrency gates and fake BIOS replies only; no hardware or WMI access.</summary>
internal static class HpWmiFanCoalescingSelfTest
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    internal static async Task<int> RunAsync(TextWriter output)
    {
        var passed = 0;
        async Task Test(string name, Func<Task> test)
        { await test(); passed++; await output.WriteLineAsync("PASS: coalescing " + name); }
        try
        {
            await Test("16 queued callers share one raw acquisition and one publication", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1);
                var calls = 0;
                using var telemetry = Telemetry(slot);
                HpBiosResponse Send(HpBiosRequest r)
                {
                    Check(r.CommandType == 0x2D && r.OutputSize == 128, "read envelope changed");
                    Interlocked.Increment(ref calls); return new(0, [30, 31]);
                }
                var readers = Enumerable.Range(0, 16).Select(_ => Reader(slot, Send)).ToArray();
                var pending = readers.Select(r => r.ReadFreshAsync(default).AsTask()).ToArray();
                Check(calls == 0 && pending.All(p => !p.IsCompleted), "queued work dispatched early");
                slot.Release();
                var samples = await Task.WhenAll(pending).WaitAsync(Deadline);
                Check(calls == 1 && samples.All(s => ReferenceEquals(s, samples[0])), "duplicate native acquisitions");
                Check(telemetry.ReadWindowCached()?.WindowCount == 1 &&
                    ReferenceEquals(telemetry.ReadCached(false), samples[0].Speeds), "duplicate publication or timestamp replacement");
                var broker = HpWmiFanSampleBroker.For(slot);
                Check(broker.ControlRequests == 16 && broker.CoalescedControlRequests == 15 &&
                    broker.Diagnostics.Counters.ControlNativeStarts == 1, "caller/native counters are not distinct");
                var next = await readers[0].ReadFreshAsync(default);
                Check(calls == 2 && next.Sequence > samples[0].Sequence, "completed sample reused as fresh proof");
            });
            await Test("a caller after native start gets a new acquisition; later queued callers may join it", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var release = new ManualResetEventSlim();
                var entered = Signal(); var calls = 0; var active = 0; var peak = 0;
                HpBiosResponse Send(HpBiosRequest _)
                {
                    var n = Interlocked.Increment(ref active); peak = Math.Max(peak, n);
                    try
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        { entered.SetResult(); Check(release.Wait(Deadline), "native test gate expired"); }
                        return new(0, [30, 31]);
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
                var first = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                try
                {
                    await entered.Task.WaitAsync(Deadline);
                    var second = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                    var third = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                    Check(calls == 1 && !second.IsCompleted && !third.IsCompleted, "new caller reused running acquisition");
                    release.Set();
                    var samples = await Task.WhenAll(first, second, third).WaitAsync(Deadline);
                    Check(calls == 2 && peak == 1 && samples[1].Sequence > samples[0].Sequence &&
                        ReferenceEquals(samples[1], samples[2]), "query-start boundary or serialization lost");
                }
                finally { release.Set(); await first.WaitAsync(Deadline); }
            });
            await Test("canceling the leader does not cancel a remaining follower", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1);
                using var cancel = new CancellationTokenSource(); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _) { Interlocked.Increment(ref calls); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(cancel.Token).AsTask();
                var second = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                cancel.Cancel(); await Canceled(first);
                Check(!second.IsCompleted && calls == 0, "leader cancellation escaped to follower");
                slot.Release(); var result = await second.WaitAsync(Deadline);
                Check(calls == 1 && result.Speeds.CpuSpeedLevel == 30, "remaining follower lost its acquisition");
            });
            await Test("all callers canceled before dispatch produce no hardware request", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1);
                using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource(); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _) { Interlocked.Increment(ref calls); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(a.Token).AsTask();
                var second = Reader(slot, Send).ReadFreshAsync(b.Token).AsTask();
                a.Cancel(); b.Cancel(); await Canceled(first); await Canceled(second);
                slot.Release(); await HpWmiFanSampleBroker.For(slot).WaitForQuiescenceAsync(default).WaitAsync(Deadline);
                Check(calls == 0 && slot.CurrentCount == 1, "canceled queued batch dispatched or released another owner");
                await Reader(slot, Send).ReadFreshAsync(default);
                Check(calls == 1, "canceled batch prevented a new acquisition");
            });
            await Test("a short follower deadline does not cancel the longer leader", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _) { Interlocked.Increment(ref calls); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                var second = new HpWmiFanProofReader(Send, slot, () => 0, TimeSpan.FromMilliseconds(75)).ReadFreshAsync(default).AsTask();
                var timedOut = false;
                try { await second.WaitAsync(Deadline); } catch (TimeoutException) { timedOut = true; }
                Check(timedOut && !first.IsCompleted && calls == 0, "follower deadline changed shared lifetime");
                slot.Release(); await first.WaitAsync(Deadline);
                Check(calls == 1, "short deadline caused duplicate acquisition");
            });
            await Test("native faults, invalid replies and expiry fan out without cache fallback", async () =>
            {
                foreach (var mode in new[] { "failure", "invalid", "expired" })
                {
                    using var slot = new SemaphoreSlim(0, 1); long clock = 0; var calls = 0;
                    using var telemetry = new HpWmiFanTelemetryReader(_ => throw new Exception("unexpected periodic read"),
                        () => clock, () => DateTimeOffset.UnixEpoch, slot);
                    HpBiosResponse Send(HpBiosRequest _)
                    {
                        Interlocked.Increment(ref calls);
                        if (mode == "failure") throw new IOException("synthetic provider failure");
                        if (mode == "expired") Interlocked.Exchange(ref clock, 3000);
                        return new(0, mode == "invalid" ? [255, 31] : [30, 31]);
                    }
                    var pending = Enumerable.Range(0, 3).Select(_ => new HpWmiFanProofReader(Send, slot,
                        () => Interlocked.Read(ref clock), Deadline).ReadFreshAsync(default).AsTask()).ToArray();
                    slot.Release();
                    foreach (var task in pending)
                    {
                        var rejected = false;
                        try { await task.WaitAsync(Deadline); }
                        catch (Exception ex) when (ex is IOException or InvalidDataException) { rejected = true; }
                        Check(rejected, mode + " supplied a proof");
                    }
                    Check(calls == 1 && telemetry.ReadCached(false) is null && slot.CurrentCount == 1,
                        mode + " retried silently, published bad data or leaked admission");
                }
            });
            await Test("all waiters cancel during native work without freeing its slot or publishing late", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1); using var release = new ManualResetEventSlim();
                using var a = new CancellationTokenSource(); using var b = new CancellationTokenSource();
                using var telemetry = Telemetry(slot); var entered = Signal(); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _)
                { Interlocked.Increment(ref calls); entered.TrySetResult(); Check(release.Wait(Deadline), "native test gate expired"); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(a.Token).AsTask();
                var second = Reader(slot, Send).ReadFreshAsync(b.Token).AsTask();
                slot.Release();
                try
                {
                    await entered.Task.WaitAsync(Deadline); a.Cancel(); b.Cancel();
                    await Canceled(first); await Canceled(second);
                    var replacement = new HpWmiFanProofReader(Send, slot, () => 0, TimeSpan.FromMilliseconds(75));
                    var timedOut = false;
                    try { await replacement.ReadFreshAsync(default); } catch (TimeoutException) { timedOut = true; }
                    Check(timedOut && calls == 1 && slot.CurrentCount == 0, "last waiter freed active native ownership");
                    release.Set(); await HpWmiFanSampleBroker.For(slot).WaitForQuiescenceAsync(default).WaitAsync(Deadline);
                    Check(telemetry.ReadCached(false) is null, "canceled shared work published a late result");
                    await Reader(slot, Send).ReadFreshAsync(default);
                    Check(calls == 2, "fresh recovery was not admitted after real native completion");
                }
                finally { release.Set(); }
            });
            await Test("each recipient rechecks sample age at delivery", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _) { Interlocked.Increment(ref calls); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                // This clock is used only by the follower's delivery check: model
                // a recipient resumed at the exact 3-second expiry boundary.
                var delayed = new HpWmiFanProofReader(Send, slot, () => 3000, Deadline).ReadFreshAsync(default).AsTask();
                slot.Release(); var sample = await first.WaitAsync(Deadline);
                var rejected = false;
                try { await delayed.WaitAsync(Deadline); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && calls == 1 && sample.Speeds.StartedAtMilliseconds == 0,
                    "delayed delivery renewed the timestamp or supplied expired proof");
            });
            await Test("publication cannot cross a paused/recreated reader epoch", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1); using var release = new ManualResetEventSlim();
                using var old = Telemetry(slot); var entered = Signal(); var calls = 0;
                HpBiosResponse Send(HpBiosRequest _)
                { Interlocked.Increment(ref calls); entered.SetResult(); Check(release.Wait(Deadline), "native test gate expired"); return new(0, [30, 31]); }
                var first = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                var second = Reader(slot, Send).ReadFreshAsync(default).AsTask();
                slot.Release();
                try
                {
                    await entered.Task.WaitAsync(Deadline); old.Pause();
                    using var replacement = Telemetry(slot);
                    release.Set(); await Task.WhenAll(first, second).WaitAsync(Deadline);
                    Check(calls == 1 && old.ReadCached(false) is null && replacement.ReadCached(false) is null,
                        "shared publication crossed lifecycle or reader replacement boundary");
                }
                finally { release.Set(); await first.WaitAsync(Deadline); }
            });
        }
        catch (Exception ex) { await output.WriteLineAsync("FAIL: WMI read coalescing: " + ex); return 43; }
        await output.WriteLineAsync($"HP WMI read coalescing: PASS ({passed} cases; no hardware access).");
        return 0;
    }
    private static HpWmiFanProofReader Reader(SemaphoreSlim slot, Func<HpBiosRequest, HpBiosResponse> send) =>
        new(send, slot, () => 0, Deadline);
    private static HpWmiFanTelemetryReader Telemetry(SemaphoreSlim slot) =>
        new(_ => throw new Exception("unexpected periodic read"), () => 0, () => DateTimeOffset.UnixEpoch, slot);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Canceled(Task task)
    {
        var canceled = false;
        try { await task.WaitAsync(Deadline); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "caller cancellation was lost");
    }
}
