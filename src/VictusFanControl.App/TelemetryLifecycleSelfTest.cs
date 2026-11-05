using System.Diagnostics;
using VictusFanControl.Runtime;

namespace VictusFanControl.App;

/// <summary>Production signal/watchdog/disposal paths with synthetic lifetimes; no hardware reader can start.</summary>
internal static class TelemetryLifecycleSelfTest
{
    internal static async Task<int> RunAsync()
    {
        try { await RunChecksAsync(); return 0; }
        catch (Exception error) { Console.WriteLine("FAIL telemetry lifecycle: " + error); return 1; }
    }

    internal static async Task RunChecksAsync()
    {
        var failures = new List<Exception>();
        async Task Case(string name, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS telemetry lifecycle: " + name); }
            catch (Exception error) { failures.Add(new InvalidOperationException(name,error)); Console.WriteLine("FAIL telemetry lifecycle: " + name + ": " + error.Message); }
        }
        await Case("concurrent notifications coalesce without exceptions", TestSignalsAsync);
        await Case("state observer failure preserves remaining observers", () =>
        {
            var machine = new RuntimeStateMachine(); var observed = 0;
            machine.StateChanged += (_, _) => throw new IOException(new string('x',1024));
            machine.StateChanged += (_, _) => observed++;
            machine.Transition(SystemState.Degraded,"synthetic failure");
            Require(observed == 1 && machine.State == SystemState.Degraded,"A failed observer interrupted state delivery.");
            Require(machine.ObserverFailureCount == 1 && machine.LastObserverFailure is { ExceptionType:nameof(IOException), Message.Length:512 },
                "State observer failure was lost or retained unbounded detail.");
            return Task.CompletedTask;
        });
        await Case("watchdog requests recovery despite observer and logger failures", async () =>
        {
            await using var worker = Fixture(); worker.StateMachine.Transition(SystemState.Healthy,"fixture ready");
            var original = worker.LastCompletedReadTick; var delivered = 0; var recoveryBeforeNotification = false;
            worker.StateMachine.StateChanged += (_, _) =>
            {
                recoveryBeforeNotification = worker.RecoveryPending;
                throw new IOException("synthetic watchdog observer");
            };
            worker.EventLogged += (_, _) => throw new IOException("synthetic watchdog logger");
            worker.EventLogged += (_, _) => delivered++;
            worker.CheckWatchdog(original + 6000);
            Require(recoveryBeforeNotification && worker.RecoveryPending && worker.StateMachine.State == SystemState.Degraded,
                "Watchdog did not request recovery before optional notification.");
            Require(delivered == 2 && worker.LastCompletedReadTick == original,"Diagnostics failed or renewed acquisition freshness.");
            Require(worker.ObserverFailureCount == 2 && worker.LastObserverFailure is { Event:"EventLogged", ExceptionType:nameof(IOException) } &&
                worker.StateMachine.ObserverFailureCount == 1,"Observer failures were not retained in memory.");
        });
        await Case("completed loop faults do not skip cleanup", async () =>
        {
            var cleanup = 0;
            var worker = TelemetryWorker.CreateLifetimeFixture(Task.FromException(new IOException("acquisition fault")),
                Task.FromException(new InvalidOperationException("watchdog fault")),() => cleanup++);
            var error = await CaptureAsync(() => worker.DisposeAsync().AsTask());
            Require(cleanup == 1,"Loop fault skipped reader cleanup.");
            Require(error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count == 2,"Shutdown lost the original task failures.");
            Require(worker.ShutdownReport is { ResourcesDrained:true, TaskFailures.Count:2, CleanupFailures.Count:0 },
                "Completed task faults were confused with undrained resources.");
            Require(await CaptureAsync(() => worker.WaitOrWakeAsync(0,CancellationToken.None)) is ObjectDisposedException,"Signal resource was not disposed.");
        });
        await Case("reader cleanup failure still drains independent resources", async () =>
        {
            var worker = TelemetryWorker.CreateLifetimeFixture(Task.CompletedTask,Task.CompletedTask,
                () => throw new IOException("reader cleanup fault"));
            var error = await CaptureAsync(() => worker.DisposeAsync().AsTask());
            Require(error is AggregateException,"Cleanup failure was not reported.");
            Require(worker.ShutdownReport is { ResourcesDrained:false, TaskFailures.Count:0, CleanupFailures.Count:1 },
                "A failed reader cleanup falsely confirmed drainage.");
            Require(await CaptureAsync(() => worker.WaitOrWakeAsync(0,CancellationToken.None)) is ObjectDisposedException,"Reader failure skipped signal cleanup.");
        });
        await Case("one completed fault cannot bypass another in-flight task", async () =>
        {
            var nativeReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cleanup = 0;
            var worker = TelemetryWorker.CreateLifetimeFixture(Task.FromException(new IOException("completed fault")),
                nativeReturn.Task,() => cleanup++);
            var disposal = worker.DisposeAsync().AsTask();
            var held = !disposal.IsCompleted && cleanup == 0 && worker.ShutdownReport is null;
            nativeReturn.SetResult();
            var error = await CaptureAsync(() => disposal);
            Require(held && cleanup == 1 && error is AggregateException &&
                worker.ShutdownReport is { ResourcesDrained:true, TaskFailures.Count:1, CleanupFailures.Count:0 },
                "A completed fault allowed cleanup to overtake another running task.");
        });
        await Case("disposal waits for in-flight work and is single-flight", async () =>
        {
            var nativeReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var cleanup = 0;
            var worker = TelemetryWorker.CreateLifetimeFixture(nativeReturn.Task,Task.CompletedTask,() => cleanup++);
            var first = worker.DisposeAsync().AsTask(); var second = worker.DisposeAsync().AsTask();
            var single = ReferenceEquals(first,second); var held = !first.IsCompleted && cleanup == 0 && worker.ShutdownReport is null;
            nativeReturn.TrySetResult();
            var error = await CaptureAsync(() => Task.WhenAll(first,second));
            Require(single && held && cleanup == 1 && error is null,"Disposal duplicated cleanup, skipped a live reader or failed after return.");
            Require(worker.ShutdownReport is { ResourcesDrained:true, TaskFailures.Count:0, CleanupFailures.Count:0 },"Returned work did not confirm local drainage.");
            await worker.DisposeAsync();
        });
        await Case("cancellation callback failure preserves cleanup and single-flight reentry", async () =>
        {
            TelemetryWorker? worker = null; Task? reentered = null; var cleanup = 0;
            worker = TelemetryWorker.CreateLifetimeFixture(Task.CompletedTask,Task.CompletedTask,() => cleanup++,token =>
                token.Register(() => { reentered = worker!.DisposeAsync().AsTask(); throw new IOException("cancellation callback fault"); }));
            var disposal = worker.DisposeAsync().AsTask();
            var error = await CaptureAsync(() => disposal);
            Require(ReferenceEquals(disposal,reentered) && cleanup == 1 && error is AggregateException,
                "Cancellation reentry duplicated disposal or skipped cleanup after callback failure.");
            Require(worker.ShutdownReport is { ResourcesDrained:false, CleanupFailures.Count:1 },"Cancellation failure lost its conservative cleanup result.");
            Require(await CaptureAsync(() => worker.WaitOrWakeAsync(0,CancellationToken.None)) is ObjectDisposedException,"Cancellation failure skipped signal cleanup.");
        });
        await Case("expected loop cancellation drains without a task failure", async () =>
        {
            var cleanup = 0;
            var cancelled = Task.FromCanceled(new CancellationToken(true));
            var worker = TelemetryWorker.CreateLifetimeFixture(cancelled,cancelled,() => cleanup++);
            await worker.DisposeAsync();
            Require(cleanup == 1 && worker.ShutdownReport is { ResourcesDrained:true, TaskFailures.Count:0, CleanupFailures.Count:0 },
                "Expected cancellation was confused with a failed or live native task.");
        });
        await Case("concurrent notifications during disposal stay harmless", async () =>
        {
            var nativeReturn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = TelemetryWorker.CreateLifetimeFixture(nativeReturn.Task,Task.CompletedTask,() => { });
            var senders = Enumerable.Range(0,8).Select(_ => Task.Run(() =>
            {
                for (var i=0;i<1000;i++) worker.Wake();
            })).ToArray();
            var disposal = worker.DisposeAsync().AsTask();
            nativeReturn.SetResult();
            await Task.WhenAll(senders.Append(disposal));
            worker.Wake();
            Require(worker.ShutdownReport is { ResourcesDrained:true },"Concurrent closing notifications prevented cleanup.");
        });
        await Case("missing or reused parent is rejected before NVIDIA initialization", () =>
        {
            var missing = NvmlTelemetryWorkerHost.Run(["--nvml-telemetry-fixture",int.MaxValue.ToString(),"1","healthy"]);
            using var owner = Process.GetCurrentProcess();
            var reused = NvmlTelemetryWorkerHost.Run(["--nvml-telemetry-fixture",owner.Id.ToString(),(owner.StartTime.ToUniversalTime().Ticks+1).ToString(),"healthy"]);
            Require(missing == 197 && reused == 197,"Helper did not reject absent/mismatched owner with a controlled result.");
            Require(NvmlTelemetryWorkerHost.Run([]) == 2,"Empty arguments were not rejected.");
            return Task.CompletedTask;
        });
        if (failures.Count != 0) throw new AggregateException("Telemetry lifecycle regressions failed.",failures);
        Console.WriteLine("PASS telemetry lifecycle block 1: all cases; zero hardware IO.");
    }

