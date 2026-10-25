using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.App;

internal static class NvmlTelemetryIsolationSelfTest
{
    internal static int Run()
    {
        var results = new List<object>();
        var children = new HashSet<int>();
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        NvmlTelemetryProcessClient Reader(string fixture, Func<long>? clock = null) =>
            new("fixture-GPU", true, () => NvmlTelemetryProcessClient.CreateStartInfo(fixture), sampleTimeout: 700, clock: clock);
        bool Alive(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        void Case(string name, Action action)
        {
            var watch = Stopwatch.StartNew(); action();
            results.Add(new { name, passed = true, elapsedMs = watch.ElapsedMilliseconds });
            Console.WriteLine("PASS " + name);
        }
        try
        {
            Case("production read-only host explicitly High before any GPU initialization",()=>
            {
                using var child=Process.Start(NvmlTelemetryProcessClient.CreateStartInfo())??throw new IOException("Priority child missing.");
                children.Add(child.Id);
                try
                {
                    var line=child.StandardError.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    child.Refresh();Require(child.PriorityClass==ProcessPriorityClass.High&&line?.Contains("effective=High")==true,"Production helper did not confirm High.");
                    child.StandardInput.Close();Require(child.WaitForExit(5000),"Uninitialized helper did not exit on EOF.");
                }
                finally{if(!child.HasExited){child.StandardInput.Close();child.WaitForExit(5000);}}
            });
            Case("initialization progress is bounded and never a telemetry sample",()=>
            {
                using(var reader=Reader("init-progress"))
                {children.Add(reader.ChildProcessId!.Value);Require(reader.CurrentPhase=="ReadyAwaitingFirstSample"&&reader.ReadSample().TemperatureC==51,"Initialization phases became data or prevented a real sample.");}
                foreach(var fixture in new[]{"init-hang","init-progress-flood"})
                {
                    var watch=Stopwatch.StartNew();var rejected=false;
                    try{using var bad=Reader(fixture);}
                    catch(NvmlTelemetryUnavailableException error){rejected=fixture!="init-hang"||error.Message.Contains("phase=NvmlInitialization");}
                    Require(rejected&&watch.ElapsedMilliseconds<7000,"Phase traffic renewed the startup deadline or lost the stalled phase.");
                }
            });
            Case("slow battery recovery does not rearm resume-gap detector", () => TelemetryRecoveryClockSelfTest.Run(Require));
            Case("healthy repeated acquisition; orderly EOF/disposal", () =>
            {
                var reader = Reader("healthy"); var pid = reader.ChildProcessId!.Value; children.Add(pid);
                try
                {
                    for (var i = 0; i < 25; i++) Require(reader.ReadSample() == new NvmlClient.GpuSample(51, 18, 12), "Sample changed.");
                    Require(reader.ChildProcessId == pid, "Healthy acquisition spawned extra helpers.");
                }
                finally { reader.Dispose(); }
                Require(!Alive(pid), "Dispose left a helper running.");
                reader.Dispose();
                try { reader.ReadSample(); throw new InvalidOperationException("Disposed reader accepted a sample."); }
                catch (ObjectDisposedException) { }
            });
            foreach (var fixture in new[] { "exit", "fatal", "hang", "malformed", "oversize", "partial", "wrong-session",
                "wrong-sequence", "wrong-version", "stale", "future", "invalid-temperature", "invalid-power", "invalid-load", "error", "replay" })
                Case("reject " + fixture + "; parent survives; bounded cleanup/backoff", () =>
                {
                    using var reader = Reader(fixture); var pid = reader.ChildProcessId!.Value; children.Add(pid);
                    if (fixture == "replay") _ = reader.ReadSample();
                    var watch = Stopwatch.StartNew();
                    try { _ = reader.ReadSample(); throw new InvalidOperationException("Invalid acquisition accepted: " + fixture); }
                    catch (NvmlTelemetryUnavailableException) { }
                    Require(watch.ElapsedMilliseconds < 2500, "Failed acquisition exceeded its bounded deadline.");
                    Require(!Alive(pid) && reader.ChildProcessId is null, "Failed helper still running.");
                    try { _ = reader.ReadSample(); throw new InvalidOperationException("Backoff returned a sample."); }
                    catch (NvmlTelemetryUnavailableException) { }
                    Require(reader.ChildProcessId is null, "Backoff spawned a helper.");
                });
            Case("new process/session after retry delay; no stale sample", () =>
            {
                var starts = 0; long clock = 1000;
                using var reader = new NvmlTelemetryProcessClient("fixture-GPU", true,
                    () => NvmlTelemetryProcessClient.CreateStartInfo(++starts == 1 ? "exit" : "healthy"), clock: () => clock);
                var first = reader.ChildProcessId!.Value; children.Add(first);
                try { _ = reader.ReadSample(); throw new InvalidOperationException("Exited helper accepted."); }
                catch (NvmlTelemetryUnavailableException) { }
                clock += NvmlTelemetryProcessClient.RestartBackoffMilliseconds - 1;
                try { _ = reader.ReadSample(); throw new InvalidOperationException("Early restart accepted."); }
                catch (NvmlTelemetryUnavailableException) { }
                Require(starts == 1, "Restarted before backoff."); clock++;
                Require(reader.ReadSample().TemperatureC == 51 && starts == 2 && reader.ChildProcessId != first, "Fresh helper did not recover.");
                children.Add(reader.ChildProcessId!.Value);
            });
            Case("stderr is drained without retaining a flood", () =>
            {
                using var reader = Reader("stderr-flood"); children.Add(reader.ChildProcessId!.Value);
                Require(reader.ReadSample().LoadPercent == 12, "Stderr blocked telemetry.");
                Require(reader.NativeDiagnosticCharacterCount <= 8192, "Native diagnostics retained an unbounded flood.");
            });
            Case("unconfirmed read-only termination blocks replacements until exact child exits",()=>
            {
                using var reader=new NvmlTelemetryProcessClient("fixture-GPU",true,()=>NvmlTelemetryProcessClient.CreateStartInfo("hang"),
                    sampleTimeout:300,terminateFixture:_=>throw new System.ComponentModel.Win32Exception("Synthetic termination refusal"));
                var pid=reader.ChildProcessId!.Value;children.Add(pid);
                try
                {
                    try { reader.ReadSample();throw new InvalidOperationException("Hanging reader produced a sample."); } catch(NvmlTelemetryUnavailableException) { }
                    int starts=0;
                    try
                    {
                        using var replacement=new NvmlTelemetryProcessClient("fixture-GPU",true,()=>{starts++;return NvmlTelemetryProcessClient.CreateStartInfo("healthy");});
                        throw new InvalidOperationException("Unconfirmed reader allowed replacement.");
                    }
                    catch(NvmlTelemetryUnavailableException) { }
                    Require(starts==0&&Alive(pid),"Unconfirmed read-only child identity was lost or a replacement spawned.");
                }
                finally
                {
                    // Exact synthetic read-only fixture only. Never a live native mutator.
                    using var fixtureChild=Process.GetProcessById(pid);fixtureChild.Kill();Require(fixtureChild.WaitForExit(5000),"Fixture child did not exit.");
                }
                using var fresh=Reader("healthy");children.Add(fresh.ChildProcessId!.Value);
                Require(fresh.ReadSample().TemperatureC==51,"Confirmed read-only child exit did not unblock a fresh reader.");
            });
            Case("concurrent reads and disposal are serialized", () =>
            {
                using var reader = Reader("healthy"); var pid = reader.ChildProcessId!.Value; children.Add(pid);
                var tasks = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
                { try { reader.ReadSample(); } catch (ObjectDisposedException) { } })).ToArray();
                reader.Dispose(); Require(Task.WaitAll(tasks, 5000) && !Alive(pid), "Concurrent disposal leaked/blocked.");
            });
            Case("helper exits when its owner process ends", () =>
            {
                var start = NvmlTelemetryProcessClient.CreateStartInfo();
                for (var i = 0; i < 3; i++) start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
                start.ArgumentList.Add("--nvml-telemetry-fixture-parent");
                using var owner = Process.Start(start) ?? throw new IOException("Fixture owner did not start.");
                try
                {
                    var line = owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                    Require(int.TryParse(line, out var child), "Owner did not report child PID."); children.Add(child);
                    Require(owner.WaitForExit(5000) && owner.ExitCode == 0, "Owner fixture did not exit.");
                    Require(SpinWait.SpinUntil(() => !Alive(child), 3000), "Orphan helper remained after parent exit.");
                }
                finally { if (!owner.HasExited) owner.Kill(entireProcessTree: true); }
            });
            Require(children.All(pid => !Alive(pid)), "A fixture left a helper behind.");
            Console.WriteLine("NVML telemetry isolation: PASS; synthetic process failures, zero hardware IO.");
            return 0;
        }
        catch (Exception ex) { results.Add(new { passed = false, error = ex.ToString(),
            helperDiagnostics = AppLog.ReadTail(AppLog.CurrentLogPath, 16384) }); Console.Error.WriteLine(ex); return 1; }
        finally
        {
            var directory = Path.Combine("logs", "nvml-telemetry-self-test"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new
            { hardwareIo = false, physicalCrashReproduced = false, cases = results }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
}
