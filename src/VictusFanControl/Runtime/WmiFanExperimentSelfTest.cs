using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;

namespace VictusFanControl.Runtime;

internal static class WmiFanExperimentSelfTest
{
    internal static int Run(TextWriter output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "vfc-wmi-experiment-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
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
            output.WriteLine("PASS: WMI fan normal/recovery lifecycle, failed intent, ambiguous dispatch, independent recovery steps, whitelist, shadow default, EC prohibition and retained native completion.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: " + ex); return 1; }
        finally { Directory.Delete(directory, true); }
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
