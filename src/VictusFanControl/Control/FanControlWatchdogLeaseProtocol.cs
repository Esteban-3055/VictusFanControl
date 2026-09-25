using System.Buffers.Binary;
using System.Text.Json;

namespace VictusFanControl.Control;

public sealed record FanControlWatchdogLeaseRequest(
    int ProtocolVersion,
    Guid RequestId,
    string Type,
    int? ControllerPid = null,
    long? ControllerStartUtcTicks = null,
    Guid? SessionId = null,
    long? Generation = null,
    int? CpuLevel = null,
    int? GpuLevel = null);

public sealed record FanControlWatchdogLeaseResponse(
    int ProtocolVersion,
    Guid RequestId,
    bool Ok,
    string Code,
    string Message,
    Guid? SessionId = null,
    long? Generation = null,
    string? Phase = null);

public sealed class FanControlWatchdogProtocolException :
    InvalidDataException
{
    public FanControlWatchdogProtocolException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Exact bounded length-prefixed JSON wire codec shared by the elevated
/// controller and LocalSystem watchdog. Sharing this implementation prevents
/// client/server DTO drift at the privileged IPC boundary.
/// </summary>
public static class FanControlWatchdogLeaseCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static ValueTask WriteRequestAsync(
        Stream stream,
        FanControlWatchdogLeaseRequest request,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            request,
            cancellationToken);

    public static ValueTask WriteResponseAsync(
        Stream stream,
        FanControlWatchdogLeaseResponse response,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            response,
            cancellationToken);

    public static ValueTask<FanControlWatchdogLeaseRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<FanControlWatchdogLeaseRequest>(
            stream,
            cancellationToken);

    public static ValueTask<FanControlWatchdogLeaseResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<FanControlWatchdogLeaseResponse>(
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
            throw new FanControlWatchdogProtocolException(
                "FRAME_SIZE",
                $"Watchdog protocol frame size {payload.Length} is invalid.");
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
            throw new FanControlWatchdogProtocolException(
                "FRAME_SIZE",
                $"Watchdog protocol frame length {length} is invalid.");
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
                throw new FanControlWatchdogProtocolException(
                    "MALFORMED_MESSAGE",
                    "Watchdog protocol frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new FanControlWatchdogProtocolException(
                "MALFORMED_MESSAGE",
                $"Malformed watchdog JSON message: {ex.Message}");
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
            var read =
                await stream.ReadAsync(
                    buffer[readTotal..],
                    cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                if (readTotal == 0)
                {
                    return false;
                }

                throw new FanControlWatchdogProtocolException(
                    "TRUNCATED_FRAME",
                    "Watchdog protocol stream ended in the middle of a frame header.");
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
            var read =
                await stream.ReadAsync(
                    buffer[readTotal..],
                    cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new FanControlWatchdogProtocolException(
                    "TRUNCATED_FRAME",
                    "Watchdog protocol stream ended in the middle of a frame payload.");
            }

            readTotal += read;
        }
    }
}
