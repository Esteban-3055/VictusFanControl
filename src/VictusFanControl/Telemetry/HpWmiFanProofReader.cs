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
    private readonly HpWmiFanAcquisitionDiagnostics _acquisitionDiagnostics;

    public HpWmiFanProofReader()
    {
        _admission = HpWmiFanTelemetryReader.SharedReadAdmission;
        _acquisitionDiagnostics = HpWmiFanAcquisitionDiagnostics.For(_admission);
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
        _acquisitionDiagnostics = HpWmiFanAcquisitionDiagnostics.For(_admission);
        _milliseconds = milliseconds;
        _maximumWait = maximumWait;
    }

    public async ValueTask<HpWmiFanProofSample> ReadFreshAsync(CancellationToken cancellationToken)
    {
        var waitStarted = _milliseconds();
        var acquisition = _acquisitionDiagnostics.Begin(HpWmiFanAcquisitionPurpose.Control, waitStarted);

        bool admitted;
        try
        {
            admitted = await _admission.WaitAsync(_maximumWait, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "admission-canceled",
                _milliseconds(),
                "Control caller was canceled before acquiring the shared HP WMI slot.");
            throw;
        }

        if (!admitted)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "admission-timeout",
                _milliseconds(),
                "Control proof admission timed out while a prior native read still owned the slot.");
            throw new TimeoutException("HP WMI proof admission timed out; prior native read still owns the slot.");
        }

        _acquisitionDiagnostics.MarkAdmitted(acquisition, _milliseconds());

        Task<(HpWmiFanProofSample Sample, Action<HpWmiFanTelemetrySample> Publish)> pending;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            pending = Task.Run(() =>
            {
                try
                {
                    var publish = HpWmiFanSamplePublication.Capture(_admission);
                    var started = _milliseconds();
                    _acquisitionDiagnostics.MarkNativeStarted(acquisition, started);
                    var utc = DateTimeOffset.UtcNow;
                    var sequence = Interlocked.Increment(ref _sequence);

                    HpBiosResponse response;
                    try
                    {
                        response = _send(Hp8C40BiosFanControl.BuildGetFanLevelRequest());
                    }
                    catch (Exception ex)
                    {
                        var nativeCompleted = _milliseconds();
                        _acquisitionDiagnostics.MarkNativeCompleted(acquisition, nativeCompleted);
                        _acquisitionDiagnostics.Complete(acquisition, "native-failed", nativeCompleted, ex.Message);
                        throw;
                    }

                    var completedAt = _milliseconds();
                    _acquisitionDiagnostics.MarkNativeCompleted(acquisition, completedAt);

                    HpWmiFanTelemetrySample speeds;
                    try
                    {
                        speeds = HpWmiFanTelemetryReader.Decode(response, utc, started);
                    }
                    catch (Exception ex)
                    {
                        _acquisitionDiagnostics.Complete(acquisition, "response-rejected", completedAt, ex.Message);
                        throw;
                    }

                    _acquisitionDiagnostics.MarkNativeDecoded(acquisition, completedAt);
                    return (Sample: new HpWmiFanProofSample(speeds, sequence), Publish: publish);
                }
                catch (Exception ex)
                {
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "unexpected-failure",
                        _milliseconds(),
                        ex.Message);
                    throw;
                }
                finally
                {
                    _admission.Release();
                }
            });
        }
        catch (OperationCanceledException)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "canceled-before-native",
                _milliseconds(),
                "Control caller was canceled after admission but before the native worker was started.");
            _admission.Release();
            throw;
        }
        catch
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "unexpected-failure",
                _milliseconds(),
                "Control proof worker could not be scheduled after admission.");
            _admission.Release();
            throw;
        }

        // Observe a native failure even if the awaiting caller has timed out.
        _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var remaining = _maximumWait - TimeSpan.FromMilliseconds(_milliseconds() - waitStarted);
        if (remaining <= TimeSpan.Zero)
        {
            _acquisitionDiagnostics.MarkWaiterTimeout(
                acquisition,
                _milliseconds(),
                "Control proof acquisition budget expired before awaiting native completion.");
            throw new TimeoutException("HP WMI proof acquisition budget expired.");
        }

        (HpWmiFanProofSample Sample, Action<HpWmiFanTelemetrySample> Publish) completed;
        try
        {
            completed = await pending.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _acquisitionDiagnostics.MarkWaiterTimeout(
                acquisition,
                _milliseconds(),
                "Control proof caller timed out while the synchronous native WMI call retained the slot.");
            throw;
        }
        catch (OperationCanceledException)
        {
            _acquisitionDiagnostics.MarkWaiterCanceled(
                acquisition,
                _milliseconds(),
                "Control proof caller was canceled while the synchronous native WMI call retained the slot.");
            throw;
        }

        var sample = completed.Sample;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            _acquisitionDiagnostics.MarkWaiterCanceled(
                acquisition,
                _milliseconds(),
                "Control proof caller was canceled after native completion and before publication.");
            throw;
        }

        var age = _milliseconds() - sample.Speeds.StartedAtMilliseconds;
        if (age < 0 || age >= MaximumWaitMilliseconds)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "expired",
                _milliseconds(),
                "Control proof sample expired from query start before it could be accepted.");
            throw new InvalidDataException("HP WMI proof sample expired from query start; result discarded.");
        }

        if (sample.Speeds.CpuSpeedLevel >= 100 || sample.Speeds.GpuSpeedLevel >= 100)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "response-rejected",
                _milliseconds(),
                "WMI control RPM interval exceeds the 10000-RPM plausibility ceiling.");
            throw new InvalidDataException("WMI control RPM interval exceeds the 10000-RPM plausibility ceiling.");
        }

        try
        {
            completed.Publish(sample.Speeds);
        }
        catch (Exception ex)
        {
            _acquisitionDiagnostics.Complete(acquisition, "publish-failed", _milliseconds(), ex.Message);
            throw;
        }

        _acquisitionDiagnostics.Complete(acquisition, "accepted", _milliseconds());
        return sample;
    }

}
