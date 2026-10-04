using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Runtime;

internal static class WmiFanThermalAdmissionSelfTest
{
    private static HardwareIdentity Hardware => new(Hp8C40TargetProfile.BoardManufacturer,
        Hp8C40TargetProfile.BoardProduct, Hp8C40TargetProfile.BoardVersion,
        Hp8C40TargetProfile.SystemManufacturer, Hp8C40TargetProfile.SystemProductName,
        Hp8C40TargetProfile.SystemSkuPrefix, Hp8C40TargetProfile.ValidatedBiosVersion);

    private static TelemetrySnapshot Sample(DateTimeOffset at, double cpu = 63, double gpu = 47, double power = 5) =>
        new(at, "CPU", Math.Min(cpu, 97), power, 17, Hp8C40TargetProfile.ExpectedGpuName,
            gpu, 14, 0, 3100, 3000)
        {
            CpuExpectedPhysicalCoreCount = 14,
            CpuCoreTemperatures = Enumerable.Range(0, 14)
                .Select(i => new CpuCoreTemperatureSample(i, i, "Performance", i == 3 ? cpu : 53)).ToArray(),
            FanTelemetrySource = "HP-WMI-ACPI-2D", FanRpmResolution = 100,
            FanSampledAtUtc = at.AddMilliseconds(-400), FanSampleAgeMilliseconds = 400,
            FanAgeCapturedAtUtc = at
        };

