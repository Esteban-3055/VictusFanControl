using VictusFanControl.Control;
using System.Buffers.Binary;
using System.Text.Json;

namespace VictusFanControl.Watchdog;

internal static class GateCProtocol
{
    public const int Version =
        FanControlWatchdogLeaseContract.ProtocolVersion;

    public const int MaximumFrameBytes =
        FanControlWatchdogLeaseContract.MaximumFrameBytes;

    public const string Hello =
        FanControlWatchdogLeaseContract.Hello;

    public const string Prepare =
        FanControlWatchdogLeaseContract.Prepare;

    public const string CancelPrepared =
        FanControlWatchdogLeaseContract.CancelPrepared;

    public const string WriteIntent =
        FanControlWatchdogLeaseContract.WriteIntent;

    public const string AbortWriteIntent =
        FanControlWatchdogLeaseContract.AbortWriteIntent;

    public const string Commit =
        FanControlWatchdogLeaseContract.Commit;

    public const string Heartbeat =
        FanControlWatchdogLeaseContract.Heartbeat;

    public const string RestoreBegin =
        FanControlWatchdogLeaseContract.RestoreBegin;

    public const string Release =
        FanControlWatchdogLeaseContract.Release;
}

internal sealed record GateCRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Type,
    int? ControllerPid = null,
    long? ControllerStartUtcTicks = null,
    Guid? SessionId = null,
    long? Generation = null,
    int? CpuLevel = null,
    int? GpuLevel = null);

internal sealed record GateCResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Ok,
    string Code,
    string Message,
    Guid? SessionId = null,
    long? Generation = null,
    string? Phase = null);

internal static class GateCProtocolCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ValueTask WriteRequestAsync(
        Stream stream,
        GateCRequest request,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, request, cancellationToken);

    public static ValueTask WriteResponseAsync(
        Stream stream,
        GateCResponse response,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(stream, response, cancellationToken);

    public static ValueTask<GateCRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<GateCRequest>(stream, cancellationToken);

    public static ValueTask<GateCResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<GateCResponse>(stream, cancellationToken);

    private static async ValueTask WriteFrameAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);

        if (payload.Length is <= 0 or > GateCProtocol.MaximumFrameBytes)
        {
            throw new LeaseProtocolException(
                "FRAME_SIZE",
                $"Protocol frame size {payload.Length} is invalid.");
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
        var hasHeader =
            await ReadExactOrEofAsync(
                stream,
                header,
                cancellationToken).ConfigureAwait(false);

        if (!hasHeader)
        {
            return default;
        }

        var length =
            BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length is <= 0 or > GateCProtocol.MaximumFrameBytes)
        {
            throw new LeaseProtocolException(
                "FRAME_SIZE",
                $"Protocol frame length {length} is invalid.");
        }

        var payload = new byte[length];

        try
        {
            await ReadExactAsync(
                stream,
                payload,
                cancellationToken).ConfigureAwait(false);

            var value =
                JsonSerializer.Deserialize<T>(
                    payload,
                    JsonOptions);

            return value ??
                throw new LeaseProtocolException(
                    "MALFORMED_MESSAGE",
                    "Protocol frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new LeaseProtocolException(
                "MALFORMED_MESSAGE",
                $"Malformed JSON protocol message: {ex.Message}");
        }
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

                throw new LeaseProtocolException(
                    "TRUNCATED_FRAME",
                    "Protocol stream ended in the middle of a frame header.");
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
                throw new LeaseProtocolException(
                    "TRUNCATED_FRAME",
                    "Protocol stream ended in the middle of a frame payload.");
            }

            readTotal += read;
        }
    }
}
