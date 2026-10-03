using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

internal sealed record HpWmiFanProofSample(HpWmiFanTelemetrySample Speeds, long Sequence);

/// <summary>
/// On-demand control proof, with no cache or EC fallback. Queue + native await
/// is bounded; a timed-out/canceled native read retains the shared telemetry
/// slot until it really returns. No replacement worker can overlap it.
/// </summary>
internal sealed class HpWmiFanProofReader
{
    internal const int MaximumWaitMilliseconds = 3000;
    private static long _sequence;
    private readonly Func<HpBiosRequest, HpBiosResponse> _send;
    private readonly SemaphoreSlim _admission;
    private readonly Func<long> _milliseconds;
    private readonly TimeSpan _maximumWait;

    public HpWmiFanProofReader()
    {
        _admission = HpWmiFanTelemetryReader.SharedReadAdmission;
        _milliseconds = () => Environment.TickCount64;
        _maximumWait = TimeSpan.FromMilliseconds(MaximumWaitMilliseconds);
        HpOmenBiosWmiClient? client = null;
        _send = request =>
        {
            try { client ??= new HpOmenBiosWmiClient(); return client.SendWithResponse(request); }
            catch { client = null; throw; }
        };
    }

    internal HpWmiFanProofReader(Func<HpBiosRequest, HpBiosResponse> send,
        SemaphoreSlim admission, Func<long> milliseconds, TimeSpan maximumWait)
    {
        _send = send;
        _admission = admission;
        _milliseconds = milliseconds;
        _maximumWait = maximumWait;
    }

    public async ValueTask<HpWmiFanProofSample> ReadFreshAsync(CancellationToken cancellationToken)
    {
        var waitStarted = _milliseconds();
        if (!await _admission.WaitAsync(_maximumWait, cancellationToken).ConfigureAwait(false))
            throw new TimeoutException("HP WMI proof admission timed out; prior native read still owns the slot.");

        Task<HpWmiFanProofSample> pending;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending = Task.Run(() =>
            {
                try
                {
                    var started = _milliseconds();
                    var utc = DateTimeOffset.UtcNow;
                    var sequence = Interlocked.Increment(ref _sequence);
                    var speeds = HpWmiFanTelemetryReader.Decode(
                        _send(Hp8C40BiosFanControl.BuildGetFanLevelRequest()), utc, started);
                    return new HpWmiFanProofSample(speeds, sequence);
                }
                finally { _admission.Release(); }
            });
        }
        catch { _admission.Release(); throw; }

        // Observe a native failure even if the awaiting caller has timed out.
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var remaining = _maximumWait - TimeSpan.FromMilliseconds(_milliseconds() - waitStarted);
        if (remaining <= TimeSpan.Zero) throw new TimeoutException("HP WMI proof acquisition budget expired.");
        var sample = await pending.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var age = _milliseconds() - sample.Speeds.StartedAtMilliseconds;
        if (age < 0 || age >= MaximumWaitMilliseconds)
            throw new InvalidDataException("HP WMI proof sample expired from query start; result discarded.");
        if (sample.Speeds.CpuSpeedLevel >= 100 || sample.Speeds.GpuSpeedLevel >= 100)
            throw new InvalidDataException("WMI control RPM interval exceeds the 10000-RPM plausibility ceiling.");
        return sample;
    }
}
