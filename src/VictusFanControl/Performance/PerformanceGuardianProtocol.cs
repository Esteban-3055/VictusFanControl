using System.Buffers.Binary;
using System.Text.Json;

namespace VictusFanControl.Performance;

internal static class PerformanceGuardianProtocol
{
    internal const int Version = 1;
    internal const int MaximumFrameBytes = 32 * 1024;

    internal const string Hello = "HELLO";
    internal const string EnableSession = "ENABLE_SESSION";
    internal const string DisableSession = "DISABLE_SESSION";
    internal const string Status = "STATUS";
    internal const string Shutdown = "SHUTDOWN";
}

internal sealed record PerformanceGuardianRequest(
    int ProtocolVersion,
    Guid RequestId,
    string TargetProfileId,
    Guid SessionNonce,
    string Type,
    int? OwnerPid = null,
    long? OwnerStartUtcTicks = null,
    bool? CpuEnabled = null,
    bool? GpuEnabled = null);

internal sealed record PerformanceGuardianResponse(
    int ProtocolVersion,
    Guid RequestId,
    string TargetProfileId,
    bool Ok,
    string Code,
    string Message,
    string Phase,
    bool SessionEnabled,
    bool CpuEnabled,
    bool GpuEnabled,
    string? CpuState = null,
    string? GpuState = null,
    string? CpuStatus = null,
    string? GpuStatus = null,
    string? PowerSource = null,
    string? RuntimeFailure = null);

internal sealed class PerformanceGuardianProtocolException :
    IOException
{
    internal PerformanceGuardianProtocolException(
        string code,
        string message)
        : base(message)
    {
        Code = code;
    }

    internal string Code { get; }
}

/// <summary>
/// Bounded length-prefixed JSON protocol. The wire surface is semantic only:
/// it contains session enable flags but no raw MSR values, NVML clock ranges,
/// WMI method ids, EC offsets or arbitrary hardware commands.
/// </summary>
internal static class PerformanceGuardianCodec
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase
        };

    internal static ValueTask WriteRequestAsync(
        Stream stream,
        PerformanceGuardianRequest request,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            request,
            cancellationToken);

    internal static ValueTask WriteResponseAsync(
        Stream stream,
        PerformanceGuardianResponse response,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            response,
            cancellationToken);

    internal static ValueTask<PerformanceGuardianRequest?> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<PerformanceGuardianRequest>(
            stream,
            cancellationToken);

    internal static ValueTask<PerformanceGuardianResponse?> ReadResponseAsync(
        Stream stream,
        CancellationToken cancellationToken) =>
        ReadFrameAsync<PerformanceGuardianResponse>(
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
            > PerformanceGuardianProtocol.MaximumFrameBytes)
        {
            throw new PerformanceGuardianProtocolException(
                "FRAME_SIZE",
                $"Performance Guardian frame size {payload.Length} is invalid.");
        }

        var header =
            new byte[4];

        BinaryPrimitives.WriteInt32LittleEndian(
            header,
            payload.Length);

        await stream.WriteAsync(
            header,
            cancellationToken).ConfigureAwait(false);

        await stream.WriteAsync(
            payload,
            cancellationToken).ConfigureAwait(false);

        await stream.FlushAsync(
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<T?> ReadFrameAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var header =
            new byte[4];

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
            > PerformanceGuardianProtocol.MaximumFrameBytes)
        {
            throw new PerformanceGuardianProtocolException(
                "FRAME_SIZE",
                $"Performance Guardian frame length {length} is invalid.");
        }

        var payload =
            new byte[length];

        await ReadExactAsync(
            stream,
            payload,
            cancellationToken).ConfigureAwait(false);

        try
        {
            return
                JsonSerializer.Deserialize<T>(
                    payload,
                    JsonOptions) ??
                throw new PerformanceGuardianProtocolException(
                    "MALFORMED_MESSAGE",
                    "Performance Guardian frame deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new PerformanceGuardianProtocolException(
                "MALFORMED_MESSAGE",
                "Malformed Performance Guardian JSON: " +
                ex.Message);
        }
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total =
            0;

        while (total <
            buffer.Length)
        {
            var read =
                await stream.ReadAsync(
                    buffer[total..],
                    cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                if (total == 0)
                    return false;

                throw new PerformanceGuardianProtocolException(
                    "TRUNCATED_FRAME",
                    "Performance Guardian stream ended inside a frame header.");
            }

            total += read;
        }

        return true;
    }

    private static async ValueTask ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total =
            0;

        while (total <
            buffer.Length)
        {
            var read =
                await stream.ReadAsync(
                    buffer[total..],
                    cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                throw new PerformanceGuardianProtocolException(
                    "TRUNCATED_FRAME",
                    "Performance Guardian stream ended inside a frame payload.");
            }

            total += read;
        }
    }
}
