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
            await Test("five historical acquisitions never replace a new raw proof query", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                var calls = 0;
                using var telemetry = new HpWmiFanTelemetryReader(_ => throw new Exception("unexpected periodic query"),
                    () => Interlocked.Read(ref clock), () => DateTimeOffset.UnixEpoch, slot);
                var reader = new HpWmiFanProofReader(_ =>
                    Interlocked.Increment(ref calls) <= 5 ? new(0, [22, 24]) : new(0, [40, 41]),
                    slot, () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(2));
                HpWmiFanProofSample? previous = null;
                for (var i = 0; i < 5; i++) previous = await reader.ReadFreshAsync(CancellationToken.None);
                Check(telemetry.ReadWindowCached() is { WindowCount: 5, StableCpuRpm: 2200 }, "history not filled");
                Interlocked.Exchange(ref clock, 1000);
                var fresh = await reader.ReadFreshAsync(CancellationToken.None);
                Check(calls == 6 && fresh.Sequence > previous!.Sequence &&
                    fresh.Speeds is { CpuNominalRpm: 4000, GpuNominalRpm: 4100, StartedAtMilliseconds: 1000 } &&
                    telemetry.ReadWindowCached() is { WindowCount: 5, StableCpuRpm: 2200, RawLatest.CpuNominalRpm: 4000 },
                    "proof reused history or median instead of the new raw acquisition");
            });
            await Test("full fresh window cannot satisfy failed, invalid, expired or canceled Control reads", async () =>
            {
                foreach (var outcome in new[] { "native failure", "invalid", "expired", "admission timeout", "canceled" })
                {
                    using var slot = new SemaphoreSlim(1, 1);
                    long clock = 0;
                    var calls = 0;
                    using var telemetry = new HpWmiFanTelemetryReader(_ => throw new Exception("unexpected periodic query"),
                        () => Interlocked.Read(ref clock), () => DateTimeOffset.UnixEpoch, slot);
                    var seed = new HpWmiFanProofReader(_ => { calls++; return new(0, [40, 41]); },
                        slot, () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(2));
                    for (var i = 0; i < 5; i++) await seed.ReadFreshAsync(CancellationToken.None);
                    Check(telemetry.ReadWindowCached()?.WindowCount == 5, "history not filled");
                    var candidate = new HpWmiFanProofReader(_ =>
                    {
                        calls++;
                        if (outcome == "native failure") throw new IOException("synthetic native failure");
                        if (outcome == "invalid") return new(0, [255, 41]);
                        if (outcome == "expired") Interlocked.Exchange(ref clock, 3000);
                        return new(0, [40, 41]);
                    }, slot, () => Interlocked.Read(ref clock), outcome == "admission timeout" ? wait : TimeSpan.FromSeconds(4));
                    using var cancel = new CancellationTokenSource();
                    var held = outcome == "admission timeout";
                    if (held) Check(slot.Wait(0), "test lane not idle");
                    if (outcome == "canceled") cancel.Cancel();
                    var rejected = false;
                    try { await candidate.ReadFreshAsync(cancel.Token); }
                    catch (InvalidDataException) when (outcome is "invalid" or "expired") { rejected = true; }
                    catch (IOException) when (outcome == "native failure") { rejected = true; }
                    catch (TimeoutException) when (held) { rejected = true; }
                    catch (OperationCanceledException) when (outcome == "canceled") { rejected = true; }
                    finally { if (held) slot.Release(); }
                    Check(rejected && calls == (held || outcome == "canceled" ? 5 : 6), "history supplied proof after a failed new acquisition");
                    Check(outcome == "expired" ? telemetry.ReadWindowCached() is null : telemetry.ReadWindowCached()?.WindowCount == 5,
                        "failed query entered history or expired history appeared fresh");
                }
            });
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
            await Test("fresh control query renews periodic telemetry while the shared slot delays polling", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                    () => Interlocked.Read(ref clock), () => DateTimeOffset.UtcNow, slot);
                telemetry.ReadCached(); await telemetry.PendingQuery;
                Check(telemetry.ReadCached() is { StartedAtMilliseconds: 0 }, "seed sample missing");
                using var release = new ManualResetEventSlim();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var proof = new HpWmiFanProofReader(_ =>
                {
                    entered.SetResult();
                    Check(release.Wait(TimeSpan.FromSeconds(5)), "test native release timed out");
                    return new(0, [30, 31]);
                }, slot, () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(4));
                Interlocked.Exchange(ref clock, 1000);
                var pending = proof.ReadFreshAsync(CancellationToken.None).AsTask();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Interlocked.Exchange(ref clock, 3100);
                    Check(telemetry.ReadCached() is null, "in-flight proof renewed an expired periodic sample");
                }
                finally { release.Set(); }
                var actual = await pending;
                Check(actual.Speeds.StartedAtMilliseconds == 1000, "proof start age was changed");
                var observed = telemetry.ReadCached();
                Check(observed is { CpuNominalRpm: 3000, GpuNominalRpm: 3100, StartedAtMilliseconds: 1000 },
                    "fresh completed control query was invisible to periodic telemetry");
                Check(ReferenceEquals(observed, actual.Speeds), "publication replaced acquisition metadata");
                Interlocked.Exchange(ref clock, 4000);
                Check(telemetry.ReadCached() is null, "mirrored sample survived its original 3-second expiry");
            });
            await Test("canceled and logically timed-out control reads never publish late native results", async () =>
            {
                foreach (var cancel in new[] { true, false })
                {
                    using var slot = new SemaphoreSlim(1, 1);
                    using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                        () => 0, () => DateTimeOffset.UtcNow, slot);
                    telemetry.ReadCached(); await telemetry.PendingQuery;
                    using var release = new ManualResetEventSlim();
                    using var cts = new CancellationTokenSource();
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var proof = new HpWmiFanProofReader(_ =>
                    {
                        entered.SetResult();
                        Check(release.Wait(TimeSpan.FromSeconds(5)), "test native release timed out");
                        return new(0, [30, 31]);
                    }, slot, () => 0, wait);
                    var pending = proof.ReadFreshAsync(cts.Token).AsTask();
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        if (cancel) cts.Cancel();
                        var refused = false;
                        try { await pending; }
                        catch (OperationCanceledException) when (cancel) { refused = true; }
                        catch (TimeoutException) when (!cancel) { refused = true; }
                        Check(refused && slot.CurrentCount == 0, "abandoned native call released the slot");
                    }
                    finally { release.Set(); }
                    await telemetry.WaitForQuiescenceAsync(CancellationToken.None);
                    Check(telemetry.ReadCached() is { CpuNominalRpm: 2600 }, "abandoned query published after native completion");
                    Check(telemetry.ReadWindowCached() is { WindowCount: 1, RawLatest.CpuNominalRpm: 2600 },
                        "abandoned native result entered the rolling window");
                }
            });
            await Test("control publication cannot cross pause, disposal or reader recreation", async () =>
            {
                foreach (var boundary in new[] { "pause", "dispose", "new-reader" })
                {
                    using var slot = new SemaphoreSlim(1, 1);
                    using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                        () => 0, () => DateTimeOffset.UtcNow, slot);
                    telemetry.ReadCached(); await telemetry.PendingQuery;
                    using var release = new ManualResetEventSlim();
                    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var proof = new HpWmiFanProofReader(_ =>
                    {
                        entered.SetResult();
                        Check(release.Wait(TimeSpan.FromSeconds(5)), "test native release timed out");
                        return new(0, [30, 31]);
                    }, slot, () => 0, TimeSpan.FromSeconds(4));
                    var pending = proof.ReadFreshAsync(CancellationToken.None).AsTask();
                    HpWmiFanTelemetryReader? replacement = null;
                    try
                    {
                        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                        if (boundary == "pause") telemetry.Pause();
                        else if (boundary == "dispose") telemetry.Dispose();
                        if (boundary != "pause")
                            replacement = new HpWmiFanTelemetryReader(_ => new(0, [40, 41]),
                                () => 0, () => DateTimeOffset.UtcNow, slot);
                        release.Set(); await pending;
                        Check(boundary == "new-reader"
                                ? telemetry.ReadCached() is { CpuNominalRpm: 3000 }
                                : telemetry.ReadCached() is null,
                            "pre-boundary result crossed a paused/disposed reader epoch");
                        if (replacement is not null)
                        {
                            Check(replacement.ReadCached() is null, "new reader received a query begun before registration");
                            await replacement.PendingQuery;
                            Check(replacement.ReadCached() is { CpuNominalRpm: 4000 }, "replacement own query failed");
                        }
                    }
                    finally
                    {
                        release.Set(); await pending; replacement?.Dispose();
                    }
                }
            });
            await Test("invalid and expired control responses cannot renew periodic telemetry", async () =>
            {
                foreach (var response in new HpBiosResponse[] { new(1, [30, 31]), new(0, [30]), new(0, [255, 31]), new(0, [100, 31]) })
                {
                    using var slot = new SemaphoreSlim(1, 1);
                    using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                        () => 0, () => DateTimeOffset.UtcNow, slot);
                    telemetry.ReadCached(); await telemetry.PendingQuery;
                    var proof = new HpWmiFanProofReader(_ => response, slot, () => 0, TimeSpan.FromSeconds(1));
                    var refused = false;
                    try { await proof.ReadFreshAsync(CancellationToken.None); }
                    catch (InvalidDataException) { refused = true; }
                    Check(refused && telemetry.ReadCached() is { CpuNominalRpm: 2600 }, "invalid proof renewed periodic telemetry");
                }
                using var slowSlot = new SemaphoreSlim(1, 1);
                long clock = 0;
                using var slowTelemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                    () => Interlocked.Read(ref clock), () => DateTimeOffset.UtcNow, slowSlot);
                slowTelemetry.ReadCached(); await slowTelemetry.PendingQuery;
                var slowProof = new HpWmiFanProofReader(_ =>
                {
                    Interlocked.Exchange(ref clock, 3000); return new(0, [30, 31]);
                }, slowSlot, () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(4));
                try { await slowProof.ReadFreshAsync(CancellationToken.None); throw new Exception("expired proof accepted"); }
                catch (InvalidDataException) { }
                Check(slowTelemetry.ReadCached() is null, "expired proof was republished with a renewed timestamp");
                await slowTelemetry.PendingQuery;
            });
            await Test("older control publication cannot overwrite a newer periodic sample", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [40, 41]),
                    () => Interlocked.Read(ref clock), () => DateTimeOffset.UtcNow, slot);
                var oldSink = HpWmiFanSamplePublication.Capture(slot);
                Interlocked.Exchange(ref clock, 1000);
                telemetry.ReadCached(); await telemetry.PendingQuery;
                oldSink(new(30, 31, DateTimeOffset.UtcNow, 500));
                Check(telemetry.ReadCached() is { CpuNominalRpm: 4000, StartedAtMilliseconds: 1000 }, "older publication rolled back fresh RPM");
                oldSink(new(30, 31, DateTimeOffset.UtcNow, 2000));
                Check(telemetry.ReadCached() is { CpuNominalRpm: 4000 }, "future acquisition accepted after clock regression");
                Check(telemetry.ReadWindowCached() is { WindowCount: 1, RawLatest.CpuNominalRpm: 4000 },
                    "old/future publication entered rolling history");
            });
            await Test("control publication is isolated by native admission slot", async () =>
            {
                using var telemetrySlot = new SemaphoreSlim(1, 1);
                using var proofSlot = new SemaphoreSlim(1, 1);
                using var telemetry = new HpWmiFanTelemetryReader(_ => new(0, [26, 24]),
                    () => 0, () => DateTimeOffset.UtcNow, telemetrySlot);
                telemetry.ReadCached(); await telemetry.PendingQuery;
                var proof = new HpWmiFanProofReader(_ => new(0, [30, 31]), proofSlot, () => 0, TimeSpan.FromSeconds(1));
                await proof.ReadFreshAsync(CancellationToken.None);
                Check(telemetry.ReadCached() is { CpuNominalRpm: 2600 }, "unrelated slot replaced periodic RPM");
                Check(telemetry.ReadWindowCached() is { WindowCount: 1, RawLatest.CpuNominalRpm: 2600 },
                    "unrelated slot contaminated history");
            });
            await Test("delayed old publication cannot hide a newer periodic failure; fresh recovery can", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0; var calls = 0;
                using var telemetry = new HpWmiFanTelemetryReader(_ =>
                    ++calls == 1 ? new(0, [26, 24]) : new(7, []),
                    () => Interlocked.Read(ref clock), () => DateTimeOffset.UtcNow, slot);
                telemetry.ReadCached(); await telemetry.PendingQuery;
                var oldSink = HpWmiFanSamplePublication.Capture(slot);
                Interlocked.Exchange(ref clock, 1000);
                telemetry.ReadCached(); await telemetry.PendingQuery;
                Check(telemetry.ReadCached() is null, "failed periodic query retained cache");
                oldSink(new(30, 31, DateTimeOffset.UtcNow, 500));
                Check(telemetry.ReadCached() is null, "older publication hid a newer failure");
                Check(telemetry.ReadWindowCached() is null, "old publication resurrected failed window");
                var proof = new HpWmiFanProofReader(_ => new(0, [30, 31]), slot,
                    () => Interlocked.Read(ref clock), TimeSpan.FromSeconds(1));
                await proof.ReadFreshAsync(CancellationToken.None);
                Check(telemetry.ReadCached() is { CpuNominalRpm: 3000 } && telemetry.Recoveries == 1 && calls == 2,
                    "genuinely newer success failed to recover telemetry or caused redundant periodic I/O");
                Check(telemetry.ReadWindowCached()?.WindowCount == 1, "recovery reused pre-failure history");
            });
        }
        catch (Exception ex) { await output.WriteLineAsync($"FAIL: WMI control proof: {ex}"); return 38; }
        await output.WriteLineAsync($"WMI control proof reader self-test: PASS ({passed} cases)");
        return 0;
    }
}
