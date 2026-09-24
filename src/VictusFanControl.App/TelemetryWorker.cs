using System.Diagnostics;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class TelemetryWorker : IAsyncDisposable
{
    private const int NormalIntervalMs = 1000;
    private const int ResumeSettleMs = 1500;
    private const int HealthySamplesRequired = 3;
    private const int GapThresholdMs = 10_000;

    private readonly string _modulesDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _commandGate = new();

    private HardwareTelemetryReader? _reader;
    private Task? _loop;
    private bool _suspended;
    private bool _recoveryRequested = true;
    private string _recoveryReason = "Initial telemetry validation.";
    private long _lastLoopTick = Environment.TickCount64;

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
        }

        StateMachine.Transition(SystemState.Suspending, $"Suspend detected ({source}).");
        Log($"Suspend detected by {source}.");
        StateMachine.Transition(SystemState.Suspended, "Telemetry paused while Windows is suspended.");
        Wake();
    }

    public void NotifyResume(string source)
    {
        lock (_commandGate)
        {
            _suspended = false;
            _recoveryRequested = true;
            _recoveryReason = $"Resume detected ({source}).";
        }

        StateMachine.Transition(SystemState.Resuming, _recoveryReason);
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

                if (!snapshot.IsComplete)
                {
                    StateMachine.Transition(
                        SystemState.Degraded,
                        "A telemetry snapshot was incomplete; HP firmware must remain authoritative.");
                }
                else if (StateMachine.State == SystemState.Degraded)
                {
                    RequestRecovery("Complete telemetry returned after a degraded sample; revalidating.");
                }
            }
            catch (Exception ex)
            {
                StateMachine.Transition(SystemState.Degraded, $"Telemetry exception: {ex.Message}");
                Log($"Telemetry exception: {ex}");
                RequestRecovery("Telemetry read threw an exception.");
            }

            await WaitOrWakeAsync(NormalIntervalMs, cancellationToken).ConfigureAwait(false);
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

            // Prime RAPL and GetSystemTimes differential counters.
            _ = _reader!.ReadSnapshot();
            await Task.Delay(NormalIntervalMs, cancellationToken).ConfigureAwait(false);

            var consecutiveComplete = 0;
            for (var attempt = 1; attempt <= 12 && !cancellationToken.IsCancellationRequested; attempt++)
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
                    if (consecutiveComplete >= HealthySamplesRequired)
                    {
                        StateMachine.Transition(
                            SystemState.Healthy,
                            $"{HealthySamplesRequired} consecutive complete telemetry snapshots.");
                        Log("Recovery completed; telemetry is healthy.");
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
                "Telemetry did not produce three consecutive complete snapshots after recovery.");
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
        try
        {
            await _wake.WaitAsync(milliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
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

    private void Log(string text) =>
        EventLogged?.Invoke(this, $"{DateTime.Now:HH:mm:ss}  {text}");

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
