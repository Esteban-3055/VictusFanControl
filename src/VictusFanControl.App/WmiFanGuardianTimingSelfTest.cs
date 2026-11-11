using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

internal static class WmiFanGuardianTimingSelfTest
{
    private static void Require(bool value, string detail)
    { if (!value) throw new InvalidOperationException(detail); }

    internal static async Task RunAsync()
    {
        static WmiFanGuardianClockSample Awake(ulong ms) => new(ms + 10_000, ms, ms);
        var timing = new WmiFanGuardianTiming();
        timing.BeginWork(TimeSpan.Zero, Awake(0));
        timing.CompleteWork(TimeSpan.FromMilliseconds(3400), Awake(3400));
        timing.BeginWork(TimeSpan.FromMilliseconds(3650), Awake(3650));
        Require(timing.Status is { StopReason: null, WorkMs: 3400, IdleMs: 250 },
            "Completed slow work was mistaken for a lifecycle gap.");
        timing.CompleteWork(TimeSpan.FromMilliseconds(10_450), Awake(10_450));
        timing.BeginWork(TimeSpan.FromMilliseconds(10_700), Awake(10_700));
        Require(timing.Status.StopReason is null, "Repeated long work accumulated a false idle/sleep gap.");

        timing = new(); timing.BeginWork(TimeSpan.Zero, Awake(0));
        timing.CompleteWork(TimeSpan.FromMilliseconds(4000), new(14_000, 500, 500));
        Require(timing.Status is { StopReason: "SYSTEM_SLEEP_GAP", SleepLowerBoundMs: 3500 },
            "Suspend hidden inside synchronous work was discarded.");
        timing = new(); timing.BeginWork(TimeSpan.Zero, Awake(0)); timing.CompleteWork(TimeSpan.Zero, Awake(0));
        timing.BeginWork(TimeSpan.FromMilliseconds(4000), new(14_000, 500, 500));
        Require(timing.Status.StopReason == "SYSTEM_SLEEP_GAP", "Sleep during idle was not fenced.");
        timing = new(); timing.BeginWork(TimeSpan.Zero, Awake(0)); timing.CompleteWork(TimeSpan.Zero, Awake(0));
        timing.BeginWork(TimeSpan.FromMilliseconds(3000), Awake(3000));
        Require(timing.Status.StopReason is null, "Three-second boundary changed.");
        timing.CompleteWork(TimeSpan.FromMilliseconds(3000), Awake(3000));
        timing.BeginWork(TimeSpan.FromMilliseconds(6001), Awake(6001));
        Require(timing.Status.StopReason == "SUPERVISOR_IDLE_GAP", "Unexplained awake idle gap was ignored.");

        timing = new(); timing.BeginWork(TimeSpan.Zero, new(10_000, 0, 0));
        timing.Check(TimeSpan.FromMilliseconds(3500), new(10_100, 100, 3500));
        Require(timing.Status.StopReason is null, "Preemption between clock reads invented sleep.");
        var rejected = false;
        try { timing.Check(TimeSpan.FromMilliseconds(3501), new(10_099, 3500, 3501)); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "Regressed Windows clock was accepted.");
        var first = WmiFanGuardianClockSample.Read();
        await Task.Delay(30);
        var second = WmiFanGuardianClockSample.Read();
        timing = new(); timing.Check(TimeSpan.Zero, first); timing.Check(TimeSpan.FromMilliseconds(50), second);
        Require(timing.Status.StopReason is null && second.TotalMs >= first.TotalMs,
            "Real Windows interrupt/active clock contract failed.");
        Console.WriteLine("PASS: slow work separated from idle; real Windows clocks; sleep during work/idle, scheduler stall, boundary, bracketed sampling and clock regression (no hardware IO).");
        await DelayedObservationAsync();
        await TimedOutObservationAsync();
        await LockedStatusReleaseAsync();
    }

