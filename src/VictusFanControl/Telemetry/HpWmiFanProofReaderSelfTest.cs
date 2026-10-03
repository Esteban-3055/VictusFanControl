using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

internal static class HpWmiFanProofReaderSelfTest
{
    public static async Task<int> RunAsync(TextWriter output)
    {
        var passed = 0;
        async Task Test(string name, Func<Task> test)
        {
            await test(); await output.WriteLineAsync($"PASS: {name}"); passed++;
        }
        void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        var wait = TimeSpan.FromMilliseconds(250);
        try
        {
            await Test("fresh WMI proof uses only 2D and invokes again instead of reusing a sample", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1); var calls = 0;
                var reader = new HpWmiFanProofReader(request =>
                {
                    Check(request.Command == 0x20008 && request.CommandType == 0x2D && request.OutputSize == 128 &&
                        request.Payload.SequenceEqual(new byte[4]), "read-only proof envelope changed");
                    calls++; return new(0, [26, 24]);
                }, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(1));
                var before = await reader.ReadFreshAsync(CancellationToken.None);
                var after = await reader.ReadFreshAsync(CancellationToken.None);
                Check(calls == 2 && after.Sequence > before.Sequence && before.Speeds.CpuNominalRpm == 2600, "query was reused");
            });
            await Test("timeout retains native read slot across proof-reader recreation", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var nativeEntered = new ManualResetEventSlim(); using var releaseNative = new ManualResetEventSlim();
                var calls = 0;
                var reader = new HpWmiFanProofReader(_ => { Interlocked.Increment(ref calls); nativeEntered.Set(); releaseNative.Wait(); return new(0, [30, 30]); }, slot, () => Environment.TickCount64, wait);
                var timedOut = false;
                try { await reader.ReadFreshAsync(CancellationToken.None); } catch (TimeoutException) { timedOut = true; }
                Check(nativeEntered.IsSet && timedOut && slot.CurrentCount == 0, "native call was falsely canceled/released");
                var next = new HpWmiFanProofReader(_ => { calls++; return new(0, [30, 30]); }, slot, () => Environment.TickCount64, wait);
                try { await next.ReadFreshAsync(CancellationToken.None); throw new Exception("parallel replacement admitted"); } catch (TimeoutException) { }
                Check(calls == 1, "native queries overlapped");
                using var telemetry = new HpWmiFanTelemetryReader(_ => throw new InvalidOperationException("unexpected periodic query"),
                    () => Environment.TickCount64, () => DateTimeOffset.UtcNow, slot);
                telemetry.Pause(); var quiescence = telemetry.WaitForQuiescenceAsync(CancellationToken.None);
                Check(!quiescence.IsCompleted, "periodic quiescence ignored in-flight control proof");
                releaseNative.Set(); await quiescence;
                Check(await slot.WaitAsync(TimeSpan.FromSeconds(1)), "native slot never released after completion"); slot.Release();
                var recovered = await next.ReadFreshAsync(CancellationToken.None);
                Check(calls == 2 && recovered.Speeds.CpuSpeedLevel == 30, "fresh recovery query missing");
            });
            await Test("cancellation releases the waiter while native read retains its slot", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1); using var cts = new CancellationTokenSource();
                using var releaseNative = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var reader = new HpWmiFanProofReader(_ => { entered.SetResult(); releaseNative.Wait(); return new(0, [30, 30]); }, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(1));
                var pending = reader.ReadFreshAsync(cts.Token).AsTask(); await entered.Task; cts.Cancel();
                try { await pending; throw new Exception("canceled waiter returned proof"); } catch (OperationCanceledException) { }
                Check(slot.CurrentCount == 0, "cancellation released an active native slot"); releaseNative.Set();
                Check(await slot.WaitAsync(TimeSpan.FromSeconds(1)), "canceled native completion never released slot"); slot.Release();
            });
            await Test("query-start age expiry and invalid response fail without fallback", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1); long clock = 0;
                var reader = new HpWmiFanProofReader(_ => { clock = 3000; return new(0, [26, 24]); }, slot, () => clock, TimeSpan.FromSeconds(4));
                try { await reader.ReadFreshAsync(CancellationToken.None); throw new Exception("expired proof accepted"); } catch (InvalidDataException) { }
                foreach (var response in new HpBiosResponse[] { new(1, [26, 24]), new(0, [26]), new(0, [255, 24]), new(0, [100, 24]) })
                {
                    var invalid = new HpWmiFanProofReader(_ => response, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(1));
                    try { await invalid.ReadFreshAsync(CancellationToken.None); throw new Exception("invalid proof accepted"); } catch (InvalidDataException) { }
                }
                Check(slot.CurrentCount == 1, "failed read retained idle slot");
            });
            await Test("post-command proof rejects duplicated, pre-command and expired query identity", () =>
            {
                var now = Environment.TickCount64;
                var baseline = new Hp8C40EcControlState(255,255,255,255,30,30,255,255,255,0,0,2600,2400)
                    { TachometerResolutionRpm = 100, FanQuerySequence = 10, FanQueryStartedAtMilliseconds = now - 500 };
                var valid = baseline with { CpuRpm = 2900, FanQuerySequence = 11, FanQueryStartedAtMilliseconds = now };
                Check(Hp8C40FanControlBackend.IsFreshTachometerProof(valid, baseline, now, 10), "valid post-command proof rejected");
                Check(!Hp8C40FanControlBackend.IsFreshTachometerProof(valid, baseline, now, 11), "same query counted twice");
                Check(!Hp8C40FanControlBackend.IsFreshTachometerProof(valid with { FanQueryStartedAtMilliseconds = now - 1 }, baseline, now, 10), "pre-command query counted");
                Check(!Hp8C40FanControlBackend.IsFreshTachometerProof(valid with { FanQueryStartedAtMilliseconds = now - 3000 }, baseline, now - 5000, 10), "expired sample counted");
                Check(!Hp8C40FanControlBackend.IsFreshTachometerProof(valid with { FanQuerySequence = 0 }, baseline, now, 0), "missing identity counted");
                return Task.CompletedTask;
            });
        }
        catch (Exception ex) { await output.WriteLineAsync($"FAIL: WMI control proof: {ex}"); return 38; }
        await output.WriteLineAsync($"WMI control proof reader self-test: PASS ({passed} cases)");
        return 0;
    }
}
