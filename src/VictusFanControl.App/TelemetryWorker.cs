using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class TelemetryWorker : IAsyncDisposable
{
    private const int NormalIntervalMs = 1000;
    private const int ResumeSettleMs = 1500;
    private const int HealthySamplesRequired = 3;
    private const int ResumeHealthySamplesRequired = 5;
    private const int RecoveryAfterIncompleteSamples = 3;
    private const int GapThresholdMs = 10_000;

    private readonly string _modulesDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _commandGate = new();

    private HardwareTelemetryReader? _reader;
    private Task? _loop;
    private bool _suspended;
    private bool _recoveryRequested = true;
    private bool _resumeValidationActive;
    private string _recoveryReason = "Initial telemetry validation.";
    private long _lastLoopTick = Environment.TickCount64;
    private long _logSequence;
    private int _degradedCompleteStreak;
    private int _degradedIncompleteStreak;

    public TelemetryWorker(string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
        StateMachine = new RuntimeStateMachine();
    }

    public RuntimeStateMachine StateMachine { get; }

    public event EventHandler<TelemetrySnapshot>? SnapshotAvailable;
    public event EventHandler<string>? DiagnosticsAvailable;
    public event EventHandler<string>? EventLogged;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void NotifySuspend(string source)
    {
        lock (_commandGate)
        {
            _suspended = true;
            _recoveryRequested = false;
            _resumeValidationActive = false;
            _degradedCompleteStreak = 0;
            _degradedIncompleteStreak = 0;
        }

        StateMachine.Transition(SystemState.Suspending, $"Suspend detected ({source}).");
        Log($"Suspend detected by {source}.");
        StateMachine.Transition(SystemState.Suspended, "Telemetry paused while Windows is suspended.");
        Wake();
    }

    public void NotifyResume(string source)
    {
        bool accepted;

        lock (_commandGate)
        {
            _suspended = false;

            // Windows commonly emits PBT_APMRESUMEAUTOMATIC followed by
            // PBT_APMRESUMESUSPEND for the same wake cycle. Treat both as one
            // logical resume so the telemetry stack is rebuilt only once.
            accepted = !_resumeValidationActive;
            if (accepted)
            {
                _resumeValidationActive = true;
                _recoveryRequested = true;
                _recoveryReason = $"Resume detected ({source}).";
                _degradedCompleteStreak = 0;
                _degradedIncompleteStreak = 0;
            }
        }

        if (!accepted)
        {
            Log($"Duplicate resume signal coalesced: {source}.");
            return;
        }

        StateMachine.Transition(SystemState.Resuming, $"Resume detected ({source}).");
        Log($"Resume detected by {source}; telemetry revalidation requested.");
        Wake();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        Wake();

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _reader?.Dispose();
        _wake.Dispose();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsSuspended())
            {
                await WaitOrWakeAsync(500, cancellationToken).ConfigureAwait(false);
                _lastLoopTick = Environment.TickCount64;
                continue;
            }

            var nowTick = Environment.TickCount64;
            var gap = unchecked(nowTick - _lastLoopTick);
            _lastLoopTick = nowTick;

            if (gap > GapThresholdMs && !RecoveryRequested())
            {
                RequestRecovery($"Scheduling gap of {gap} ms detected; possible missed resume event.");
                StateMachine.Transition(SystemState.Resuming, "Fallback timer-gap detector triggered.");
                Log($"Fallback resume detector saw a {gap} ms scheduling gap.");
            }

            if (RecoveryRequested())
            {
                await RecoverAndValidateAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            EnsureReader();

            try
            {
                var snapshot = _reader!.ReadSnapshot();
                SnapshotAvailable?.Invoke(this, snapshot);
                PublishDiagnostics();

                HandleNormalSnapshotHealth(snapshot);
            }
            catch (Exception ex)
            {
                _degradedCompleteStreak = 0;
                _degradedIncompleteStreak = RecoveryAfterIncompleteSamples;

                StateMachine.Transition(SystemState.Degraded, $"Telemetry exception: {ex.Message}");
                Log($"Telemetry exception: {ex}");
                RequestRecovery("Telemetry read threw an exception.");
            }

            await WaitOrWakeAsync(NormalIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private void HandleNormalSnapshotHealth(TelemetrySnapshot snapshot)
    {
        if (snapshot.IsComplete)
        {
            _degradedIncompleteStreak = 0;

            if (StateMachine.State == SystemState.Degraded)
            {
                _degradedCompleteStreak++;
                if (_degradedCompleteStreak >= HealthySamplesRequired)
                {
                    StateMachine.Transition(
                        SystemState.Healthy,
                        $"{HealthySamplesRequired} consecutive complete snapshots after a transient degradation.");
                    Log("Telemetry recovered from a transient degradation without rebuilding the backends.");
                    _degradedCompleteStreak = 0;
                }
            }
            else
            {
                _degradedCompleteStreak = 0;
            }

            return;
        }

        _degradedCompleteStreak = 0;
        _degradedIncompleteStreak++;

        var missing = DescribeMissing(snapshot);

        StateMachine.Transition(
            SystemState.Degraded,
            $"Incomplete telemetry snapshot ({_degradedIncompleteStreak}/{RecoveryAfterIncompleteSamples}): {missing}.");

        if (_degradedIncompleteStreak == 1)
        {
            Log($"Transient telemetry degradation detected; missing={missing}.");
        }

        if (_degradedIncompleteStreak >= RecoveryAfterIncompleteSamples)
        {
            Log($"{_degradedIncompleteStreak} consecutive incomplete snapshots; full telemetry recovery requested.");
            RequestRecovery("Repeated incomplete telemetry snapshots.");
        }
    }

    private async Task RecoverAndValidateAsync(CancellationToken cancellationToken)
    {
        string reason;
        lock (_commandGate)
        {
            _recoveryRequested = false;
            reason = _recoveryReason;
        }

        StateMachine.Transition(SystemState.Recovering, reason);
        Log($"Recovery started: {reason}");

        _reader?.Dispose();
        _reader = null;

        await Task.Delay(ResumeSettleMs, cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureReader();

            // Prime RAPL and GetSystemTimes differential counters. The priming
            // sample is intentionally not considered for health.
            _ = _reader!.ReadSnapshot();
            await Task.Delay(NormalIntervalMs, cancellationToken).ConfigureAwait(false);

            var requiredComplete = _resumeValidationActive
                ? ResumeHealthySamplesRequired
                : HealthySamplesRequired;

            var consecutiveComplete = 0;
            for (var attempt = 1; attempt <= 16 && !cancellationToken.IsCancellationRequested; attempt++)
            {
                if (IsSuspended())
                {
                    return;
                }

                var snapshot = _reader.ReadSnapshot();
                SnapshotAvailable?.Invoke(this, snapshot);
                PublishDiagnostics();

                if (snapshot.IsComplete)
                {
                    consecutiveComplete++;
                    if (consecutiveComplete >= requiredComplete)
                    {
                        _degradedCompleteStreak = 0;
                        _degradedIncompleteStreak = 0;

                        lock (_commandGate)
                        {
                            _resumeValidationActive = false;
                        }

                        StateMachine.Transition(
                            SystemState.Healthy,
                            $"{requiredComplete} consecutive complete telemetry snapshots.");
                        Log($"Recovery completed; telemetry is healthy after {requiredComplete} complete snapshots.");
                        return;
                    }
                }
                else
                {
                    consecutiveComplete = 0;
                }

                await Task.Delay(NormalIntervalMs, cancellationToken).ConfigureAwait(false);
            }

            StateMachine.Transition(
                SystemState.Degraded,
                $"Telemetry did not produce {requiredComplete} consecutive complete snapshots after recovery.");
            Log("Recovery validation did not reach the healthy criterion.");
            RequestRecovery("Retrying degraded telemetry.");
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StateMachine.Transition(SystemState.Faulted, $"Recovery failed: {ex.Message}");
            Log($"Recovery failed: {ex}");
            RequestRecovery("Retrying after recovery failure.");
            await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
        }
    }


    private static string DescribeMissing(TelemetrySnapshot snapshot)
    {
        var missing = new List<string>(8);

        if (!snapshot.CpuTemperatureC.HasValue) missing.Add("cpu_temp");
        if (!snapshot.CpuPackagePowerW.HasValue) missing.Add("cpu_power");
        if (!snapshot.CpuLoadPercent.HasValue) missing.Add("cpu_load");
        if (!snapshot.GpuTemperatureC.HasValue) missing.Add("gpu_temp");
        if (!snapshot.GpuPowerW.HasValue) missing.Add("gpu_power");
        if (!snapshot.GpuLoadPercent.HasValue) missing.Add("gpu_load");
        if (!snapshot.CpuFanRpm.HasValue) missing.Add("cpu_fan");
        if (!snapshot.GpuFanRpm.HasValue) missing.Add("gpu_fan");

        return missing.Count == 0 ? "unknown" : string.Join(",", missing);
    }

    private void EnsureReader()
    {
        _reader ??= new HardwareTelemetryReader(_modulesDirectory);
        if (!_reader.BackendsInitialized)
        {
            throw new InvalidOperationException(
                "One or more telemetry backends failed to initialize. " +
                string.Join(" | ", _reader.GetBackendDiagnostics()));
        }
    }

    private bool IsSuspended()
    {
        lock (_commandGate)
        {
            return _suspended;
        }
    }

    private bool RecoveryRequested()
    {
        lock (_commandGate)
        {
            return _recoveryRequested;
        }
    }

    private void RequestRecovery(string reason)
    {
        lock (_commandGate)
        {
            _recoveryRequested = true;
            _recoveryReason = reason;
        }

        Wake();
    }

    private async Task WaitOrWakeAsync(int milliseconds, CancellationToken cancellationToken)
    {
        await _wake.WaitAsync(milliseconds, cancellationToken).ConfigureAwait(false);
    }

    private void PublishDiagnostics()
    {
        if (_reader is null)
        {
            return;
        }

        var lines = _reader.GetBackendDiagnostics()
            .Concat(_reader.GetReadDiagnostics())
            .Concat(_reader.GetHealthSummary());

        DiagnosticsAvailable?.Invoke(this, string.Join(Environment.NewLine, lines));
    }

    private void Log(string text)
    {
        var sequence = Interlocked.Increment(ref _logSequence);
        EventLogged?.Invoke(
            this,
            $"#{sequence:0000}  {DateTime.Now:HH:mm:ss.fff}  {text}");
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
