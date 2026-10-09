using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

/// <summary>Worker publication/lifecycle fixtures; never starts hardware acquisition.</summary>
internal static class TelemetryCoordinationSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try
        {
            await using var worker = new TelemetryWorker("unused-synthetic-modules");
            var published = new List<TelemetrySnapshot>();
            worker.SnapshotAvailable += (_, snapshot) => published.Add(snapshot);
            var initial = Snapshot(DateTimeOffset.UtcNow);
            var firstRefresh = Snapshot(initial.Timestamp.AddSeconds(1));
            var lastRefresh = Snapshot(initial.Timestamp.AddSeconds(4));
            worker.SnapshotProcessor = async (snapshot, ct) =>
            {
                Require(ReferenceEquals(snapshot, initial));
                worker.PublishAcknowledgementSnapshot(firstRefresh);
                await Task.Yield();
                worker.PublishAcknowledgementSnapshot(lastRefresh);
            };
            var healthSnapshot = await worker.ProcessAndPublishSnapshotAsync(initial, CancellationToken.None);
            Require(ReferenceEquals(healthSnapshot, lastRefresh) && published.Count == 2 &&
                ReferenceEquals(published[0], firstRefresh) && ReferenceEquals(published[1], lastRefresh));
            Console.WriteLine("PASS: long ACK preserves latest publication and health sample; no old epoch replay.");

            var next = Snapshot(lastRefresh.Timestamp.AddSeconds(1));
            worker.SnapshotProcessor = (_, _) => Task.CompletedTask;
            healthSnapshot = await worker.ProcessAndPublishSnapshotAsync(next, CancellationToken.None);
            Require(ReferenceEquals(healthSnapshot, next) && published.Count == 3 && ReferenceEquals(published[^1], next));
            Console.WriteLine("PASS: next ordinary acquisition clears the prior ACK continuation.");

            var interrupted = Snapshot(next.Timestamp.AddSeconds(1));
            var finalRefresh = Snapshot(interrupted.Timestamp.AddSeconds(1));
            worker.SnapshotProcessor = (_, _) =>
            {
                worker.PublishAcknowledgementSnapshot(finalRefresh);
                throw new OperationCanceledException("Synthetic in-flight interruption.");
            };
            var cancelled = false;
            try { await worker.ProcessAndPublishSnapshotAsync(interrupted, CancellationToken.None); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && published.Count == 4 && ReferenceEquals(published[^1], finalRefresh));
            Console.WriteLine("PASS: interrupted operation propagates failure without replaying its original sample.");

            var beforeRejectedRefresh = published.Count;
            var lifecycleRejected = false;
            try { await worker.RefreshDuringFanAcknowledgementAsync(CancellationToken.None); }
            catch (InvalidOperationException) { lifecycleRejected = true; }
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            cancelled = false;
            try { await worker.RefreshDuringFanAcknowledgementAsync(cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(lifecycleRejected && cancelled && published.Count == beforeRejectedRefresh);
            Console.WriteLine("PASS: unvalidated/cancelled lifecycle cannot acquire or publish ACK telemetry.");

            worker.SnapshotProcessor = (_, _) =>
            {
                worker.NotifySuspend("synthetic boundary during ACK");
                return Task.CompletedTask;
            };
            healthSnapshot = await worker.ProcessAndPublishSnapshotAsync(next, CancellationToken.None);
            Require(healthSnapshot is null && published.Count == beforeRejectedRefresh);
            Console.WriteLine("PASS: suspend during a processor await discards its pre-boundary publication/health sample.");

            worker.NotifyResume("synthetic initial resume");
            worker.SnapshotProcessor = (_, _) =>
            {
                worker.NotifySuspend("synthetic second boundary");
                worker.NotifyResume("synthetic second resume");
                return Task.CompletedTask;
            };
            healthSnapshot = await worker.ProcessAndPublishSnapshotAsync(next, CancellationToken.None);
            Require(healthSnapshot is null && published.Count == beforeRejectedRefresh);
            Console.WriteLine("PASS: suspend/resume in one operation still discards the previous power epoch.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL: telemetry coordination: " + ex);
            return 1;
        }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Telemetry coordination assertion failed.");
    }

    private static TelemetrySnapshot Snapshot(DateTimeOffset timestamp) =>
        new(timestamp, "synthetic CPU", 55, 20, 10, "synthetic GPU", 45, 15, 10, 2200, 2400);
}
