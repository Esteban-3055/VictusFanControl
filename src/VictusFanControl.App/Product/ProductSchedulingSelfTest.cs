using System.Diagnostics;
using VictusFanControl.Performance;
using VictusFanControl.Scheduling;

namespace VictusFanControl.App;

internal static class ProductSchedulingSelfTest
{
    internal static void Run(Action<bool, string> require)
    {
        using (var process = Process.GetCurrentProcess())
        {
            var original = process.PriorityClass;
            try
            {
                var applied = ProductProcessPriority.Apply(Console.WriteLine);
                process.Refresh();
                require(applied && process.PriorityClass == ProcessPriorityClass.High,
                    "High priority was not confirmed on the isolated test process.");
            }
            finally { process.PriorityClass = original; }
            process.Refresh();
            require(process.PriorityClass == original, "Test process priority was not restored.");
        }
        var logs = new List<string>();
        require(!ProductProcessPriority.Apply(logs.Add, () => throw new UnauthorizedAccessException("fixture denial")), "Priority denial was hidden.");
        require(!ProductProcessPriority.Apply(logs.Add, () => { }, () => ProcessPriorityClass.Normal), "Unapplied priority was reported as High.");
        require(logs.Count == 2, "Priority failure diagnostics missing.");
        foreach (var option in new[] { "--gui-session", "--fan-wmi-session", "--recover-gui-session", "--recover-product-fans" })
            require(ProductProcessPriority.IsProductionGuardian([option]), "Production Guardian priority route missing.");
        foreach (var option in new[] { "--gui-fixture-session", "--fan-wmi-fixture-session", "--self-test", "--gui-recovery-self-test" })
            require(!ProductProcessPriority.IsProductionGuardian([option]), "Fixture unexpectedly acquired production priority.");

        long now = 100;
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var cache = new RecoveryPreviewCache(() =>
        {
            Interlocked.Increment(ref calls); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fixture read not released.");
            return new(false, "empty fixture");
        }, () => Interlocked.Read(ref now));
        try
        {
            require(cache.Get() is { Pending: true, Actionable: false }, "Unknown inventory was admitted.");
            require(entered.Wait(TimeSpan.FromSeconds(5)), "Background reader never started.");
            var timer = Stopwatch.StartNew();
            // Model repeated snapshot publications while filesystem IO is held indefinitely.
            for (var sequence = 1; sequence <= 1000; sequence++)
                require(cache.Get() is { Pending: true, Actionable: false }, "Blocked inventory invented a recovery command.");
            require(timer.Elapsed < TimeSpan.FromSeconds(1) && calls == 1, "Publications blocked on IO or spawned duplicate readers.");
            release.Set();
            require(SpinWait.SpinUntil(() => !cache.Get().Pending, 5000), "Completed inventory did not reach presentation.");

            // A new authoritative preflight must reject a journal even while presentation says empty.
            var starts = 0;
            var client = new PerformanceGuardianClient("fixture://modules", _ => { starts++; return null; },
                () => new(true, "new pending journal", Guid.NewGuid()));
            var rejected = false;
            try { client.EnableAsync(new PerformanceGuiSessionConfiguration()).GetAwaiter().GetResult(); }
            catch (InvalidOperationException ex) when (ex.Message == "new pending journal") { rejected = true; }
            require(rejected && starts == 0 && !cache.Get().Pending, "Presentation cache bypassed fresh native admission.");

            entered.Reset(); release.Reset(); Interlocked.Exchange(ref now, 5100);
            require(cache.Get() is { Pending: true, Actionable: false }, "Expired empty inventory admitted a session.");
            require(SpinWait.SpinUntil(() => { cache.Get(); return entered.IsSet; }, 5000) && calls == 2, "Expired inventory did not refresh exactly once.");
            var dispose = cache.DisposeAsync().AsTask();
            require(dispose.IsCompleted && cache.Get().Pending && calls == 2, "Optional read retained shutdown or admitted a late result.");
            release.Set(); dispose.GetAwaiter().GetResult();
            require(cache.Get() is { Pending: true, Actionable: false } && calls == 2, "Late result survived runtime disposal.");
        }
        finally { release.Set(); cache.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var cpu = Guid.NewGuid(); var gpu = Guid.NewGuid();
        var exact = new RecoveryPreviewCache(() => new(true, "pending fixture", cpu, gpu));
        try
        {
            require(SpinWait.SpinUntil(() => exact.Get().Actionable, 5000), "Pending inventory was not published.");
            require(exact.Get() is { CpuSession: var c, GpuSession: var g } && c == cpu && g == gpu, "Exact recovery identities changed.");
        }
        finally { exact.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var failed = new RecoveryPreviewCache(() => throw new IOException("fixture IO failure"));
        try
        {
            require(SpinWait.SpinUntil(() => failed.Get().Detail.Contains("fixture IO failure"), 5000), "Read error was not published.");
            require(failed.Get() is { Pending: true, Actionable: false }, "Read failure admitted hardware or invented session IDs.");
        }
        finally { failed.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        // Refreshes must preserve the coordinator's ten-second exact-inventory
        // window: a loading state every TTL would indefinitely postpone recovery.
        now = 0; calls = 0;
        var periodic = new RecoveryPreviewCache(() => new(true, "refresh " + Interlocked.Increment(ref calls), cpu, gpu),
            () => Interlocked.Read(ref now));
        try
        {
            require(SpinWait.SpinUntil(() => periodic.Get().Actionable, 5000), "Periodic fixture did not initialize.");
            var coordinator = new ProductAutomaticRecovery();
            for (var tick = 0; tick <= 10; tick++)
            {
                Interlocked.Exchange(ref now, tick * 1000L);
                var value = periodic.Get();
                require(value.Actionable, "Routine refresh interrupted the recovery observation window.");
                require(coordinator.TakeRequest(new() {PerformanceRecovery=value,Runtime="RecoveryRequired"}, tick * 1000L) == (tick == 10),
                    "Periodic refresh postponed or bypassed the recovery delay.");
                if (tick > 0 && tick % 3 == 0)
                {
                    var previous = value.Detail;
                    require(SpinWait.SpinUntil(() => periodic.Get().Detail != previous, 5000), "Ahead-of-expiry refresh did not complete.");
                }
            }
        }
        finally { periodic.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        Console.WriteLine("PASS scheduling: High verified/restored; blocked IO publication/coalescing; freshness; fresh admission; disposal; exact IDs; read failure; periodic recovery; zero hardware IO.");
    }
}