    internal static async Task RunAsync()
    {
        var origin = DateTimeOffset.UtcNow;
        long clock = 0;
        DateTimeOffset Now() => origin.AddMilliseconds(clock);
        WmiFanThermalAdmission Ready()
        {
            clock = 0;
            var guard = new WmiFanThermalAdmission(Hardware, () => clock);
            Check(guard.Observe(Sample(Now()), Now()).EffectiveSafety.CustomControlPermitted, "Healthy startup denied.");
            clock = 100;
            return guard;
        }
        var guard = Ready();
        var high = Sample(Now(), 98, power: 35.284);
        var first = guard.Observe(high, Now());
        Check(first.RawSafety.ThermalEmergency && !first.EffectiveSafety.ThermalEmergency &&
            first.EffectiveSafety.CustomControlPermitted && first.CpuHighSamples == 1 &&
            first.CpuConfirmationPending && first.RemainingConfirmationMilliseconds == 2000,
            "First fresh 98 C epoch must retain raw heat and start bounded confirmation.");
        clock += 500;
        for (var i = 0; i < 10; i++)
            Check(guard.Preview(high, Now()).CpuHighSamples == 1 && guard.RemainingConfirmationMilliseconds == 1500,
                "Preview counted an epoch twice or renewed its deadline.");
        clock += 100;
        var cold = guard.Observe(Sample(Now(), 94), Now());
        Check(cold.CpuHighSamples == 0 && !cold.CpuConfirmationPending && guard.RemainingConfirmationMilliseconds is null,
            "A timely valid cool acquisition must clear the streak and budget.");
        clock += 100;
        Check(guard.Observe(Sample(Now(), 98), Now()).CpuHighSamples == 1 &&
            guard.RemainingConfirmationMilliseconds == 2000, "A new streak needs its own bounded budget.");

        guard = Ready();
        for (var i = 1; i <= 5; i++)
        {
            clock = 100 + (i - 1) * 400;
            var decision = guard.Observe(Sample(Now(), 98), Now());
            Check(decision.CpuHighSamples == i && decision.Closed == (i == 5) &&
                decision.EffectiveSafety.CustomControlPermitted == (i < 5),
                "Five unique consecutive high epochs must close admission before two seconds.");
        }
        Check(guard.Preview(guard.LastObservedSnapshot!, Now()).EffectiveSafety.ThermalEmergency,
            "Confirmed CPU heat lost its handoff classification.");

        guard = Ready();
        high = Sample(Now(), 98);
        guard.Observe(high, Now());
        clock += 1500;
        high = Sample(Now(), 98);
        Check(guard.Observe(high, Now()).CpuHighSamples == 2, "Slow valid second sample rejected prematurely.");
        clock = 2100;
        Check(high.IsFanTelemetryFreshAt(Now()) && guard.Preview(high, Now()).Closed,
            "Two-second thermal expiry must close even when ordinary freshness is still valid.");
        Reject(guard.EnsureOpen);
        Reject(() => _ = guard.RemainingConfirmationMilliseconds);
        clock += 1;
        Check(guard.Observe(Sample(Now(), 60), Now()).Closed, "Late cooling reopened a closed admission.");
        guard = Ready();
        guard.Observe(Sample(Now(), 98), Now());
        clock = 2100;
        Check(guard.Observe(Sample(Now(), 60), Now()).Closed,
            "A late cool sample must not reset expiry before it is checked.");

        foreach (var kind in new[] { "duplicate", "backwards", "gap", "replacement", "clock" })
        {
            guard = Ready();
            high = Sample(Now(), 98);
            guard.Observe(high, Now());
            var decision = kind switch
            {
                "duplicate" => guard.Observe(high, Now()),
                "backwards" => guard.Observe(Sample(Now().AddMilliseconds(-1), 98), Now()),
                "replacement" => guard.Preview(high with { CpuTemperatureC = 60 }, Now()),
                "clock" => Regress(),
                _ => Gap()
            };
            Check(decision.Closed && !decision.EffectiveSafety.CustomControlPermitted,
                "Epoch/clock discontinuity admitted: " + kind);
            Hp8C40AutomaticThermalAdmissionDecision Regress() { clock--; return guard.Preview(high, Now()); }
            Hp8C40AutomaticThermalAdmissionDecision Gap()
            {
                // Below-threshold gap tests continuity independently of the hot deadline.
                guard = Ready();
                clock = 3100;
                return guard.Observe(Sample(Now()), Now());
            }
        }
        foreach (var (cpu, gpu) in new[] { (99d, 47d), (63d, 87d) })
        {
            guard = Ready();
            var decision = guard.Observe(Sample(Now(), cpu, gpu) with { CpuPackagePowerW = null }, Now());
            Check(decision.Closed && decision.EffectiveSafety.ThermalEmergency,
                "Known hard CPU/GPU heat must stop even if another sensor is incomplete.");
        }
        foreach (var invalid in new Func<TelemetrySnapshot, TelemetrySnapshot>[]
        {
            s => s with { CpuPackagePowerW = null }, s => s with { CpuLoadPercent = double.NaN },
            s => s with { CpuCoreTemperatures = [] }, s => s with { FanSampleAgeMilliseconds = 3000 },
            s => s with { GpuName = "different GPU" }
        })
        {
            guard = Ready();
            Check(guard.Observe(invalid(Sample(Now(), 98)), Now()).Closed,
                "CPU confirmation must not admit incomplete, implausible, stale or mismatched telemetry.");
        }
        clock = 0;
        guard = new(Hardware, () => clock);
        Check(guard.Observe(Sample(Now(), 98), Now()).Closed, "Hot startup initialized custom control.");
        guard = new(Hardware, () => clock);
        var warmup = guard.Observe(Sample(Now()) with { CpuPackagePowerW = null }, Now());
        Check(!warmup.Closed && !warmup.EffectiveSafety.CustomControlPermitted, "Incomplete cold startup cannot write.");
        clock++;
        Check(guard.Observe(Sample(Now()), Now()).EffectiveSafety.CustomControlPermitted, "Valid cold warmup never admitted.");
        Reject(() => _ = new WmiFanThermalAdmission(Hardware with { BiosVersion = "F.19" }));
        var rejectedEvidence = System.Text.Json.JsonSerializer.Serialize(
            new { Snapshot = Sample(Now()) with { CpuLoadPercent = double.NaN, GpuPowerW = double.PositiveInfinity } },
            WmiFanExperiment.ThermalDiagnosticJson);
        Check(rejectedEvidence.Contains("\"NaN\"") && rejectedEvidence.Contains("\"Infinity\""),
            "Rejected sensor diagnostics must preserve named nonfinite values without masking the admission fault.");

        // Counterfactual replay of 9c9616: preceding level 31, package 97 / core 98,
        // 35.284 W and cool GPU. No claim about the unseen following hardware epoch.
        guard = Ready();
        var policy = new WmiFanInertiaPolicy(WmiFanExperiment.CreatePolicy());
        // Seed the prior accepted actuator level; the capture had reached 31
        // through earlier filtered history rather than its last 63 C reading.
        var baseline = policy.Evaluate(new(origin, 71, 5, 17, 47, 14, 0));
        var captured = Sample(Now(), 98, power: 35.284);
        var admitted = guard.Observe(captured, Now());
        var target = policy.Evaluate(new(Now(), 98, 35.284, 16.547, 47, 14, 0));
        Check(baseline.EqualFanLevel == 31 && target.EqualFanLevel == 35 && target.ThermalOverride &&
            admitted.EffectiveSafety.CustomControlPermitted && captured.CpuControlTemperatureC == 98,
            "Pending raw heat must raise 31 -> 35 via thermal override, without averaging it away.");
        var calls = new List<HpBiosRequest>();
        var session = new WmiFanSession(r => { calls.Add(r); return 0; }, _ => { });
        session.Apply(31, true);
        session.Apply(target.EqualFanLevel!.Value, admitted.EffectiveSafety.CustomControlPermitted);
        clock += 2000;
        Reject(() => session.Apply(39, guard.Preview(captured, Now()).EffectiveSafety.CustomControlPermitted));
        session.Recover();
        Check(calls.Count == 4 && calls[2].Payload[0] == 255 && calls[3].CommandType == 0x1A,
            "Thermal closure must preserve FF/FF then LegacyDefault recovery.");

        await TestAcquisitionExpiryAsync();
        await TestBlockedNativeAsync();
    }

