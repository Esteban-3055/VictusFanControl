using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Telemetry;

public sealed record HpWmiFanTelemetrySample(
    byte CpuSpeedLevel,
    byte GpuSpeedLevel,
    DateTimeOffset SampledAtUtc,
    long StartedAtMilliseconds)
{
    public const int ResolutionRpm = 100;
    public int CpuNominalRpm => CpuSpeedLevel * ResolutionRpm;
    public int GpuNominalRpm => GpuSpeedLevel * ResolutionRpm;
}

/// <summary>
/// Read-only HP 8C40/F.18 telemetry: 20008h/2Dh, never setpoints or ownership.
/// ReadCached never waits for WMI. A synchronous provider call cannot safely
/// be forcibly cancelled: timeout invalidates its result, retains the single
/// admission slot until it returns, and never starts replacement calls in parallel.
/// </summary>
public sealed class HpWmiFanTelemetryReader : IDisposable
{
    public const int PollIntervalMilliseconds = 1000;
    public const int MaximumSampleAgeMilliseconds = 3000;
    public const int QueryTimeoutMilliseconds = 5000;
    private const int FailureBackoffMilliseconds = 5000;

    internal static SemaphoreSlim SharedReadAdmission => HpWmiFanSampleBroker.Production.Admission;
    private readonly HpWmiFanSampleBroker _broker;
    private readonly SemaphoreSlim _admission;
    private readonly object _gate = new();
    private readonly Func<HpBiosRequest, HpBiosResponse> _send;
    private readonly Func<long> _milliseconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly HpWmiFanAcquisitionDiagnostics _acquisitionDiagnostics;
    private Task? _pending;
    private HpWmiFanAcquisitionDiagnostics.Operation? _pendingAcquisition;
    private HpWmiFanTelemetrySample? _sample;
    private long _nextAttempt;
    private long _startedAt;
    private long _latestOutcomeSequence;
    private int _epoch;
    private bool _timedOut;
    private bool _paused;
    private bool _disposed;
    private string _diagnostic = "Waiting for first HP WMI fan sample.";
    private int _recoveries;
    private bool _failed;

    public HpWmiFanTelemetryReader(HardwareTargetProfile target)
    {
        if (target != Hp8C40TargetProfile.Instance)
        {
            throw new ArgumentException("Fan WMI telemetry is qualified only for exact HP 8C40/F.18.", nameof(target));
        }

        _broker = HpWmiFanSampleBroker.Production;
        _admission = _broker.Admission;
        _acquisitionDiagnostics = _broker.Diagnostics;
        _milliseconds = () => Environment.TickCount64;
        _utcNow = () => DateTimeOffset.UtcNow;
        HpOmenBiosWmiClient? client = null;
        // Connection/discovery also happen off the fast telemetry thread.
        _send = request =>
        {
            try
            {
                client ??= new HpOmenBiosWmiClient();
                return client.SendWithResponse(request);
            }
            catch
            {
                client = null;
                throw;
            }
        };
        HpWmiFanSamplePublication.Register(_admission, this);
    }

    internal HpWmiFanTelemetryReader(
        Func<HpBiosRequest, HpBiosResponse> send,
        Func<long> milliseconds,
        Func<DateTimeOffset> utcNow,
        SemaphoreSlim admission)
    {
        _send = send;
        _milliseconds = milliseconds;
        _utcNow = utcNow;
        _broker = HpWmiFanSampleBroker.For(admission);
        _admission = _broker.Admission;
        _acquisitionDiagnostics = _broker.Diagnostics;
        HpWmiFanSamplePublication.Register(_admission, this);
    }

    public string Diagnostic { get { lock (_gate) return _diagnostic; } }
    public int Recoveries { get { lock (_gate) return _recoveries; } }
    internal string AcquisitionDiagnostic => _acquisitionDiagnostics.Describe();
    internal IReadOnlyList<string> DrainAcquisitionNotices() => _acquisitionDiagnostics.DrainNotices();

