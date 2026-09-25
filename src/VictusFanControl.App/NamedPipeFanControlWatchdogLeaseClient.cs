using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using VictusFanControl.Control;

namespace VictusFanControl.App;

internal sealed class NamedPipeFanControlWatchdogLeaseClient :
    IFanControlWatchdogLeaseClient
{
    private static readonly TimeSpan ConnectTimeout =
        TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RequestTimeout =
        TimeSpan.FromSeconds(4);

    private static readonly TimeSpan ReleaseTimeout =
        TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private NamedPipeClientStream? _pipe;
    private Guid? _sessionId;
    private long? _generation;
    private ClientPhase _phase;
    private bool _disposed;

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
                RequestTimeout,
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
                RequestTimeout,
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
                RequestTimeout,
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
                RequestTimeout,
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
                RequestTimeout,
                cancellationToken).ConfigureAwait(false);

            ApplyLeaseResponse(response, ClientPhase.Owned);
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
                RequestTimeout,
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
                    RequestTimeout,
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
                RequestTimeout,
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
                    RequestTimeout,
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
                    RequestTimeout,
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
                ReleaseTimeout,
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
            FanControlWatchdogLeaseContract.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeout.CancelAfter(ConnectTimeout);

            await pipe.ConnectAsync(timeout.Token)
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
                RequestTimeout,
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

    private async ValueTask<WireResponse> SendLockedAsync(
        WireRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await EnsureConnectedLockedAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            return await SendConnectedLockedAsync(
                request,
                timeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The durable service journal, not this process-local state, is the
            // authority after a transport break. Preserve session/generation
            // locally for a possible restore retry, but force the next request
            // to establish a fresh kernel-validated pipe connection.
            DisposePipeLocked();
            throw;
        }
    }

    private async ValueTask<WireResponse> SendConnectedLockedAsync(
        WireRequest request,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var pipe = _pipe ??
            throw new InvalidOperationException(
                "Watchdog pipe is not connected.");

        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);

        timeout.CancelAfter(requestTimeout);

        await WireCodec.WriteRequestAsync(
            pipe,
            request,
            timeout.Token).ConfigureAwait(false);

        var response =
            await WireCodec.ReadResponseAsync(
                pipe,
                timeout.Token).ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "Watchdog closed the pipe before responding.");

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

    private WireRequest NewLeaseRequest(
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

    private static WireRequest NewRequest(
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
        WireResponse response,
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
        WireResponse response,
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

    private sealed record WireRequest(
        int ProtocolVersion,
        Guid RequestId,
        string Type,
        int? ControllerPid = null,
        long? ControllerStartUtcTicks = null,
        Guid? SessionId = null,
        long? Generation = null,
        int? CpuLevel = null,
        int? GpuLevel = null);

    private sealed record WireResponse(
        int ProtocolVersion,
        Guid RequestId,
        bool Ok,
        string Code,
        string Message,
        Guid? SessionId = null,
        long? Generation = null,
        string? Phase = null);

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

    private static class WireCodec
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static ValueTask WriteRequestAsync(
            Stream stream,
            WireRequest request,
            CancellationToken cancellationToken) =>
            WriteFrameAsync(
                stream,
                request,
                cancellationToken);

        public static ValueTask<WireResponse?> ReadResponseAsync(
            Stream stream,
            CancellationToken cancellationToken) =>
            ReadFrameAsync<WireResponse>(
                stream,
                cancellationToken);

        private static async ValueTask WriteFrameAsync<T>(
            Stream stream,
            T value,
            CancellationToken cancellationToken)
        {
            var payload =
                JsonSerializer.SerializeToUtf8Bytes(
                    value,
                    JsonOptions);

            if (payload.Length is <= 0 or
                > FanControlWatchdogLeaseContract.MaximumFrameBytes)
            {
                throw new InvalidDataException(
                    $"Watchdog frame size {payload.Length} is invalid.");
            }

            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(
                header,
                payload.Length);

            await stream.WriteAsync(
                header,
                cancellationToken).ConfigureAwait(false);

            await stream.WriteAsync(
                payload,
                cancellationToken).ConfigureAwait(false);

            await stream.FlushAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private static async ValueTask<T?> ReadFrameAsync<T>(
            Stream stream,
            CancellationToken cancellationToken)
        {
            var header = new byte[4];

            if (!await ReadExactOrEofAsync(
                    stream,
                    header,
                    cancellationToken).ConfigureAwait(false))
            {
                return default;
            }

            var length =
                BinaryPrimitives.ReadInt32LittleEndian(
                    header);

            if (length is <= 0 or
                > FanControlWatchdogLeaseContract.MaximumFrameBytes)
            {
                throw new InvalidDataException(
                    $"Watchdog frame length {length} is invalid.");
            }

            var payload = new byte[length];
            await ReadExactAsync(
                stream,
                payload,
                cancellationToken).ConfigureAwait(false);

            return JsonSerializer.Deserialize<T>(
                payload,
                JsonOptions);
        }

        private static async ValueTask<bool> ReadExactOrEofAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var readTotal = 0;

            while (readTotal < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer[readTotal..],
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    if (readTotal == 0)
                    {
                        return false;
                    }

                    throw new EndOfStreamException(
                        "Watchdog pipe ended during a frame header.");
                }

                readTotal += read;
            }

            return true;
        }

        private static async ValueTask ReadExactAsync(
            Stream stream,
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var readTotal = 0;

            while (readTotal < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer[readTotal..],
                    cancellationToken).ConfigureAwait(false);

                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Watchdog pipe ended during a frame payload.");
                }

                readTotal += read;
            }
        }
    }
}