    private static async Task TestAcquisitionExpiryAsync()
    {
        using var slot = new SemaphoreSlim(1, 1);
        var origin = DateTimeOffset.UtcNow;
        long clock = 0;
        var guard = new WmiFanThermalAdmission(Hardware, () => clock);
        guard.Observe(Sample(origin), origin);
        clock = 100;
        guard.Observe(Sample(origin.AddMilliseconds(clock), 98), origin.AddMilliseconds(clock));
        var fans = new HpWmiFanProofReader(_ => { clock += 2000; return new(0, [37, 37]); },
            slot, () => clock, TimeSpan.FromSeconds(3));
        var snapshots = 0;
        var denied = false;
        try { await WmiFanExperiment.AcquireThermalSnapshotAsync(fans,
            () => { snapshots++; return Sample(origin.AddMilliseconds(clock)); }, () => { }, guard, CancellationToken.None); }
        catch (InvalidOperationException) { denied = true; }
        Check(denied && snapshots == 0 && slot.CurrentCount == 1,
            "Deadline after RPM return must stop before CPU/GPU sampling and release the completed native slot.");

        guard = new(Hardware);
        var now = DateTimeOffset.UtcNow;
        guard.Observe(Sample(now), now);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var nativeCalls = 0;
        fans = new(_ => { nativeCalls++; return new(0, [37, 37]); }, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(3));
        denied = false;
        try { await WmiFanExperiment.AcquireThermalSnapshotAsync(fans, () => Sample(now),
            () => canceled.Token.ThrowIfCancellationRequested(), guard, canceled.Token); }
        catch (OperationCanceledException) { denied = true; }
        Check(denied && nativeCalls == 0 && !guard.Preview(guard.LastObservedSnapshot!, now).Closed,
            "Caller cancellation must stop before native work without inventing a CPU deadline fault.");
    }