    public HpWmiFanTelemetrySample? ReadCached()
    {
        lock (_gate)
        {
            if (_disposed || _paused) return null;
            var now = _milliseconds();
            if (_pending is { IsCompleted: false } && now - _startedAt >= QueryTimeoutMilliseconds)
            {
                if (!_timedOut && _pendingAcquisition is not null)
                    _acquisitionDiagnostics.MarkLogicalTimeout(_pendingAcquisition, now);
                _timedOut = true;
                Fail("HP WMI fan query timed out; awaiting provider completion without overlapping retries.");
            }

            if (_sample is not null &&
                now - _sample.StartedAtMilliseconds >= MaximumSampleAgeMilliseconds)
            {
                _sample = null;
                _diagnostic = "HP WMI fan sample expired (age >= 3000 ms).";
            }

            if ((_pending is null || _pending.IsCompleted) && now >= _nextAttempt)
            {
                var acquisition = _acquisitionDiagnostics.Begin(HpWmiFanAcquisitionPurpose.Periodic, now);
                var lease = _broker.TryAcquirePeriodic();
                if (lease is not null)
                {
                    _acquisitionDiagnostics.MarkAdmitted(acquisition, now);
                    _pendingAcquisition = acquisition;
                    _startedAt = now;
                    _timedOut = false;
                    var epoch = _epoch;
                    try
                    {
                        var sampledAt = _utcNow();
                        _pending = _broker.RunNative(lease, () => Query(epoch, now, sampledAt, acquisition));
                    }
                    catch
                    {
                        lease.CancelBeforeNative();
                        throw;
                    }
                }
                else
                {
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "admission-busy",
                        now,
                        "Periodic poll skipped because another native HP WMI read owns the slot.");
                    _nextAttempt = now + PollIntervalMilliseconds;
                    _diagnostic = "Previous HP WMI fan reader still in flight; no overlapping query admitted.";
                }
            }

            return _sample;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            _epoch++;
            _sample = null;
            _diagnostic = "HP WMI fan telemetry paused; pre-boundary results invalidated.";
        }
    }

    public Task WaitForQuiescenceAsync(CancellationToken cancellationToken) =>
        _broker.WaitForQuiescenceAsync(cancellationToken);

    public static Task WaitForProductionQuiescenceAsync(CancellationToken cancellationToken) =>
        HpWmiFanSampleBroker.Production.WaitForQuiescenceAsync(cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _epoch++;
            _sample = null;
        }
        // The broker's native worker retains the slot after reader disposal.
    }

    internal Task PendingQuery { get { lock (_gate) return _pending ?? Task.CompletedTask; } }

    internal Action<HpWmiFanTelemetrySample>? CaptureControlSampleSink(long sequence)
    {
        lock (_gate)
        {
            if (_disposed || _paused) return null;
            var epoch = _epoch;
            return sample => PublishControlSample(sample, epoch, sequence);
        }
    }

    private void PublishControlSample(HpWmiFanTelemetrySample sample, int epoch, long sequence)
    {
        lock (_gate)
        {
            if (_disposed || _paused || _epoch != epoch) return;
            if (sequence < _latestOutcomeSequence) return;
            var now = _milliseconds();
            var age = now - sample.StartedAtMilliseconds;
            if (age < 0 || age >= MaximumSampleAgeMilliseconds) return;
            if (_sample is not null && sample.StartedAtMilliseconds < _sample.StartedAtMilliseconds) return;

            _sample = sample;
            _latestOutcomeSequence = sequence;
            _diagnostic = "OK (fresh HP WMI 20008h/2Dh control read; original acquisition time, resolution 100 RPM).";
            _nextAttempt = now + PollIntervalMilliseconds;
            if (_failed) _recoveries++;
            _failed = false;
        }
    }

    internal static HpWmiFanTelemetrySample Decode(
        HpBiosResponse response, DateTimeOffset sampledAt, long startedAt)
    {
        if (response.ReturnCode != 0)
            throw new InvalidDataException($"HP WMI GetFanLevel rejected: rc={response.ReturnCode}.");
        if (response.Data.Length < 2)
            throw new InvalidDataException("HP WMI GetFanLevel requires two fan speed bytes.");
        // Zero is valid in Firmware. 0xFF and all values >100 are invalid
        // telemetry, never interpreted as release tokens or command levels.
        if (response.Data[0] > 100 || response.Data[1] > 100)
            throw new InvalidDataException("HP WMI fan speed outside plausible 0..100 (0..10000 nominal RPM).");
        return new(response.Data[0], response.Data[1], sampledAt, startedAt);
    }

    private void Query(
        int epoch,
        long startedAt,
        DateTimeOffset sampledAt,
        HpWmiFanAcquisitionDiagnostics.Operation acquisition)
    {
        var sequence = HpWmiFanSamplePublication.NextSequence(_admission);
        try
        {
            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch)
                {
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "lifecycle-discarded",
                        _milliseconds(),
                        "Periodic acquisition was invalidated before native WMI execution.");
                    return;
                }
            }

            var nativeStarted = _milliseconds();
            _acquisitionDiagnostics.MarkNativeStarted(acquisition, nativeStarted);

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

            HpWmiFanTelemetrySample sample;
            try
            {
                sample = Decode(response, sampledAt, startedAt);
            }
            catch (Exception ex)
            {
                _acquisitionDiagnostics.Complete(acquisition, "response-rejected", completedAt, ex.Message);
                throw;
            }

            _acquisitionDiagnostics.MarkNativeDecoded(acquisition, completedAt);

            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch)
                {
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "lifecycle-discarded",
                        _milliseconds(),
                        "Periodic native result crossed a pause/dispose epoch and was discarded.");
                    return;
                }

                if (sequence < _latestOutcomeSequence)
                {
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "older-outcome-discarded",
                        _milliseconds(),
                        "A newer shared-slot outcome already owns the periodic cache ordering.");
                    return;
                }

                _latestOutcomeSequence = sequence;
                var now = _milliseconds();
                if (_timedOut || now - startedAt >= QueryTimeoutMilliseconds)
                {
                    Fail("Late HP WMI fan query discarded after timeout.");
                    _nextAttempt = now + FailureBackoffMilliseconds;
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "late-after-logical-timeout",
                        now,
                        "Native WMI returned after the periodic logical timeout.");
                    return;
                }

                if (now - startedAt >= MaximumSampleAgeMilliseconds)
                {
                    Fail("HP WMI fan query completed with an expired sample; discarded.");
                    _nextAttempt = now + FailureBackoffMilliseconds;
                    _acquisitionDiagnostics.Complete(
                        acquisition,
                        "expired",
                        now,
                        "Periodic sample exceeded the 3000-ms freshness boundary from query start.");
                    return;
                }

                _sample = sample;
                _diagnostic = "OK (HP WMI 20008h/2Dh -> ACPI; nominal RPM, resolution 100 RPM).";
                _nextAttempt = now + PollIntervalMilliseconds;
                if (_failed) _recoveries++;
                _failed = false;
                _acquisitionDiagnostics.Complete(acquisition, "accepted", now);
            }
        }
        catch (Exception ex)
        {
            _acquisitionDiagnostics.Complete(
                acquisition,
                "unexpected-failure",
                _milliseconds(),
                ex.Message);
            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch) return;
                if (sequence < _latestOutcomeSequence) return;
                _latestOutcomeSequence = sequence;
                Fail($"HP WMI fan read failed: {ex.Message}");
                _nextAttempt = _milliseconds() + FailureBackoffMilliseconds;
            }
        }
    }

    private void Fail(string diagnostic)
    {
        _sample = null;
        _failed = true;
        _diagnostic = diagnostic;
    }
}
