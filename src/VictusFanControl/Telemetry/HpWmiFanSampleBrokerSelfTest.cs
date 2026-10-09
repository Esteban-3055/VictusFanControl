using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

/// <summary>Fake transport and synchronization only; never opens hardware.</summary>
internal static class HpWmiFanSampleBrokerSelfTest
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);

    internal static async Task<int> RunAsync(TextWriter output)
    {
        var passed = 0;
        async Task Test(string name, Func<Task> test)
        {
            await test();
            await output.WriteLineAsync($"PASS: broker {name}");
            passed++;
        }

        try
        {
            await Test("identity and admission survive adapter recreation", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                var broker = HpWmiFanSampleBroker.For(slot);
                Check(ReferenceEquals(broker, HpWmiFanSampleBroker.For(slot)) &&
                    ReferenceEquals(broker.Diagnostics, HpWmiFanAcquisitionDiagnostics.For(slot)) &&
                    ReferenceEquals(HpWmiFanTelemetryReader.SharedReadAdmission, HpWmiFanSampleBroker.Production.Admission),
                    "shared broker/diagnostic/production identity changed");
                var lease = broker.TryAcquirePeriodic() ?? throw new Exception("idle slot unavailable");
                try
                {
                    using var old = Telemetry(slot, () => 0, _ => throw new Exception("unexpected native query"));
                    old.Dispose();
                    using var replacement = Telemetry(slot, () => 0, _ => throw new Exception("overlapping native query"));
                    Check(replacement.ReadCached() is null && replacement.PendingQuery.IsCompleted,
                        "recreation bypassed a reserved native slot");
                    using var cancel = new CancellationTokenSource();
                    var quiescence = replacement.WaitForQuiescenceAsync(cancel.Token);
                    Check(!quiescence.IsCompleted, "quiescence ignored broker reservation");
                    cancel.Cancel();
                    try { await quiescence; throw new Exception("canceled quiescence succeeded"); }
                    catch (OperationCanceledException) { }
                    Check(slot.CurrentCount == 0, "quiescence cancellation released another owner");
                }
                finally { lease.CancelBeforeNative(); }
                await broker.WaitForQuiescenceAsync(CancellationToken.None).WaitAsync(TestDeadline);
                Check(slot.CurrentCount == 1, "idle slot not restored");
            });

            await Test("lease cannot be reused, cross brokers, or release native through cancellation", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var otherSlot = new SemaphoreSlim(1, 1);
                var broker = HpWmiFanSampleBroker.For(slot);
                var other = HpWmiFanSampleBroker.For(otherSlot);
                var lease = broker.TryAcquirePeriodic() ?? throw new Exception("idle slot unavailable");
                Refused(() => other.RunNative(lease, () => 1));
                Check(slot.CurrentCount == 0 && otherSlot.CurrentCount == 1, "wrong broker altered ownership");
                using var release = new ManualResetEventSlim();
                var entered = Signal();
                var pending = broker.RunNative(lease, () =>
                {
                    entered.SetResult();
                    Check(release.Wait(TestDeadline), "test release timed out");
                    return 42;
                });
                try
                {
                    await entered.Task.WaitAsync(TestDeadline);
                    lease.CancelBeforeNative();
                    Refused(() => broker.RunNative(lease, () => 1));
                    Check(slot.CurrentCount == 0 && broker.TryAcquirePeriodic() is null,
                        "abandoned/reused lease released live native work");
                }
                finally { release.Set(); await pending.WaitAsync(TestDeadline); }
                lease.CancelBeforeNative();
                Check(await pending == 42 && slot.CurrentCount == 1, "native completion did not release exactly once");
                var canceled = broker.TryAcquirePeriodic() ?? throw new Exception("idle slot unavailable");
                canceled.CancelBeforeNative();
                canceled.CancelBeforeNative();
                Refused(() => broker.RunNative(canceled, () => 1));
                Check(slot.CurrentCount == 1, "reservation cancellation over-released admission");
            });

            await Test("native exception releases once and admits a fresh worker", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                var broker = HpWmiFanSampleBroker.For(slot);
                var lease = broker.TryAcquirePeriodic() ?? throw new Exception("idle slot unavailable");
                try
                {
                    await broker.RunNative<int>(lease, () => throw new IOException("synthetic native failure"));
                    throw new Exception("native failure swallowed");
                }
                catch (IOException) { }
                lease.CancelBeforeNative();
                var next = await broker.AcquireControlAsync(TestDeadline, CancellationToken.None)
                    ?? throw new Exception("failed native worker retained slot");
                Check(await broker.RunNative(next, () => 24) == 24 && slot.CurrentCount == 1,
                    "fresh worker failed after native exception");
            });

            await Test("Control waits behind Periodic, admission abandonment cannot overlap native I/O", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var release = new ManualResetEventSlim();
                var entered = Signal();
                var active = 0;
                var peak = 0;
                var calls = 0;
                HpBiosResponse Send(HpBiosRequest request)
                {
                    var count = Interlocked.Increment(ref active);
                    Interlocked.Exchange(ref peak, Math.Max(peak, count));
                    try
                    {
                        if (Interlocked.Increment(ref calls) == 1)
                        {
                            entered.SetResult();
                            Check(release.Wait(TestDeadline), "test release timed out");
                        }
                        return new(0, [30, 31]);
                    }
                    finally { Interlocked.Decrement(ref active); }
                }
                using var telemetry = Telemetry(slot, () => 0, Send);
                telemetry.ReadCached();
                try
                {
                    await entered.Task.WaitAsync(TestDeadline);
                    var broker = HpWmiFanSampleBroker.For(slot);
                    Check(broker.TryAcquirePeriodic() is null && telemetry.ReadCached() is null,
                        "busy periodic read waited or reused absent telemetry");
                    Check(await broker.AcquireControlAsync(TimeSpan.Zero, CancellationToken.None) is null,
                        "admission timeout bypassed live native work");
                    using var cancel = new CancellationTokenSource();
                    var abandoned = broker.AcquireControlAsync(TestDeadline, cancel.Token).AsTask();
                    Check(!abandoned.IsCompleted, "Control failed to wait for periodic native work");
                    cancel.Cancel();
                    try { await abandoned; throw new Exception("canceled admission succeeded"); }
                    catch (OperationCanceledException) { }
                    Check(slot.CurrentCount == 0 && calls == 1, "abandoned admission released native owner");
                    var proof = new HpWmiFanProofReader(Send, slot, () => 0, TestDeadline);
                    var control = proof.ReadFreshAsync(CancellationToken.None).AsTask();
                    Check(!control.IsCompleted && calls == 1, "Control bypassed periodic native owner");
                    release.Set();
                    await telemetry.PendingQuery.WaitAsync(TestDeadline);
                    var sample = await control.WaitAsync(TestDeadline);
                    Check(calls == 2 && peak == 1 && sample.Speeds.CpuSpeedLevel == 30,
                        "Control reused cache or overlapped periodic native read");
                }
                finally { release.Set(); await telemetry.PendingQuery.WaitAsync(TestDeadline); }
            });

            await Test("sustained Control pressure exposes freshness loss without fabricated renewal", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                var periodicCalls = 0;
                using var telemetry = Telemetry(slot, () => Interlocked.Read(ref clock), _ =>
                {
                    periodicCalls++;
                    return new(0, [26, 24]);
                });
                telemetry.ReadCached();
                await telemetry.PendingQuery.WaitAsync(TestDeadline);
                HpWmiFanProofSample? previous = null;
                for (var i = 1; i <= 7; i++)
                {
                    Interlocked.Exchange(ref clock, i * 1000);
                    using var release = new ManualResetEventSlim();
                    var entered = Signal();
                    var proof = new HpWmiFanProofReader(_ =>
                    {
                        entered.SetResult();
                        Check(release.Wait(TestDeadline), "test release timed out");
                        return new(0, [30, 31]);
                    }, slot, () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(4));
                    var pending = proof.ReadFreshAsync(CancellationToken.None).AsTask();
                    try
                    {
                        await entered.Task.WaitAsync(TestDeadline);
                        var cached = telemetry.ReadCached();
                        Check(cached is not null && cached.StartedAtMilliseconds == (i - 1) * 1000,
                            "in-flight Control changed cached acquisition age");
                        if (i == 7)
                        {
                            Interlocked.Exchange(ref clock, 10_000);
                            Check(telemetry.ReadCached() is null && periodicCalls == 1,
                                "Control pressure hid real RPM expiry or overlapped native work");
                        }
                    }
                    finally { release.Set(); }
                    if (i == 7)
                    {
                        var rejected = false;
                        try { await pending.WaitAsync(TestDeadline); }
                        catch (InvalidDataException) { rejected = true; }
                        Check(rejected, "expired Control query was accepted");
                    }
                    else
                    {
                        var sample = await pending.WaitAsync(TestDeadline);
                        Check(sample.Sequence > (previous?.Sequence ?? 0) &&
                            ReferenceEquals(telemetry.ReadCached(), sample.Speeds) &&
                            sample.Speeds.StartedAtMilliseconds == i * 1000,
                            "accepted Control publication lost real query identity/timestamps");
                        previous = sample;
                    }
                }
                var counters = HpWmiFanSampleBroker.For(slot).Diagnostics.Counters;
                Check(periodicCalls == 1 && counters.ControlNativeStarts == 7 &&
                    counters.PeriodicAdmissionBusy >= 7 && counters.Expired == 1,
                    "pressure/expiry not visible in existing diagnostics");
            });
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"FAIL: HP WMI sample broker: {ex}");
            return 42;
        }
        await output.WriteLineAsync($"HP WMI sample broker: PASS ({passed} cases; no hardware access).");
        return 0;
    }

    private static HpWmiFanTelemetryReader Telemetry(SemaphoreSlim slot,
        Func<long> clock, Func<HpBiosRequest, HpBiosResponse> send) =>
        new(send, clock, () => DateTimeOffset.UnixEpoch, slot);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Refused(Action action)
    {
        var refused = false;
        try { action(); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "invalid lease use was admitted");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