    private static async Task DelayedObservationAsync()
    {
        var folder = Path.GetFullPath(Path.Combine("logs/guardian-timing-self-test", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        using var owner = Process.GetCurrentProcess();
        var clock = Stopwatch.StartNew();
        using var stopHeartbeat = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!stopHeartbeat.IsCancellationRequested)
            {
                WmiFanExperiment.WriteJson(Path.Combine(folder, "heartbeat.json"), new { Pid = owner.Id, ElapsedMs = clock.ElapsedMilliseconds });
                try { await Task.Delay(50, stopHeartbeat.Token); }
                catch (OperationCanceledException) when (stopHeartbeat.IsCancellationRequested) { break; }
            }
        });
        var delayed = false;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var args = new[] { "--fan-wmi-fixture-session", "--session-dir", folder, "--owner-pid", owner.Id.ToString(), "--owner-start", owner.StartTime.ToUniversalTime().Ticks.ToString() };
        var command = new CtgpCommand(Guid.NewGuid(), owner.Id, owner.StartTime.ToUniversalTime().Ticks, true);
        WmiFanExperiment.WriteJson(Path.Combine(folder, "ctgp-command.json"), command);
        await using var reader = new WmiFanGuiGuardianClient(fixture: true, fixtureDirectory: folder);
        var guardian = Task.Run(() => WmiFanGuiGuardianHost.RunAsync(args, fixture: true, fixtureBeforeObservation: () =>
        {
            // Delay a real host iteration only after activation. This callback is
            // rejected in production and never reaches a hardware transport.
            if (delayed || !File.Exists(Path.Combine(folder, "ctgp-reply.json"))) return;
            delayed = true; Thread.Sleep(3400); completed.SetResult();
        }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(Path.Combine(folder, "ready.json"))) await Task.Delay(20, timeout.Token);
            while (!File.Exists(Path.Combine(folder, "ctgp-reply.json"))) await Task.Delay(20, timeout.Token);
            while (!completed.Task.IsCompleted)
            {
                Require(reader.Ctgp is { Active: true, Failure: null }, "GUI lost its five-second confirmation during bounded slow work.");
                await Task.Delay(25, timeout.Token);
            }
            await completed.Task;
            // Exercise the NEXT iteration, where the former start-to-start
            // watchdog terminated an otherwise healthy cTGP session.
            await Task.Delay(750, timeout.Token);
            Require(!guardian.IsCompleted && !File.Exists(Path.Combine(folder, "stop.signal")),
                "Real host stopped after a completed 3.4-second observation.");
            Require(CtgpPublication.TryRead<CtgpStatus>(Path.Combine(folder, "ctgp-status.json")) is { Active: true, Failure: null },
                "Slow successful observation lost active cTGP.");
        }
        finally
        {
            WmiFanGuiGuardianHost.RequestStop(folder, "fixture-finished");
            try { await guardian.WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { stopHeartbeat.Cancel(); await heartbeat; }
        }
        Require(await guardian == 0, "Delayed-observation fixture did not cleanly release.");
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "guardian-report.json")));
        Require(report.RootElement.GetProperty("ExitReason").GetString() == "CLIENT_RELEASE" &&
            report.RootElement.GetProperty("CtgpRestored").GetBoolean() &&
            report.RootElement.GetProperty("GuardianLeaseRetired").GetBoolean(), "Delayed-observation fixture recovery was not verified.");
        Console.WriteLine("PASS: actual guardian host and GUI confirmation remain active across a 3.4-second completed observation and the next iteration; explicit release restores cTGP and retires its fixture lease. Zero hardware IO.");
    }

    private static async Task TimedOutObservationAsync()
    {
        var (folder, args) = CreateActiveFixture();
        using var started = new ManualResetEventSlim();
        using var returnFromRead = new ManualResetEventSlim();
        var guardian = WmiFanGuiGuardianHost.RunAsync(args, fixture:true, fixtureBeforeObservation:() =>
        {
            started.Set();
            if (!returnFromRead.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Fixture did not release its simulated WMI call.");
        });
        try
        {
            Require(started.Wait(TimeSpan.FromSeconds(5)), "Timed observation never entered the fixture.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(folder, "stop.signal"))) await Task.Delay(20, timeout.Token);
            Require(!guardian.IsCompleted && File.Exists(Path.Combine(folder, "fixture-lease.json")) &&
                !File.Exists(Path.Combine(folder, "guardian-report.json")), "Deadline falsely completed the active read or restored while it was pending.");
        }
        finally
        {
            returnFromRead.Set();
            await guardian.WaitAsync(TimeSpan.FromSeconds(15));
        }
        CheckRelease(folder, await guardian, "CTGP_OBSERVATION_TIMEOUT", expectStatusFailure:false);
        Console.WriteLine("PASS: a cTGP observation exceeding five seconds closes admission, retains its lease while pending, and restores after native completion (zero hardware IO).");
    }

    private static async Task LockedStatusReleaseAsync()
    {
        var (folder, args) = CreateActiveFixture();
        var guardian = WmiFanGuiGuardianHost.RunAsync(args, fixture:true);
        using var reader = File.Open(Path.Combine(folder, "ctgp-status.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        WmiFanGuiGuardianHost.RequestStop(folder, "CLIENT_RELEASE");
        CheckRelease(folder, await guardian.WaitAsync(TimeSpan.FromSeconds(15)), "CLIENT_RELEASE", expectStatusFailure:true);
        Console.WriteLine("PASS: locked cTGP status does not prevent fan release, LegacyDefault, or lease retirement (zero hardware IO).");
    }

    private static (string Folder, string[] Args) CreateActiveFixture()
    {
        var folder = Path.GetFullPath(Path.Combine("logs/guardian-timing-self-test", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        using var owner = Process.GetCurrentProcess();
        var start = owner.StartTime.ToUniversalTime().Ticks;
        WmiFanExperiment.WriteJson(Path.Combine(folder, "heartbeat.json"), new { Pid = owner.Id, ElapsedMs = 1L });
        WmiFanExperiment.WriteJson(Path.Combine(folder, "ctgp-command.json"), new CtgpCommand(Guid.NewGuid(), owner.Id, start, true));
        WmiFanExperiment.WriteJson(Path.Combine(folder, "write-intent.json"), new { OwnerPid = owner.Id });
        return (folder, ["--fan-wmi-fixture-session", "--session-dir", folder, "--owner-pid", owner.Id.ToString(), "--owner-start", start.ToString()]);
    }

    private static void CheckRelease(string folder, int exit, string reason, bool expectStatusFailure)
    {
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "guardian-report.json")));
        var result = report.RootElement;
        Require(exit == 0 && result.GetProperty("ExitReason").GetString() == reason &&
            result.GetProperty("CtgpRestored").GetBoolean() && result.GetProperty("ReleaseRequestAccepted").GetBoolean() &&
            result.GetProperty("LegacyDefaultRequestAccepted").GetBoolean() && result.GetProperty("GuardianLeaseRetired").GetBoolean() &&
            (result.GetProperty("CtgpStatusPublicationFailure").ValueKind == JsonValueKind.String) == expectStatusFailure,
            "Fixture failed to verify the expected release and publication result.");
    }
}
