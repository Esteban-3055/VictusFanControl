using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static class ProductAutomaticActivationSelfTest
{
    // This suite has no controls; do not block continuations on the caller's WinForms synchronization context.
    internal static void Run(Action<bool,string> require) => Task.Run(() => RunAsync(require)).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<bool,string> require)
    {
        TestSourceTransitions(require);
        var activation = new ProductAutomaticActivation();
        var profiles = new ProductProfiles { CpuEnabled = false, GpuEnabled = false };
        var ticket = activation.Begin(profiles);
        require(ticket.Performance.CpuEnabled && ticket.Performance.GpuEnabled && !profiles.CpuEnabled && !profiles.GpuEnabled,
            "Automatic did not freeze both enabled domains independently of the saved toggles.");
        require(!ReferenceEquals(ticket.Profiles.Ac.Fan, profiles.Ac.Fan) && ticket.Performance.AcPl1Watts == profiles.Ac.CpuPl1Watts &&
            ticket.Performance.BatteryGpuMaximumMHz == profiles.Battery.GpuMaximumMHz, "Automatic lost the frozen AC/Battery preferences.");
        bool duplicate = false; try { activation.Begin(profiles); } catch (InvalidOperationException) { duplicate = true; }
        require(duplicate && activation.IsCurrent(ticket), "Duplicate preparation superseded the admitted click.");
        var order = new List<string>();
        Task Mark(string step) { order.Add(step); return Task.CompletedTask; }
        await activation.RunAsync(ticket, () => Mark("admit"), () => Mark("limits"), () => Mark("fans"));
        require(order.SequenceEqual(new[] { "admit", "limits", "fans" }) && !activation.Pending, "Fans started before the prerequisite limits.");
        int refresh = 0, enable = 0;
        Task Refresh() { ++refresh; return Task.CompletedTask; }
        Task Enable() { ++enable; return Task.CompletedTask; }
        await ProductAutomaticActivation.PreparePerformanceAsync(ticket.Performance, false, null, Refresh, Enable);
        await ProductAutomaticActivation.PreparePerformanceAsync(ticket.Performance, true, ticket.Performance with { }, Refresh, Enable);
        require(refresh == 1 && enable == 1, "Matching live Guardian was rewritten or a new owner did not apply once.");
        async Task Reject(Task work, string message)
        {
            bool failed = false; try { await work; } catch (InvalidOperationException) { failed = true; } catch (IOException) { failed = true; }
            require(failed, message);
        }
        await Reject(ProductAutomaticActivation.PreparePerformanceAsync(ticket.Performance, true,
            ticket.Performance with { AcPl1Watts = 18, AcPl2Watts = 36 }, Refresh, Enable), "Different live limits were silently replaced.");
        await Reject(ProductAutomaticActivation.PreparePerformanceAsync(ticket.Performance, true,
            ticket.Performance with { CpuEnabled = false }, Refresh, Enable), "A partial live Guardian was silently reused.");
        await Reject(ProductAutomaticActivation.PreparePerformanceAsync(ticket.Performance, true, null, Refresh, Enable), "Unresolved owner was silently replaced.");
        require(refresh == 1 && enable == 1, "Rejected existing owners dispatched new operations.");
        foreach (var source in new[] { "Ac", "Battery" })
        {
            var status = new PerformanceGuardianResponse(1, Guid.NewGuid(), ticket.Performance.TargetProfileId,
                true, "SESSION_ENABLED", "fixture", "SessionEnabled", true, true, true,
                "Active", "ActiveUnverified", PowerSource: source);
            bool Ready(PerformanceGuardianResponse? candidate, bool fresh = true, string? actualSource = null,
                PerformanceGuiSessionConfiguration? applied = null) => ProductAutomaticActivation.PerformanceReady(
                    ticket.Performance, applied ?? ticket.Performance, candidate, fresh, actualSource ?? source);
            require(Ready(status), "Confirmed AC/Battery limits did not admit the curve.");
            require(!Ready(status, false) && !Ready(status, actualSource: "Unknown") &&
                !Ready(status, actualSource: source == "Ac" ? "Battery" : "Ac") && !Ready(null) &&
                !Ready(status with { CpuEnabled = false }) && !Ready(status with { GpuEnabled = false }) &&
                !Ready(status with { CpuState = "Recovering" }) && !Ready(status with { GpuState = "Failed" }) &&
                !Ready(status with { Ok = false }) && !Ready(status with { SessionEnabled = false }) &&
                !Ready(status with { RuntimeFailure = "fixture fault" }) && !Ready(status, applied: ticket.Performance with { AcGpuMaximumMHz = 1800 }),
                "Missing, stale, partial, failed, mismatched or wrong-source limits admitted Automatic.");
        }
        ticket = activation.Begin(profiles); activation.Cancel(); order.Clear();
        await Reject(activation.RunAsync(ticket, () => Mark("admit"), () => Mark("limits"), () => Mark("fans")), "Cancelled queued click rearmed.");
        require(order.Count == 0, "Cancelled queued click started a hardware stage.");
        ticket = activation.Begin(profiles); order.Clear();
        await Reject(activation.RunAsync(ticket, () => Mark("admit"), () => Task.FromException(new IOException("GPU rejected")), () => Mark("fans")),
            "Performance failure did not block the curve.");
        require(order.SequenceEqual(new[] { "admit" }) && !activation.Pending, "Performance failure started fans or retained preparation.");
        ticket = activation.Begin(profiles); order.Clear();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = activation.RunAsync(ticket, () => Mark("admit"), () => gate.Task, () => Mark("fans"));
        activation.Cancel(); var replacement = activation.Begin(profiles); gate.SetResult();
        await Reject(old, "Firmware during slow Performance Apply allowed late Automatic.");
        require(order.SequenceEqual(new[] { "admit" }) && activation.Pending && activation.IsCurrent(replacement),
            "A stale completion cleared the replacement intent or entered Custom.");
        await activation.RunAsync(replacement, () => Mark("new-admit"), () => Mark("new-limits"), () => Mark("new-fans"));
        ticket = activation.Begin(profiles); order.Clear();
        await activation.ReleaseLimitsAsync(() => Mark("firmware"), () => Mark("release-limits"));
        require(order.SequenceEqual(new[] { "firmware", "release-limits" }) && !activation.IsCurrent(ticket),
            "Removing prerequisite limits did not cancel pending Automatic and restore fans first.");
        order.Clear();
        await Reject(activation.ReleaseLimitsAsync(() => Task.FromException(new IOException("WMI release unresolved")), () => Mark("release-limits")),
            "Unresolved fan release was hidden.");
        require(order.Count == 0, "CPU/GPU were released after an unresolved fan restore.");
        Console.WriteLine("PASS: Automatic coupled activation order, frozen preferences, Guardian reuse/partial failure, source/freshness fences and late cancellation.");
    }
    private static void TestSourceTransitions(Action<bool,string> require)
    {
        long clock = 0;
        var gate = new ProductAutomaticSourceTransition(() => clock);
        var config = new ProductProfiles().PerformanceConfiguration();
        var status = new PerformanceGuardianResponse(1, Guid.NewGuid(), config.TargetProfileId,
            true, "fixture", "fixture", "SessionEnabled", true, true, true, "Active", "ActiveUnverified", PowerSource: "Ac");
        bool Observe(string source, string selected, PerformanceGuardianResponse? response, bool fresh = true) =>
            gate.Observe(source, selected, config, config, response, fresh);
        require(Observe("Ac", "Ac", status) && !gate.Pending, "Stable AC incorrectly started a transition.");
        require(!Observe("Battery", "Ac", status) && gate.Pending, "Source mismatch admitted the old limits.");
        clock = 1000;
        require(!Observe("Battery", "Ac", status with { PowerSource = "Battery" }), "Cached response admitted the handoff.");
        status = status with { RequestId = Guid.NewGuid(), PowerSource = "Battery" };
        require(Observe("Battery", "Ac", status), "Fresh Battery limits did not admit a stable handoff.");
        gate.Reset();
        require(Observe("Battery", "Battery", status) && !gate.Pending, "Duplicate source observation started another transition.");
        require(!Observe("Ac", "Battery", status), "Battery-to-AC bypassed reconciliation.");
        clock = 2000; status = status with { RequestId = Guid.NewGuid(), PowerSource = "Ac" };
        require(Observe("Ac", "Battery", status), "Fresh AC limits did not admit the return.");
        gate.Reset();
        require(!Observe("Battery", "Ac", status), "Bounce fixture unexpectedly admitted.");
        clock = 3000; require(!Observe("Ac", "Ac", status with { RequestId = Guid.NewGuid() }), "First bounce back did not require stability.");
        clock = 4000; require(!Observe("Battery", "Ac", status), "Bounce did not reset stability.");
        clock = 6000;
        bool rejected = false;
        try { Observe("Ac", "Ac", status with { RequestId = Guid.NewGuid() }); } catch (InvalidOperationException) { rejected = true; }
        require(rejected, "Repeated source bounce renewed the four-second deadline.");
        foreach (var bad in new[] { status with { RuntimeFailure = "failed" }, status with { GpuState = "Failed" },
            status with { CpuState = "Recovering" }, status with { Ok = false }, status with { SessionEnabled = false } })
        {
            gate.Reset(); rejected = false;
            try { Observe("Ac", "Ac", bad); } catch (InvalidOperationException) { rejected = true; }
            require(rejected && !gate.Pending, "Failed/partial limits entered source reconciliation.");
        }
        foreach (var source in new[] { "Unknown", "Ac" })
        {
            gate.Reset(); rejected = false;
            try { Observe(source, "Ac", status, fresh: false); } catch (InvalidOperationException) { rejected = true; }
            require(rejected, "Unknown source or stale status admitted.");
        }
        gate.Reset(); clock = 7000; Observe("Battery", "Ac", status); clock = 6999; rejected = false;
        try { Observe("Battery", "Ac", status); } catch (InvalidOperationException) { rejected = true; }
        require(rejected, "Clock regression extended source reconciliation.");
        Console.WriteLine("PASS: bounded AC/Battery source handoff, fresh response, stable samples, duplicates, bounce deadline and failure fences.");
    }
}
