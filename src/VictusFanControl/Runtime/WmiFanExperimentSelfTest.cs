using VictusFanControl.Control;
using VictusFanControl.Control.Adaptive;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Runtime;

internal static class WmiFanExperimentSelfTest
{
    internal static int Run(TextWriter output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vfc-wmi-experiment-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestFreshAcquisitionAsync().GetAwaiter().GetResult();
            TestInertia();
            var result = "{\"Pid\":31920,\"ExitCode\":1,\"StopReason\":\"Telemetry admission lost\",\"SamplesAdmitted\":true,\"NormalPhaseEnded\":true}";
            Check(WmiFanExperiment.ReadWorkerStopReason(result, 31920, 1) == "Telemetry admission lost",
                "Worker fault must be reported instead of the guardian's duration default.");
            Reject(() => WmiFanExperiment.ReadWorkerStopReason(result, 31584, 1));
            Reject(() => WmiFanExperiment.ReadWorkerStopReason(result, 31920, 0));
            Reject(() => WmiFanExperiment.ReadWorkerStopReason(result.Replace("\"NormalPhaseEnded\":true", "\"NormalPhaseEnded\":false"), 31920, 1));
            var noSamples = result.Replace("\"ExitCode\":1", "\"ExitCode\":0").Replace("\"SamplesAdmitted\":true", "\"SamplesAdmitted\":false");
            Reject(() => WmiFanExperiment.ReadWorkerStopReason(noSamples, 31920, 0));
            Check(WmiFanExperiment.CanRetireLease(true, false, false, true), "Completed shadow lease retained unnecessarily.");
            Check(WmiFanExperiment.CanRetireLease(false, true, false, true), "Accepted completed release lease retained unnecessarily.");
            Check(!WmiFanExperiment.CanRetireLease(true, false, true, true), "Unknown shadow native call may not retire the lease.");
            Check(!WmiFanExperiment.CanRetireLease(false, true, false, false), "Running worker may not retire the lease.");
            Check(!WmiFanExperiment.CanRetireLease(false, false, false, true), "Unaccepted release may not retire the lease.");
            var policy = new WmiFanInertiaPolicy(WmiFanExperiment.CreatePolicy());
            var t = DateTimeOffset.UtcNow;
            var idle = policy.Evaluate(new(t, 40, 10, 0, 35, 10, 0));
            var hot = policy.Evaluate(new(t.AddSeconds(1), 90, 115, 100, 84, 140, 100));
            Check(idle.Accepted && idle.EqualFanLevel == 30 && hot.Accepted && hot.EqualFanLevel == 34 && hot.RawDemandLevel == 50,
                "Real experiment policy must initialize and retain the 30..50 floor and slew limit.");
            var calls = new List<HpBiosRequest>();
            var intents = 0;
            var session = new WmiFanSession(r => { calls.Add(r); return 0; }, _ => intents++);
            Reject(() => session.Apply(30, false));
            Reject(() => session.Apply(29, true));
            Reject(() => session.Apply(51, true));
            Check(calls.Count == 0 && intents == 0, "Admission/range rejection must happen before intent/dispatch.");
            Check(session.Apply(30, true) && session.MayHaveWritten, "First accepted normal command lost.");
            Check(!session.Apply(30, true) && calls.Count == 1 && intents == 1, "Unchanged target sent again.");
            session.Apply(34, true);
            session.Recover();
            Check(calls.Count == 4 && calls[2].Payload[0] == 255 && calls[2].CommandType == 0x2E && calls[3].CommandType == 0x1A,
                "Recovery must send FF/FF then LegacyDefault.");
            Check(session.Phase == WmiFanSessionPhase.ReleaseAccepted && session.LastAcceptedLevel is null,
                "Recovery must close normal state without fabricating ownership.");
            Reject(() => session.Apply(30, true));
            session.Recover();
            Check(calls.Count == 4, "Accepted release must not be repeated by this lifecycle.");

            var blockedIntent = new WmiFanSession(_ => throw new Exception("dispatch after failed journal"),
                _ => throw new IOException("journal unavailable"));
            Reject(() => blockedIntent.Apply(30, true));
            Check(!blockedIntent.MayHaveWritten, "Failed intent persistence may not dispatch.");

            foreach (var throwInstead in new[] { false, true })
            {
                calls.Clear();
                var failed = new WmiFanSession(r =>
                {
                    calls.Add(r);
                    if (r.CommandType == 0x2E)
                    {
                        if (throwInstead) throw new IOException("native fixture failure");
                        return 1;
                    }
                    return 0;
                }, _ => { });
                Reject(() => failed.Apply(30, true));
                Check(failed.MayHaveWritten && failed.Phase == WmiFanSessionPhase.Recovering,
                    "Uncertain normal failure must close admission and require recovery.");
                Reject(() => failed.Recover());
                Check(calls.Count == 3 && calls[^1].CommandType == 0x1A && failed.Phase == WmiFanSessionPhase.RecoveryFailed,
                    "Release failure must still attempt LegacyDefault and retain failure.");
                Reject(() => failed.Apply(30, true));
            }

            var rpm = Hp8C40BiosFanControl.BuildGetFanLevelRequest();
            var level = Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 30);
            var release = Hp8C40BiosFanControl.BuildReleaseFanLevelRequest();
            Check(WmiFanExperimentBoundary.IsAllowed(rpm, false, false, false), "Shadow RPM denied.");
            Check(!WmiFanExperimentBoundary.IsAllowed(level, false, false, false), "Shadow setter admitted.");
            Check(WmiFanExperimentBoundary.IsAllowed(level, true, false, false), "Normal setter denied.");
            Check(!WmiFanExperimentBoundary.IsAllowed(level, true, false, true), "Stop signal setter admitted.");
            Check(!WmiFanExperimentBoundary.IsAllowed(level, true, true, false), "Recovery normal setter admitted.");
            Check(!WmiFanExperimentBoundary.IsAllowed(release, true, false, false), "Release admitted in normal phase.");
            Check(WmiFanExperimentBoundary.IsAllowed(release, true, true, true), "Recovery blocked by stop signal.");
            foreach (var bad in new[] { level with { Payload = [30, 31, 0, 0] },
                level with { Payload = [30, 30, 1, 0] }, level with { Payload = [29, 29, 0, 0] },
                level with { Command = 1 }, level with { OutputSize = 4 }, rpm with { Payload = [1, 0, 0, 0] } })
                Check(!WmiFanExperimentBoundary.IsAllowed(bad, true, false, false), "Out-of-contract WMI request admitted.");
            Reject(() => WmiFanExperimentOptions.Parse(["--wmi-fan-experiment", "--session-dir", directory, "--probe-8c40-setpoint"]));
            Reject(() => WmiFanExperimentOptions.Parse(["--wmi-fan-experiment", "--session-dir", directory, "--duration-seconds", "601"]));
            Check(!WmiFanExperimentOptions.Parse(["--wmi-fan-experiment", "--session-dir", directory]).Control,
                "Experiment must default to shadow.");

