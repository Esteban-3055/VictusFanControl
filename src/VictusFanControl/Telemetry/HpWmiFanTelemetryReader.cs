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

    // Shared across reader recreation by TelemetryWorker recovery. An abandoned
    // synchronous WMI call must not create an unbounded series of worker tasks.
    private static readonly SemaphoreSlim ProductionAdmission = new(1, 1);
    private readonly SemaphoreSlim _admission;
    private readonly object _gate = new();
    private readonly Func<HpBiosRequest, HpBiosResponse> _send;
    private readonly Func<long> _milliseconds;
    private readonly Func<DateTimeOffset> _utcNow;
    private Task? _pending;
    private HpWmiFanTelemetrySample? _sample;
    private long _nextAttempt;
    private long _startedAt;
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

        _admission = ProductionAdmission;
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
        _admission = admission;
    }

    public string Diagnostic { get { lock (_gate) return _diagnostic; } }
    public int Recoveries { get { lock (_gate) return _recoveries; } }

    public HpWmiFanTelemetrySample? ReadCached()
    {
        lock (_gate)
        {
            if (_disposed || _paused) return null;
            var now = _milliseconds();
            if (_pending is { IsCompleted: false } && now - _startedAt >= QueryTimeoutMilliseconds)
            {
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
                if (_admission.Wait(0))
                {
                    _startedAt = now;
                    _timedOut = false;
                    var epoch = _epoch;
                    var sampledAt = _utcNow();
                    _pending = Task.Run(() => Query(epoch, now, sampledAt));
                }
                else
                {
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

    public async Task WaitForQuiescenceAsync(CancellationToken cancellationToken)
    {
        await _admission.WaitAsync(cancellationToken).ConfigureAwait(false);
        _admission.Release();
    }

    public static async Task WaitForProductionQuiescenceAsync(CancellationToken cancellationToken)
    {
        await ProductionAdmission.WaitAsync(cancellationToken).ConfigureAwait(false);
        ProductionAdmission.Release();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _epoch++;
            _sample = null;
        }
        // Do not wait indefinitely for a synchronous WMI provider. Query owns
        // the shared slot until its finally block, even after reader disposal.
    }

    internal Task PendingQuery { get { lock (_gate) return _pending ?? Task.CompletedTask; } }

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

    private void Query(int epoch, long startedAt, DateTimeOffset sampledAt)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch) return;
            }
            var sample = Decode(_send(Hp8C40BiosFanControl.BuildGetFanLevelRequest()), sampledAt, startedAt);
            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch) return;
                var now = _milliseconds();
                if (_timedOut || now - startedAt >= QueryTimeoutMilliseconds)
                {
                    Fail("Late HP WMI fan query discarded after timeout.");
                    _nextAttempt = now + FailureBackoffMilliseconds;
                    return;
                }
                if (now - startedAt >= MaximumSampleAgeMilliseconds)
                {
                    Fail("HP WMI fan query completed with an expired sample; discarded.");
                    _nextAttempt = now + FailureBackoffMilliseconds;
                    return;
                }
                _sample = sample;
                _diagnostic = "OK (HP WMI 20008h/2Dh -> ACPI; nominal RPM, resolution 100 RPM).";
                _nextAttempt = now + PollIntervalMilliseconds;
                if (_failed) _recoveries++;
                _failed = false;
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (_disposed || _paused || _epoch != epoch) return;
                Fail($"HP WMI fan read failed: {ex.Message}");
                _nextAttempt = _milliseconds() + FailureBackoffMilliseconds;
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    private void Fail(string diagnostic)
    {
        _sample = null;
        _failed = true;
        _diagnostic = diagnostic;
    }
}
