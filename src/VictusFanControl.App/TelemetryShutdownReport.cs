namespace VictusFanControl.App;

/// <summary>Local reader cleanup only; never evidence of native fan/CPU/GPU owner release.</summary>
internal sealed record TelemetryShutdownReport(bool ResourcesDrained,
    IReadOnlyList<Exception> TaskFailures,IReadOnlyList<Exception> CleanupFailures);

internal sealed record TelemetryObserverFailure(string Event,string ExceptionType,string Message,DateTimeOffset AtUtc);
