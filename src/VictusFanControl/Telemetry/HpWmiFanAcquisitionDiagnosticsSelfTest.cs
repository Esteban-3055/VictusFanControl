using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

/// <summary>Instrumentation-only tests; fake transport and fake clocks, no hardware access.</summary>
internal static class HpWmiFanAcquisitionDiagnosticsSelfTest
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

        static void Check(bool value, string reason)
        {
            if (!value) throw new InvalidOperationException(reason);
        }

        try
        {
            await Test("periodic timing is observable without changing freshness semantics", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                long clock = 0;
                using var reader = new HpWmiFanTelemetryReader(
                    _ =>
                    {
                        Interlocked.Exchange(ref clock, 1250);
                        return new HpBiosResponse(0, [26, 24]);
                    },
                    () => Interlocked.Read(ref clock),
                    () => DateTimeOffset.UnixEpoch.AddMilliseconds(Interlocked.Read(ref clock)),
                    slot);

                reader.ReadCached();
                await reader.PendingQuery.WaitAsync(TimeSpan.FromSeconds(2));

                var status = HpWmiFanAcquisitionDiagnostics.For(slot).LastStatus;
                var counters = HpWmiFanAcquisitionDiagnostics.For(slot).Counters;
                Check(status is
                {
                    Purpose: HpWmiFanAcquisitionPurpose.Periodic,
                    Outcome: "accepted",
                    QueueWaitMilliseconds: 0,
                    NativeDurationMilliseconds: 1250,
                    TotalDurationMilliseconds: 1250
                }, "periodic acquisition timing was not preserved");
                Check(counters.PeriodicNativeStarts == 1 && counters.ControlNativeStarts == 0 &&
                    counters.Accepted == 1 && counters.SlowNative == 1, "periodic counters incorrect");
                Check(reader.ReadCached() is { StartedAtMilliseconds: 0 },
                    "instrumentation changed periodic sample timing/freshness");
                Check(reader.DrainAcquisitionNotices().Any(line =>
                    line.Contains("nativeMs=1250", StringComparison.Ordinal)),
                    "slow periodic acquisition did not produce a bounded diagnostic notice");
            });

            await Test("periodic admission contention is counted without starting native I/O", () =>
            {
                using var slot = new SemaphoreSlim(0, 1);
                var calls = 0;
                using var reader = new HpWmiFanTelemetryReader(
                    _ =>
                    {
                        calls++;
                        return new HpBiosResponse(0, [26, 24]);
                    },
                    () => 0,
                    () => DateTimeOffset.UnixEpoch,
                    slot);

                Check(reader.ReadCached() is null, "busy periodic slot unexpectedly returned telemetry");
                var status = HpWmiFanAcquisitionDiagnostics.For(slot).LastStatus;
                var counters = HpWmiFanAcquisitionDiagnostics.For(slot).Counters;
                Check(calls == 0 && status is
                {
                    Purpose: HpWmiFanAcquisitionPurpose.Periodic,
                    Outcome: "admission-busy"
                }, "periodic contention started native I/O or was not observable");
                Check(counters.PeriodicAdmissionBusy == 1 && counters.PeriodicNativeStarts == 0,
                    "periodic admission-busy counters incorrect");
                return Task.CompletedTask;
            });

            await Test("control queue wait and native duration are correlated on the shared slot", async () =>
            {
                using var slot = new SemaphoreSlim(0, 1);
                long clock = 0;
                var proof = new HpWmiFanProofReader(
                    _ =>
                    {
                        Interlocked.Exchange(ref clock, 800);
                        return new HpBiosResponse(0, [30, 31]);
                    },
                    slot,
                    () => Interlocked.Read(ref clock),
                    TimeSpan.FromSeconds(2));

                var pending = proof.ReadFreshAsync(CancellationToken.None).AsTask();
                await Task.Delay(25);
                Interlocked.Exchange(ref clock, 400);
                slot.Release();
                var sample = await pending;

                var status = HpWmiFanAcquisitionDiagnostics.For(slot).LastStatus;
                var counters = HpWmiFanAcquisitionDiagnostics.For(slot).Counters;
                Check(sample.Speeds.CpuNominalRpm == 3000 && status is
                {
                    Purpose: HpWmiFanAcquisitionPurpose.Control,
                    Outcome: "accepted",
                    QueueWaitMilliseconds: 400,
                    NativeDurationMilliseconds: 400,
                    TotalDurationMilliseconds: 800
                }, "control queue/native timing correlation incorrect");
                Check(counters.ControlNativeStarts == 1 && counters.Accepted == 1,
                    "control acquisition counters incorrect");
            });

            await Test("timed-out control waiter remains distinguishable from late native completion", async () =>
            {
                using var slot = new SemaphoreSlim(1, 1);
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var proof = new HpWmiFanProofReader(
                    _ =>
                    {
                        entered.Set();
                        release.Wait();
                        return new HpBiosResponse(0, [30, 31]);
                    },
                    slot,
                    () => Environment.TickCount64,
                    TimeSpan.FromMilliseconds(150));

                var timedOut = false;
                try
                {
                    await proof.ReadFreshAsync(CancellationToken.None);
                }
                catch (TimeoutException)
                {
                    timedOut = true;
                }

                Check(timedOut && entered.IsSet && slot.CurrentCount == 0,
                    "control timeout changed native single-flight ownership");
                var timedOutStatus = HpWmiFanAcquisitionDiagnostics.For(slot).LastStatus;
                Check(timedOutStatus is
                {
                    Purpose: HpWmiFanAcquisitionPurpose.Control,
                    Outcome: "waiter-timeout"
                }, "control waiter timeout was not separately observable");

                release.Set();
                Check(await slot.WaitAsync(TimeSpan.FromSeconds(2)),
                    "late native completion never released the shared slot");
                slot.Release();
                await Task.Delay(25);

                var completedStatus = HpWmiFanAcquisitionDiagnostics.For(slot).LastStatus;
                var counters = HpWmiFanAcquisitionDiagnostics.For(slot).Counters;
                Check(completedStatus?.Outcome == "native-completed-after-waiter-timeout",
                    "late native completion was not correlated with the timed-out waiter");
                Check(counters.WaiterTimeouts == 1 && counters.ControlNativeStarts == 1,
                    "control timeout counters incorrect");
            });
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"FAIL: HP WMI acquisition diagnostics: {ex}");
            return 41;
        }

        await output.WriteLineAsync($"HP WMI acquisition diagnostics: PASS ({passed} cases; no hardware access).");
        return 0;
    }
}