    private static TelemetryWorker Fixture() => new("fixture://lifecycle", _ => throw new InvalidOperationException("No hardware acquisition permitted."))
        { ProductTelemetryTolerance = true };

    private static async Task TestSignalsAsync()
    {
        await using var worker = Fixture();
        const int count = 8, rounds = 600;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var barrier = new Barrier(count+1);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0,count).Select(_ => new Thread(() =>
        {
            try
            {
                for (var i=0;i<rounds;i++)
                {
                    barrier.SignalAndWait(cancellation.Token);
                    try { worker.Wake(); } catch (Exception error) { errors.Enqueue(error); }
                    barrier.SignalAndWait(cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
        }) { IsBackground=true }).ToArray();
        foreach (var thread in threads) thread.Start();
        try
        {
            for (var i=0;i<rounds;i++)
            {
                await worker.WaitOrWakeAsync(0,CancellationToken.None);
                barrier.SignalAndWait(cancellation.Token); barrier.SignalAndWait(cancellation.Token);
            }
        }
        finally
        {
            cancellation.Cancel(); foreach (var thread in threads) Require(thread.Join(3000),"Signal fixture did not drain.");
        }
        Require(errors.IsEmpty,"Concurrent signal failed: " + (errors.TryPeek(out var first) ? first.GetType().Name : "unknown"));
        // Check a new signal after consumption too; coalescing must not lose future work.
        await worker.WaitOrWakeAsync(0,CancellationToken.None); worker.Wake();
        var waiting = worker.WaitOrWakeAsync(1000,CancellationToken.None);
        Require(waiting.IsCompleted,"Fresh work signal was lost."); await waiting;
    }
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private static void Require(bool condition,string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
