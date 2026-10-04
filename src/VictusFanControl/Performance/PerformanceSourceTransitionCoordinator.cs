namespace VictusFanControl.Performance;

internal interface ICpuPowerSourceTransitionSink
{
    CpuPowerPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source);
}

internal interface IGpuClockSourceTransitionSink
{
    GpuClockPresetTransitionResult HandleConfirmedSourceChange(
        PerformancePowerSourceKind source);
}

internal readonly record struct PerformanceSourceDispatchResult(
    PerformancePowerSourceObservation Observation,
    PerformancePowerSourceKind PreviousSource,
    bool Primed,
    bool DuplicateSuppressed,
    bool CpuAttempted,
    CpuPowerPresetTransitionResult? CpuResult,
    string? CpuException,
    bool GpuAttempted,
    GpuClockPresetTransitionResult? GpuResult,
    string? GpuException,
    bool Succeeded,
    string Status);

/// <summary>
/// Coordinates a power-source signal after a direct GetSystemPowerStatus query.
///
/// The native notification is never trusted as source authority. Every call
/// performs one fresh IPerformancePowerSourceReader.Read().
///
/// CPU and GPU dispatches are independent: one domain is still attempted if
/// the other returns failure or throws. No hidden retry exists here.
///
/// The first observation only primes the current source and performs zero
/// domain writes. Repeated notifications for the same direct-query source are
/// suppressed. Unknown/query-failure after a previously known source is
/// dispatched once as Unknown so each domain can relinquish source-specific
/// authority through its own fail-closed semantics.
/// </summary>
internal sealed class PerformanceSourceTransitionCoordinator
{
    private readonly IPerformancePowerSourceReader _reader;
    private readonly ICpuPowerSourceTransitionSink _cpu;
    private readonly IGpuClockSourceTransitionSink _gpu;

    private bool _primed;
    private PerformancePowerSourceKind _lastSource =
        PerformancePowerSourceKind.Unknown;

    internal PerformanceSourceTransitionCoordinator(
        IPerformancePowerSourceReader reader,
        ICpuPowerSourceTransitionSink cpu,
        IGpuClockSourceTransitionSink gpu)
    {
        _reader =
            reader ??
            throw new ArgumentNullException(nameof(reader));

        _cpu =
            cpu ??
            throw new ArgumentNullException(nameof(cpu));

        _gpu =
            gpu ??
            throw new ArgumentNullException(nameof(gpu));
    }

    internal bool IsPrimed =>
        _primed;

    internal PerformancePowerSourceKind LastSource =>
        _lastSource;

    internal PerformanceSourceDispatchResult Prime()
    {
        if (_primed)
        {
            throw new InvalidOperationException(
                "Performance source coordinator is already primed.");
        }

        var observation =
            _reader.Read();

        var source =
            observation.Succeeded
                ? observation.Source
                : PerformancePowerSourceKind.Unknown;

        _lastSource =
            source;

        _primed =
            true;

        return new PerformanceSourceDispatchResult(
            observation,
            PreviousSource:
                PerformancePowerSourceKind.Unknown,
            Primed: true,
            DuplicateSuppressed: false,
            CpuAttempted: false,
            CpuResult: null,
            CpuException: null,
            GpuAttempted: false,
            GpuResult: null,
            GpuException: null,
            Succeeded:
                observation.Succeeded,
            Status:
                observation.Succeeded
                    ? "PERFORMANCE_SOURCE_PRIMED_NO_DISPATCH"
                    : "PERFORMANCE_SOURCE_PRIMED_UNKNOWN_AFTER_QUERY_FAILURE_NO_DISPATCH");
    }

    internal PerformanceSourceDispatchResult HandleNotificationSignal()
    {
        if (!_primed)
        {
            return Prime();
        }

        var observation =
            _reader.Read();

        var source =
            observation.Succeeded
                ? observation.Source
                : PerformancePowerSourceKind.Unknown;

        var previous =
            _lastSource;

        if (source ==
            previous)
        {
            return new PerformanceSourceDispatchResult(
                observation,
                previous,
                Primed: true,
                DuplicateSuppressed: true,
                CpuAttempted: false,
                CpuResult: null,
                CpuException: null,
                GpuAttempted: false,
                GpuResult: null,
                GpuException: null,
                Succeeded:
                    observation.Succeeded,
                Status:
                    observation.Succeeded
                        ? "PERFORMANCE_SOURCE_DUPLICATE_SUPPRESSED"
                        : "PERFORMANCE_SOURCE_DUPLICATE_UNKNOWN_QUERY_FAILURE_SUPPRESSED");
        }

        // Advance the source episode once. A failed domain does not authorize
        // notification-driven retry storms on duplicate Windows events.
        _lastSource =
            source;

        CpuPowerPresetTransitionResult?
            cpuResult = null;

        GpuClockPresetTransitionResult?
            gpuResult = null;

        string? cpuException =
            null;

        string? gpuException =
            null;

        try
        {
            cpuResult =
                _cpu.HandleConfirmedSourceChange(
                    source);
        }
        catch (Exception ex)
        {
            cpuException =
                ex.ToString();
        }

        try
        {
            gpuResult =
                _gpu.HandleConfirmedSourceChange(
                    source);
        }
        catch (Exception ex)
        {
            gpuException =
                ex.ToString();
        }

        var cpuSucceeded =
            cpuException is null &&
            cpuResult.HasValue &&
            cpuResult.Value.Succeeded;

        var gpuSucceeded =
            gpuException is null &&
            gpuResult.HasValue &&
            gpuResult.Value.Succeeded;

        var succeeded =
            observation.Succeeded &&
            cpuSucceeded &&
            gpuSucceeded;

        var status =
            !observation.Succeeded
                ? "PERFORMANCE_SOURCE_QUERY_FAILED__UNKNOWN_RELEASE_DISPATCHED"
                : source ==
                    PerformancePowerSourceKind.Unknown
                    ? "PERFORMANCE_SOURCE_UNKNOWN__FAIL_CLOSED_RELEASE_DISPATCHED"
                    : succeeded
                        ? "PERFORMANCE_SOURCE_CHANGE_DISPATCHED_TO_CPU_AND_GPU"
                        : "PERFORMANCE_SOURCE_CHANGE_DISPATCH_PARTIAL_FAILURE";

        return new PerformanceSourceDispatchResult(
            observation,
            previous,
            Primed: true,
            DuplicateSuppressed: false,
            CpuAttempted: true,
            CpuResult: cpuResult,
            CpuException: cpuException,
            GpuAttempted: true,
            GpuResult: gpuResult,
            GpuException: gpuException,
            Succeeded: succeeded,
            Status: status);
    }
}