            // Real low-level constructor refusal, before any module I/O.
            WmiFanExperimentBoundary.Enable(directory, true);
            Reject(() => { using var ec = new AcpiEcReader("missing-experiment-fixture.bin"); });
            Check(WmiOnlyInvestigationPolicy.DeniedEcAccesses == 1, "EC boundary did not record rejection.");
            WmiFanExperiment.WriteJson(Path.Combine(directory, "intent.json"), new { Level = 30 });
            Check(File.ReadAllText(Path.Combine(directory, "intent.json")).Contains("30"), "Durable intent lost.");
            WmiFanExperimentBoundary.MarkNativeStart(level);
            // A completion-unknown marker rejects before invoking the supplied transport.
            Reject(() => WmiFanExperimentBoundary.Serialize(level, () => throw new Exception("native call admitted")));
            Check(File.Exists(Path.Combine(directory, "native-inflight.json")), "Unknown native completion erased.");
            WmiFanExperimentBoundary.MarkNativeReturned();
            File.WriteAllText(WmiFanExperimentBoundary.StopPath, "stop");
            Reject(() => WmiFanExperimentBoundary.EnsureRequestAllowed(level));
            WmiFanExperimentBoundary.BeginRecovery();
            WmiFanExperimentBoundary.EnsureRequestAllowed(release);
            output.WriteLine("PASS: WMI time-based inertia, immediate thermal increases, sequential fresh acquisition, capture expiry races, failed/canceled reads, normal/recovery lifecycle, failed intent, ambiguous dispatch, independent recovery steps, whitelist, shadow default, EC prohibition and retained native completion.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: " + ex); return 1; }
        finally { Directory.Delete(directory, true); }
    }

    private static void TestInertia()
    {
        var config = WmiFanExperiment.CreatePolicy() with
        {
            CpuPowerCurve = [new(0, 30), new(100, 50)]
        };
        var origin = new DateTimeOffset(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);
        AdaptiveFanPolicyInput Input(double seconds, int level = 30) =>
            new(origin.AddSeconds(seconds), 40, (level - 30) * 5, 0, 35, 0, 0);
        var policy = new WmiFanInertiaPolicy(config);
        Check(policy.Evaluate(Input(0)).EqualFanLevel == 30, "Inertia changed the initial floor.");
        Check(policy.Evaluate(Input(1, 32)).EqualFanLevel == 30, "Small power spike was sent immediately.");
        Check(policy.Evaluate(Input(2, 31)).EqualFanLevel == 30, "Small increase confirmed too early.");
        Check(policy.Evaluate(Input(3, 32)).EqualFanLevel == 31, "Increase must use the lowest sustained demand.");
        Check(policy.Evaluate(Input(4, 32)).EqualFanLevel == 31, "Each small step needs its own window.");
        Check(policy.Evaluate(Input(5)).EqualFanLevel == 31, "One-level hysteresis was lost.");
        Check(policy.Evaluate(Input(6, 32)).EqualFanLevel == 31, "Interrupted increase confirmation survived.");
        Check(policy.Evaluate(Input(7, 40)).EqualFanLevel == 35, "Large demand must bypass delay and preserve up-step limit.");

        foreach (var gpu in new[] { false, true })
        {
            policy = new(config);
            policy.Evaluate(Input(0));
            policy.Evaluate(Input(1, 31)); // A small power increase is still pending.
            var hot = Input(1.5) with { CpuEffectiveTemperatureC = gpu ? 40 : 78, GpuTemperatureC = gpu ? 72 : 35 };
            Check(policy.Evaluate(hot).EqualFanLevel == 34,
                "CPU/GPU temperature-driven increases must bypass confirmation and preserve slew.");
        }

        foreach (var cadence in new[] { 0.5, 2.5 })
        {
            policy = new(config);
            policy.Evaluate(Input(0, 40));
            var start = cadence;
            for (var s = start; s < start + 12; s += cadence)
                Check(policy.Evaluate(Input(s)).EqualFanLevel == 40, "Decrease counted samples instead of 12 real seconds.");
            Check(policy.Evaluate(Input(start + 12)).EqualFanLevel == 39, "Sustained decrease did not occur at 12 seconds.");
            Check(policy.Evaluate(Input(start + 12 + cadence)).EqualFanLevel == 39, "Second down-step bypassed confirmation.");
        }

        policy = new(config);
        policy.Evaluate(Input(0, 40));
        for (var s = 1; s <= 10; s++) policy.Evaluate(Input(s));
        policy.Evaluate(Input(11, 39)); // Inside the one-level decrease deadband.
        for (var s = 12; s < 24; s++)
            Check(policy.Evaluate(Input(s)).EqualFanLevel == 40, "Deadband did not reset the decrease window.");
        Check(policy.Evaluate(Input(24)).EqualFanLevel == 39, "Restarted decrease window never completed.");

        foreach (var invalid in new[] { "input", "duplicate", "backwards", "gap" })
        {
            policy = new(config);
            policy.Evaluate(Input(0, 40));
            for (var s = 1; s <= 10; s++) policy.Evaluate(Input(s));
            var bad = invalid switch
            {
                "input" => Input(11) with { CpuPackagePowerW = double.NaN },
                "duplicate" => Input(10),
                "backwards" => Input(9),
                _ => Input(14)
            };
            var rejected = policy.Evaluate(bad);
            Check(!rejected.Accepted && rejected.EqualFanLevel is null,
                "Inertia fabricated an accepted hold across invalid/stale/discontinuous telemetry.");
            var next = invalid == "gap" ? 15 : 12;
            Check(policy.Evaluate(Input(next)).EqualFanLevel == 40, "Rejected input did not clear confirmation.");
        }

        // Acoustic oscillation fixture: short cool periods between recurring
        // load bursts. Count actual session dispatches, not repeated decisions.
        var original = new AdaptiveFanPolicyEngine(config);
        policy = new(config);
        var oldWrites = 0;
        var newWrites = 0;
        var oldSession = new WmiFanSession(_ => { oldWrites++; return 0; }, _ => { });
        var newSession = new WmiFanSession(_ => { newWrites++; return 0; }, _ => { });
        for (var s = 0; s <= 39; s++)
        {
            var input = Input(s, s % 13 == 0 || s % 13 >= 10 ? 34 : 30);
            oldSession.Apply(original.Evaluate(input).EqualFanLevel!.Value, true);
            newSession.Apply(policy.Evaluate(input).EqualFanLevel!.Value, true);
        }
        Check(oldWrites > newWrites && newWrites == 1,
            "Short cooling fluctuations should no longer generate repeated down/up orders.");
        Reject(() => newSession.Apply(34, false));
        newSession.Recover();
        Check(newWrites == 3 && newSession.Phase == WmiFanSessionPhase.ReleaseAccepted,
            "Inertia must not delay admission refusal or the two recovery requests.");
    }

    private static async Task TestFreshAcquisitionAsync()
    {
        using var slot = new SemaphoreSlim(1, 1);
        long clock = 0;
        var origin = DateTimeOffset.UtcNow;
        var periodicCalls = 0;
        var freshCalls = 0;
        using var telemetry = new HpWmiFanTelemetryReader(_ =>
        {
            periodicCalls++;
            throw new Exception("Experiment cache queued a competing periodic read.");
        }, () => clock, () => origin.AddMilliseconds(clock), slot);
        var fans = new HpWmiFanProofReader(r =>
        {
            Check(r.CommandType == 0x2D && r.OutputSize == 128, "Fresh acquisition must be RPM-only.");
            freshCalls++;
            clock += 385; // 76d955: native WMI returns successfully after a queued change expired.
            return new(0, [37, 37]);
        }, slot, () => clock, TimeSpan.FromSeconds(3));

        TelemetrySnapshot ReadSnapshot()
        {
            var timestamp = origin.AddMilliseconds(clock);
            clock += 520; // CPU/GPU sampling duration from d3dffc.
            var sample = telemetry.ReadCached(scheduleQuery: false);
            return new(timestamp, "CPU", 55, 16, 9,
                Hp8C40TargetProfile.ExpectedGpuName, 48, 12, 0,
                sample?.CpuNominalRpm, sample?.GpuNominalRpm)
            {
                CpuExpectedPhysicalCoreCount = 14,
                CpuCoreTemperatures = Enumerable.Range(0, 14)
                    .Select(i => new CpuCoreTemperatureSample(i, i, "Performance", 55)).ToArray(),
                FanTelemetrySource = "HP-WMI-ACPI-2D", FanRpmResolution = 100,
                FanSampledAtUtc = sample?.SampledAtUtc,
                FanSampleAgeMilliseconds = sample is null ? null : clock - sample.StartedAtMilliseconds,
                FanAgeCapturedAtUtc = origin.AddMilliseconds(clock)
            };
        }
        var hardware = new HardwareIdentity(Hp8C40TargetProfile.BoardManufacturer,
            Hp8C40TargetProfile.BoardProduct, Hp8C40TargetProfile.BoardVersion,
            Hp8C40TargetProfile.SystemManufacturer, Hp8C40TargetProfile.SystemProductName,
            Hp8C40TargetProfile.SystemSkuPrefix, Hp8C40TargetProfile.ValidatedBiosVersion);
        var seed = await WmiFanExperiment.AcquireSnapshotAsync(fans, ReadSnapshot, () => { }, CancellationToken.None);
        var oldStart = clock - seed.FanSampleAgeMilliseconds!.Value;
        clock = oldStart + 2704;
        Check(seed.IsFanTelemetryFreshAt(origin.AddMilliseconds(clock)), "76d955 fixture must start with a barely fresh sample.");
        var renewed = await WmiFanExperiment.AcquireSnapshotAsync(fans, ReadSnapshot, () => { }, CancellationToken.None);
        Check(!seed.IsFanTelemetryFreshAt(origin.AddMilliseconds(clock)), "Old admission must expire while acquiring the next RPM sample.");
        Check(SafetyGate.Evaluate(hardware, SystemState.Healthy, renewed, origin.AddMilliseconds(clock), true).CustomControlPermitted,
            "Sequential acquisition must supply new RPM metadata before fresh CPU/GPU sampling.");
        Check(renewed.Timestamp > seed.Timestamp && renewed.FanSampleAgeMilliseconds == 905,
            "Neither old CPU/GPU data nor old fan age may be relabeled as fresh.");
        clock = oldStart + 7000; // d3dffc: old cache actually expired; no missing-metadata fallback.
        Check(telemetry.ReadCached(scheduleQuery: false) is null, "Expired sample must still be unavailable.");
        renewed = await WmiFanExperiment.AcquireSnapshotAsync(fans, ReadSnapshot, () => { }, CancellationToken.None);
        Check(renewed.IsComplete && renewed.IsFanTelemetryFreshAt(origin.AddMilliseconds(clock)) && freshCalls == 3,
            "An expired old cache must be replaced by one real fresh acquisition per cycle.");
        clock += 2000; // Total fan age 2905, still valid.
        Check(renewed.IsFanTelemetryFreshAt(origin.AddMilliseconds(clock)), "Freshness limit changed prematurely.");
        clock += 95;
        Check(!renewed.IsFanTelemetryFreshAt(origin.AddMilliseconds(clock)), "3000-ms limit was relaxed.");
        telemetry.ReadCached(scheduleQuery: false);
        Check(periodicCalls == 0, "Passive experiment reads must never schedule periodic work.");

        var snapshots = 0;
        foreach (var response in new HpBiosResponse[] { new(1, [37, 37]), new(0, [255, 37]) })
        {
            var rejected = new HpWmiFanProofReader(_ => response, slot, () => clock, TimeSpan.FromSeconds(3));
            var denied = false;
            try { await WmiFanExperiment.AcquireSnapshotAsync(rejected, () => { snapshots++; return renewed; }, () => { }, CancellationToken.None); }
            catch (InvalidDataException) { denied = true; }
            Check(denied && snapshots == 0, "Rejected query must stop before CPU/GPU sampling or cache fallback.");
        }
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var canceledRead = false;
        try { await WmiFanExperiment.AcquireSnapshotAsync(fans, () => { snapshots++; return renewed; },
            () => canceled.Token.ThrowIfCancellationRequested(), canceled.Token); }
        catch (OperationCanceledException) { canceledRead = true; }
        Check(canceledRead && snapshots == 0 && freshCalls == 3, "Stop before acquisition admitted native work.");

        var slow = new HpWmiFanProofReader(_ => { clock += 3000; return new(0, [37, 37]); },
            slot, () => clock, TimeSpan.FromSeconds(4));
        var expiredRead = false;
        try { await WmiFanExperiment.AcquireSnapshotAsync(slow, () => { snapshots++; return renewed; }, () => { }, CancellationToken.None); }
        catch (InvalidDataException) { expiredRead = true; }
        Check(expiredRead && snapshots == 0, "A completed but expired query may not produce a snapshot.");

        var guards = 0;
        var stoppedAfterRead = false;
        try { await WmiFanExperiment.AcquireSnapshotAsync(fans, () => { snapshots++; return renewed; },
            () => { if (++guards == 2) throw new OperationCanceledException("stop after native return"); }, CancellationToken.None); }
        catch (OperationCanceledException) { stoppedAfterRead = true; }
        Check(stoppedAfterRead && snapshots == 0, "Stop after native return admitted CPU/GPU sampling.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        catch (HpBiosCallException) { return; }
        catch (AggregateException) { return; }
        throw new Exception("Expected rejection did not occur.");
    }
}
