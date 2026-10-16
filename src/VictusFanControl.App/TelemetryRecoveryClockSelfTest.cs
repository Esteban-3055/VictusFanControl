namespace VictusFanControl.App;

internal static class TelemetryRecoveryClockSelfTest
{
    internal static void Run(Action<bool, string> require)
    {
        var worker = new TelemetryWorker("fixture://no-hardware");
        worker.ObserveLoopTick(1000);
        // Real battery diagnostic: a 47,938 ms recovery was misclassified as another resume.
        worker.CompleteRecoveryClock(48938);
        require(worker.ObserveLoopTick(49938) == 1000, "Recovery duration rearmed the resume-gap detector.");
        require(worker.ObserveLoopTick(61938) == 12000, "A genuine scheduling/suspend gap was suppressed.");
        foreach (var elapsed in new[] { 10188L, 10562L, 12078L, 47938L, 17015L, 14954L, 15125L, 18312L })
        {
            worker.CompleteRecoveryClock(61938 + elapsed);
            require(worker.ObserveLoopTick(62938 + elapsed) == 1000, "Repeated slow recovery created another false resume.");
        }
        worker.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
