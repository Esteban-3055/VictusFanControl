using VictusFanControl.Product;
using VictusFanControl.Recovery;

namespace VictusFanControl.App;

internal static partial class ProductGuiSelfTest
{
    private static void TestGuidedRecovery(Action<bool, string> require)
    {
        var cpu = Guid.NewGuid(); var gpu = Guid.NewGuid(); var selected = new ProductRecoverySelection(cpu, gpu);
        ProductRuntimeState Pending() => new() { Target = ProductRecoverySelection.Target, LifecycleBlocked = true,
            PerformanceRecovery = new(true, "fixture pending", cpu, gpu), AutomaticAuthorized = true,
            PerformanceActive = true, CpuState = "Active", GpuState = "ActiveUnverified" };
        var exitCode = Environment.ExitCode;
        try
        {
            var pending = selected; var old = new RecordingRuntime { DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var next = new RecordingRuntime(); int launches = 0, created = 0;
            using (var form = new ProductForm("fixture://recovery", fixture: old, fixtureProfiles: new ProductProfiles { ActivateAutomaticOnStart = true }))
            {
                form.RecoverySelectionReader = () => pending;
                form.RecoveryRunner = s => { require(old.Disposals == 1 && s == selected, "Recovery bypassed old cleanup or exact session selection."); launches++; pending = new(Guid.Empty, Guid.Empty); return Task.FromResult(new ProductRecoveryResult(true, "fixture-evidence", "resolved")); };
                form.RecoveryRuntimeFactory = p => { created++; require(p.Ac.CpuPl1Watts == 38, "Recovery lost unsaved profile."); return Task.FromResult<IProductRuntime>(next); };
                form.Show(); Application.DoEvents(); form.EditValue("pl1", 38); var draft = ProductProfilesStore.Serialize(form.Draft); old.Publish(Pending());
                form.Canvas.Page = ProductPage.Performance; form.Canvas.PerformanceTab = 3; form.Canvas.Refresh();
                require(form.Canvas.Hits.Single(h => h.Id == "recovery-run").Enabled, "Pending recovery is not actionable in Guardian.");
                form.Canvas.Page = ProductPage.Settings; form.Canvas.Refresh(); require(form.Canvas.Hits.Any(h => h.Id == "recovery-run"), "Settings lacks recovery.");
                var task = form.RecoverPerformanceAsync(confirmed: true); require(ReferenceEquals(task, form.RecoverPerformanceAsync(true)), "Double click duplicated recovery.");
                PumpUntil(() => old.Disposals == 1, "Recovery did not begin shutdown."); require(launches == 0 && created == 0, "Recovery ran before cleanup drain.");
                old.DisposeGate.SetResult(); PumpUntil(() => task.IsCompleted, "Recovery hung."); task.GetAwaiter().GetResult();
                require(launches == 1 && created == 1 && next.Starts == 1 && next.Commands == 0 && !form.IsDisposed && form.Dirty && ProductProfilesStore.Serialize(form.Draft) == draft,
                    "Recovery duplicated, closed the GUI, acquired authority or lost the draft.");
                old.Publish(Pending() with { Failure = "late old publication" }); require(form.Canvas.State.Failure is null, "Released runtime changed recovered GUI.");
                form.PresentationTick(); require(next.Commands == 0, "Recovery automatically activated saved Automatic preference.");
                var exit = form.RequestExitAsync(); PumpUntil(() => exit.IsCompleted, "Recovered GUI exit hung."); require(next.Disposals == 1 && old.Disposals == 1, "Recovery duplicated runtime disposal.");
            }
            foreach (var failure in new[] { "cleanup", "changed-session", "suspend-before", "suspend-during", "runner" })
            {
                pending = selected; launches = 0; created = 0;
                old = new RecordingRuntime { DisposeGate = new(TaskCreationOptions.RunContinuationsAsynchronously), DisposeFailure = failure == "cleanup" ? "unresolved release" : null };
                var recovered = new TaskCompletionSource<ProductRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var form = new ProductForm("fixture://recovery", fixture: old, fixtureProfiles: new ProductProfiles());
                form.RecoverySelectionReader = () => pending;
                form.RecoveryRuntimeFactory = _ => { created++; return Task.FromResult<IProductRuntime>(new RecordingRuntime()); };
                form.RecoveryRunner = _ => { launches++; return failure == "runner" ? Task.FromResult(new ProductRecoveryResult(false, "fixture-failure", "other active process")) : recovered.Task; };
                form.Show(); Application.DoEvents(); old.Publish(Pending()); var task = form.RecoverPerformanceAsync(true);
                PumpUntil(() => old.Disposals == 1, "Failure fixture did not release.");
                if (failure == "changed-session") pending = selected with { GpuSession = Guid.NewGuid() };
                if (failure == "suspend-before") form.HandlePowerEventAsync(4).GetAwaiter().GetResult();
                old.DisposeGate.SetResult();
                if (failure == "suspend-during")
                {
                    PumpUntil(() => launches == 1, "Recovery worker did not start."); form.HandlePowerEventAsync(4).GetAwaiter().GetResult();
                    pending = new(Guid.Empty, Guid.Empty); recovered.SetResult(new(true, "fixture-evidence", "resolved"));
                }
                PumpUntil(() => task.IsCompleted, "Failed recovery hung.");
                require(created == 0 && form.Canvas.State.LifecycleBlocked && form.Canvas.State.Failure is not null, "Failed/suspended recovery reopened control.");
                require(!form.Canvas.State.PerformanceActive && form.Canvas.State.CpuState != "Active" && form.Canvas.State.GpuState != "ActiveUnverified", "Failed recovery retained a stale active presentation.");
                require(launches == (failure is "suspend-during" or "runner" ? 1 : 0), "Unsafe cleanup/change/suspend launched recovery.");
                var exit = form.RequestExitAsync(); PumpUntil(() => exit.IsCompleted, "Failed recovery exit hung."); require(old.Disposals == 1, "Failed recovery repeated disposal.");
            }
            pending = selected; launches = 0; created = 0; old = new RecordingRuntime();
            using (var form = new ProductForm("fixture://recovery", fixture: old, fixtureProfiles: new ProductProfiles()))
            {
                var recovery = new TaskCompletionSource<ProductRecoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                form.RecoverySelectionReader = () => pending;
                form.RecoveryRunner = _ => { launches++; return recovery.Task; };
                form.RecoveryRuntimeFactory = _ => { created++; return Task.FromResult<IProductRuntime>(new RecordingRuntime()); };
                form.Show(); Application.DoEvents(); old.Publish(Pending()); var task = form.RecoverPerformanceAsync(true);
                PumpUntil(() => launches == 1, "Close race recovery never started."); var exit = form.RequestExitAsync();
                require(!exit.IsCompleted && !form.IsDisposed, "Recovery owner exited before the recovery process finished.");
                pending = new(Guid.Empty, Guid.Empty); recovery.SetResult(new(true, "fixture-evidence", "resolved"));
                PumpUntil(() => exit.IsCompleted, "Close did not drain recovery.");
                require(task.IsCompleted && old.Disposals == 1 && created == 0, "Close/recovery race recreated a runtime or repeated cleanup.");
            }
            using (var form = new ProductForm("fixture://recovery", fixture: new RecordingRuntime(), fixtureProfiles: new ProductProfiles()))
            {
                form.RecoveryRunner = _ => throw new InvalidOperationException("Malformed selection must not launch.");
                form.RecoverySelectionReader = () => throw new IOException("invalid record");
                form.Show(); Application.DoEvents(); form.RecoverPerformanceAsync(true).GetAwaiter().GetResult();
                require(!form.Canvas.Busy && form.Canvas.Notice.Contains("invalid record"), "Invalid record was ignored.");
                var exit = form.RequestExitAsync(); PumpUntil(() => exit.IsCompleted, "Invalid recovery exit hung.");
            }
            Console.WriteLine("Guided GUI recovery: PASS (one request, release drain, exact sessions, changed-record/cleanup/suspend fences, report failure, same GUI/draft and Firmware without Automatic; zero hardware IO).");
        }
        finally { Environment.ExitCode = exitCode; }
    }
}
