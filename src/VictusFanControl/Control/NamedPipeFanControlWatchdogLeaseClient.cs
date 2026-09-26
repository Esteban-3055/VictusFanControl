using System.Diagnostics;
using System.IO.Pipes;
using VictusFanControl.Runtime;

namespace VictusFanControl.Control;

internal readonly record struct FanControlWatchdogLeaseClientTiming(
    TimeSpan ConnectTimeout,
    TimeSpan RequestTimeout,
    TimeSpan ReleaseTimeout)
{
    public static FanControlWatchdogLeaseClientTiming Production => new(
        ConnectTimeout: TimeSpan.FromSeconds(3),
        RequestTimeout: TimeSpan.FromSeconds(4),
        ReleaseTimeout: TimeSpan.FromSeconds(10));
}

public sealed class NamedPipeFanControlWatchdogLeaseClient :
    IFanControlWatchdogLeaseClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _pipeName;
    private readonly IActiveTimeClock _activeTimeClock;
    private readonly FanControlWatchdogLeaseClientTiming _timing;

    private NamedPipeClientStream? _pipe;
    private Guid? _sessionId;
    private long? _generation;
    private ClientPhase _phase;
    private bool _disposed;

    public NamedPipeFanControlWatchdogLeaseClient()
        : this(
            FanControlWatchdogLeaseContract.PipeName,
            activeTimeClock: null,
            timing: null)
    {
    }

    internal NamedPipeFanControlWatchdogLeaseClient(
        string pipeName,
        IActiveTimeClock? activeTimeClock = null,
        FanControlWatchdogLeaseClientTiming? timing = null)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException(
                "Watchdog pipe name cannot be empty.",
                nameof(pipeName));
        }

        _pipeName = pipeName;
        _activeTimeClock =
            activeTimeClock ??
            new WindowsActiveTimeClock();
        _timing =
            timing ??
            FanControlWatchdogLeaseClientTiming.Production;
    }

    public async ValueTask PrepareAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase != ClientPhase.None)
            {
                throw new InvalidOperationException(
                    $"Watchdog Prepare requires no active local lease; phase={_phase}.");
            }

            await EnsureConnectedLockedAsync(cancellationToken)
                .ConfigureAwait(false);

            var response = await SendLockedAsync(
                NewRequest(FanControlWatchdogLeaseContract.Prepare),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ApplyLeaseResponse(response, ClientPhase.Prepared);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CancelPreparedAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase == ClientPhase.None)
            {
                return;
            }

            if (_phase != ClientPhase.Prepared)
            {
                throw new InvalidOperationException(
                    $"Watchdog CancelPrepared requires PREPARED; phase={_phase}.");
            }

            await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.CancelPrepared),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ClearLeaseLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteIntentAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase is not
                (ClientPhase.Prepared or ClientPhase.Owned))
            {
                throw new InvalidOperationException(
                    $"Watchdog WriteIntent requires PREPARED or OWNED; phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.WriteIntent,
                    cpuLevel,
                    gpuLevel),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ApplyLeaseResponse(response, ClientPhase.WriteArmed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask AbortWriteIntentAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase != ClientPhase.WriteArmed)
            {
                throw new InvalidOperationException(
                    $"Watchdog AbortWriteIntent requires WRITE_ARMED; phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.AbortWriteIntent),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            var next = response.Phase switch
            {
                "Prepared" => ClientPhase.Prepared,
                "Owned" => ClientPhase.Owned,
                _ => throw new InvalidDataException(
                    $"Watchdog AbortWriteIntent returned unexpected phase '{response.Phase ?? "null"}'.")
            };

            ApplyLeaseResponse(response, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask CommitAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase != ClientPhase.WriteArmed)
            {
                throw new InvalidOperationException(
                    $"Watchdog Commit requires WRITE_ARMED; phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.Commit,
                    cpuLevel,
                    gpuLevel),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ApplyLeaseResponse(response, ClientPhase.Owned);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ProbeAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase != ClientPhase.Owned)
            {
                throw new InvalidOperationException(
                    $"Watchdog Probe requires OWNED; phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.Probe),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ValidateStableLeaseResponse(
                response,
                ClientPhase.Owned);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask HeartbeatAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase != ClientPhase.Owned)
            {
                throw new InvalidOperationException(
                    $"Watchdog Heartbeat requires OWNED; phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.Heartbeat),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ValidateStableLeaseResponse(
                response,
                ClientPhase.Owned);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RestoreBeginAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase == ClientPhase.None)
            {
                return;
            }

            if (_phase == ClientPhase.Prepared)
            {
                await SendLockedAsync(
                    NewLeaseRequest(
                        FanControlWatchdogLeaseContract.CancelPrepared),
                    _timing.RequestTimeout,
                    cancellationToken).ConfigureAwait(false);

                ClearLeaseLocked();
                return;
            }

            if (_phase == ClientPhase.Restoring)
            {
                return;
            }

            if (_phase is not
                (ClientPhase.WriteArmed or ClientPhase.Owned))
            {
                throw new InvalidOperationException(
                    $"Watchdog RestoreBegin cannot run from phase={_phase}.");
            }

            var response = await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.RestoreBegin),
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ApplyLeaseResponse(response, ClientPhase.Restoring);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReleaseAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (_phase == ClientPhase.None)
            {
                return;
            }

            if (_phase == ClientPhase.Prepared)
            {
                await SendLockedAsync(
                    NewLeaseRequest(
                        FanControlWatchdogLeaseContract.CancelPrepared),
                    _timing.RequestTimeout,
                    cancellationToken).ConfigureAwait(false);

                ClearLeaseLocked();
                return;
            }

            // A failed/broken RestoreBegin response must not make the live
            // controller abandon its durable lease. Try to move into RESTORING
            // again after reconnecting before asking for Release.
            if (_phase is
                (ClientPhase.WriteArmed or ClientPhase.Owned))
            {
                var begin = await SendLockedAsync(
                    NewLeaseRequest(
                        FanControlWatchdogLeaseContract.RestoreBegin),
                    _timing.RequestTimeout,
                    cancellationToken).ConfigureAwait(false);

                ApplyLeaseResponse(begin, ClientPhase.Restoring);
            }

            if (_phase != ClientPhase.Restoring)
            {
                throw new InvalidOperationException(
                    $"Watchdog Release requires RESTORING; phase={_phase}.");
            }

            await SendLockedAsync(
                NewLeaseRequest(
                    FanControlWatchdogLeaseContract.Release),
                _timing.ReleaseTimeout,
                cancellationToken).ConfigureAwait(false);

            ClearLeaseLocked();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposePipeLocked();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask EnsureConnectedLockedAsync(
        CancellationToken cancellationToken)
    {
        if (_pipe?.IsConnected == true)
        {
            return;
        }

        DisposePipeLocked();

        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        try
        {
            await RunWithActiveTimeoutAsync(
                    async timeoutToken =>
                    {
                        await pipe.ConnectAsync(timeoutToken)
                            .ConfigureAwait(false);
                    },
                    _timing.ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            var process = Process.GetCurrentProcess();
            var hello = NewRequest(
                FanControlWatchdogLeaseContract.Hello,
                controllerPid: process.Id,
                controllerStartUtcTicks:
                    process.StartTime.ToUniversalTime().Ticks);

            _pipe = pipe;

            var response = await SendConnectedLockedAsync(
                hello,
                _timing.RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            if (!string.Equals(
                    response.Code,
                    "HELLO_OK",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Watchdog Hello returned unexpected code '{response.Code}'.");
            }
        }
        catch
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                _pipe = null;
            }

            pipe.Dispose();
            throw;
        }
    }

    private async ValueTask<FanControlWatchdogLeaseResponse> SendLockedAsync(
        FanControlWatchdogLeaseRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureConnectedLockedAsync(cancellationToken)
                .ConfigureAwait(false);

            return await SendConnectedLockedAsync(
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Connect/request timeout is watchdog transport unavailability,
            // not caller cancellation. Give the controller a stable reason so
            // Gate E cannot mistake an unrelated EC failure for watchdog loss.
            DisposePipeLocked();
            throw new FanControlWatchdogTransportException(
                request.Type,
                ex);
        }
        catch (IOException ex)
            when (ex is not FanControlWatchdogProtocolException &&
                  ex is not FanControlWatchdogTransportException)
        {
            // Broken/closed named pipes land here. Preserve session/generation:
            // the durable service journal remains authoritative and a later
            // restore retry may reconnect to the restarted watchdog.
            DisposePipeLocked();
            throw new FanControlWatchdogTransportException(
                request.Type,
                ex);
        }
        catch
        {
            // Protocol/data/caller-cancellation failures still invalidate the
            // current stream, but keep their original classification.
            DisposePipeLocked();
            throw;
        }
    }

    private async ValueTask<FanControlWatchdogLeaseResponse> SendConnectedLockedAsync(
        FanControlWatchdogLeaseRequest request,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe ??
            throw new InvalidOperationException(
                "Watchdog pipe is not connected.");

        var response =
            await RunWithActiveTimeoutAsync(
                    async timeoutToken =>
                    {
                        await FanControlWatchdogLeaseCodec.WriteRequestAsync(
                                pipe,
                                request,
                                timeoutToken)
                            .ConfigureAwait(false);

                        return
                            await FanControlWatchdogLeaseCodec.ReadResponseAsync(
                                    pipe,
                                    timeoutToken)
                                .ConfigureAwait(false) ??
                            throw new EndOfStreamException(
                                "Watchdog closed the pipe before responding.");
                    },
                    requestTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

        if (response.ProtocolVersion !=
            FanControlWatchdogLeaseContract.ProtocolVersion)
        {
            throw new InvalidDataException(
                $"Watchdog protocol version mismatch: {response.ProtocolVersion}.");
        }

        if (response.RequestId != request.RequestId)
        {
            throw new InvalidDataException(
                "Watchdog response request-id mismatch.");
        }

        if (!response.Ok)
        {
            throw new WatchdogLeaseRejectedException(
                response.Code,
                response.Message);
        }

        return response;
    }

    private async Task RunWithActiveTimeoutAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await RunWithActiveTimeoutAsync(
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return true;
                },
                timeout,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> RunWithActiveTimeoutAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        using var stopMonitoring = new CancellationTokenSource();

        var timeoutMonitor =
            ActiveTimeClock.CancelAfterActiveTimeAsync(
                timeoutCancellation,
                _activeTimeClock,
                timeout,
                stopMonitoring.Token);

        try
        {
            return await operation(timeoutCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            stopMonitoring.Cancel();
            try
            {
                await timeoutMonitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stopMonitoring.IsCancellationRequested)
            {
            }
        }
    }

    private FanControlWatchdogLeaseRequest NewLeaseRequest(
        string type,
        int? cpuLevel = null,
        int? gpuLevel = null)
    {
        if (!_sessionId.HasValue ||
            !_generation.HasValue)
        {
            throw new InvalidOperationException(
                $"Watchdog request '{type}' requires an active local lease.");
        }

        return NewRequest(
            type,
            sessionId: _sessionId,
            generation: _generation,
            cpuLevel: cpuLevel,
            gpuLevel: gpuLevel);
    }

    private static FanControlWatchdogLeaseRequest NewRequest(
        string type,
        int? controllerPid = null,
        long? controllerStartUtcTicks = null,
        Guid? sessionId = null,
        long? generation = null,
        int? cpuLevel = null,
        int? gpuLevel = null) =>
        new(
            FanControlWatchdogLeaseContract.ProtocolVersion,
            Guid.NewGuid(),
            type,
            controllerPid,
            controllerStartUtcTicks,
            sessionId,
            generation,
            cpuLevel,
            gpuLevel);

    private void ApplyLeaseResponse(
        FanControlWatchdogLeaseResponse response,
        ClientPhase phase)
    {
        if (!response.SessionId.HasValue ||
            !response.Generation.HasValue)
        {
            throw new InvalidDataException(
                "Watchdog lease response omitted session/generation.");
        }

        _sessionId = response.SessionId.Value;
        _generation = response.Generation.Value;
        _phase = phase;

        if (!string.Equals(
                response.Phase,
                PhaseName(phase),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Watchdog phase mismatch: expected {PhaseName(phase)}, received '{response.Phase ?? "null"}'.");
        }
    }

    private void ValidateStableLeaseResponse(
        FanControlWatchdogLeaseResponse response,
        ClientPhase expectedPhase)
    {
        if (response.SessionId != _sessionId ||
            response.Generation != _generation ||
            !string.Equals(
                response.Phase,
                PhaseName(expectedPhase),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Watchdog stable-state response did not match the active lease.");
        }
    }

    private void ClearLeaseLocked()
    {
        _sessionId = null;
        _generation = null;
        _phase = ClientPhase.None;
    }

    private void DisposePipeLocked()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        _pipe = null;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string PhaseName(
        ClientPhase phase) =>
        phase switch
        {
            ClientPhase.Prepared => "Prepared",
            ClientPhase.WriteArmed => "WriteArmed",
            ClientPhase.Owned => "Owned",
            ClientPhase.Restoring => "Restoring",
            _ => throw new InvalidOperationException(
                $"No protocol phase exists for {phase}.")
        };

    private enum ClientPhase
    {
        None,
        Prepared,
        WriteArmed,
        Owned,
        Restoring
    }

    private sealed class WatchdogLeaseRejectedException :
        InvalidOperationException
    {
        public WatchdogLeaseRejectedException(
            string code,
            string message)
            : base($"Watchdog lease rejected request: {code}: {message}")
        {
            Code = code;
        }

        public string Code { get; }
    }

}