    private static async Task TestBlockedNativeAsync()
    {
        using var slot = new SemaphoreSlim(1, 1);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var telemetry = new HpWmiFanTelemetryReader(_ => throw new Exception("Unexpected competing query"),
            () => Environment.TickCount64, () => DateTimeOffset.UtcNow, slot);
        var fans = new HpWmiFanProofReader(_ =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Fixture native call did not unblock.");
            return new(0, [37, 37]);
        }, slot, () => Environment.TickCount64, TimeSpan.FromSeconds(3));
        var guard = new WmiFanThermalAdmission(Hardware);
        var now = DateTimeOffset.UtcNow;
        guard.Observe(Sample(now), now);
        now = now.AddTicks(1);
        guard.Observe(Sample(now, 98), now);
        var snapshots = 0;
        try
        {
            var acquisition = WmiFanExperiment.AcquireThermalSnapshotAsync(fans,
                () => { snapshots++; return Sample(DateTimeOffset.UtcNow); }, () => { }, guard, CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var denied = false;
            try { await acquisition.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && snapshots == 0 && slot.CurrentCount == 0 && telemetry.ReadCached(false) is null,
                "Logical thermal cancellation must retain an in-flight native slot and publish no expired cache.");
            Reject(guard.EnsureOpen);
        }
        finally
        {
            release.Set();
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await HpWmiFanSampleBroker.For(slot).WaitForQuiescenceAsync(drain.Token);
        }
        Check(slot.CurrentCount == 1 && telemetry.ReadCached(false) is null,
            "Late native completion must drain exactly once without publishing an abandoned sample.");
    }

    internal static void TestBoundary()
    {
        long clock = 0;
        var now = DateTimeOffset.UtcNow;
        var guard = new WmiFanThermalAdmission(Hardware, () => Interlocked.Read(ref clock));
        guard.Observe(Sample(now.AddMilliseconds(-1)), now.AddMilliseconds(-1));
        clock = 100;
        var high = Sample(now, 98);
        guard.Observe(high, now);
        WmiFanExperimentBoundary.SetAdmission(high, () => guard.EnsureDispatchAllowed(high, DateTimeOffset.UtcNow));
        var request = Hp8C40BiosFanControl.BuildSetFanLevelRequest(35, 35);
        WmiFanExperimentBoundary.EnsureRequestAllowed(request);
        var calls = 0;
        // Serialize admits after mutex acquisition; native client checks once more
        // immediately before InvokeMethod. Expire in between these two checks.
        Reject(() => WmiFanExperimentBoundary.Serialize(request, () =>
        {
            Interlocked.Exchange(ref clock, 2100);
            WmiFanExperimentBoundary.EnsureRequestAllowed(request);
            calls++;
            return new(0, []);
        }));
        Check(calls == 0 && guard.Preview(high, DateTimeOffset.UtcNow).CpuHighSamples == 1,
            "Pre-native expiry either dispatched or counted a second sample.");

        // Real mutex contention; expire the independent monotonic clock while
        // the caller waits. The callback must not be reached after acquisition.
        clock = 0;
        guard = new(Hardware, () => Interlocked.Read(ref clock));
        now = DateTimeOffset.UtcNow;
        guard.Observe(Sample(now.AddMilliseconds(-1)), now.AddMilliseconds(-1));
        clock = 100;
        high = Sample(now, 98);
        guard.Observe(high, now);
        WmiFanExperimentBoundary.SetAdmission(high, () => guard.EnsureDispatchAllowed(high, DateTimeOffset.UtcNow));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var waiting = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, @"Global\VictusFanControl.WmiFanExperiment.Native");
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5))) throw new Exception("Fixture could not hold native mutex.");
            try { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            finally { mutex.ReleaseMutex(); }
        });
        Check(held.Wait(TimeSpan.FromSeconds(5)), "Fixture mutex holder never started.");
        var waiter = Task.Run(() =>
        {
            waiting.Set();
            Reject(() => WmiFanExperimentBoundary.Serialize(request, () => { calls++; return new(0, []); }));
        });
        try
        {
            Check(waiting.Wait(TimeSpan.FromSeconds(5)) && !waiter.Wait(TimeSpan.FromMilliseconds(50)),
                "Fixture caller did not remain blocked behind the held mutex.");
            Interlocked.Exchange(ref clock, 2100);
        }
        finally { release.Set(); }
        Task.WhenAll(holder, waiter).GetAwaiter().GetResult();
        Check(calls == 0, "Thermal expiry while waiting for mutex dispatched native work.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        catch (ArgumentException) { return; }
        throw new Exception("Expected thermal rejection did not occur.");
    }
}
