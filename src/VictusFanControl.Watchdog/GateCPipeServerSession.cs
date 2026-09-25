using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VictusFanControl.Watchdog;

internal static class GateCPipeServerSession
{
    public static async Task RunAsync(
        NamedPipeServerStream pipe,
        WatchdogLeaseManager manager,
        CancellationToken cancellationToken)
    {
        ControllerIdentity? verifiedController = null;

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            var actual =
                WindowsNamedPipeIdentity.GetClientIdentity(pipe);

            var hello =
                await GateCProtocolCodec.ReadRequestAsync(
                    pipe,
                    cancellationToken).ConfigureAwait(false);

            if (hello is null)
            {
                return;
            }

            if (hello.ProtocolVersion != GateCProtocol.Version ||
                !string.Equals(
                    hello.Type,
                    GateCProtocol.Hello,
                    StringComparison.Ordinal) ||
                hello.ControllerPid != actual.ProcessId ||
                hello.ControllerStartUtcTicks !=
                    actual.ProcessStartUtcTicks)
            {
                await GateCProtocolCodec.WriteResponseAsync(
                    pipe,
                    Error(
                        hello,
                        "IDENTITY_MISMATCH",
                        "Hello identity/version does not match the kernel-observed named-pipe client."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            verifiedController = actual;

            await GateCProtocolCodec.WriteResponseAsync(
                pipe,
                Success(
                    hello,
                    "HELLO_OK",
                    "Named-pipe client identity verified."),
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                GateCRequest? request;

                try
                {
                    request =
                        await GateCProtocolCodec.ReadRequestAsync(
                            pipe,
                            cancellationToken).ConfigureAwait(false);
                }
                catch (LeaseProtocolException)
                {
                    // Framing/JSON errors have no trustworthy request id.
                    // Drop the connection; owner-loss handling below remains
                    // the fail-safe path if a lease already existed.
                    break;
                }

                if (request is null)
                {
                    break;
                }

                var response =
                    await DispatchAsync(
                        request,
                        actual,
                        manager,
                        cancellationToken).ConfigureAwait(false);

                await GateCProtocolCodec.WriteResponseAsync(
                    pipe,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // A client can disappear after the service has durably accepted a
            // state transition but before it receives the response. Treat any
            // broken-pipe transport failure as owner loss; the finally block
            // performs recovery from the durable lease rather than faulting the
            // server session.
        }
        catch (ObjectDisposedException)
        {
            // Equivalent local transport loss. The durable lease remains the
            // authority for deciding whether a restore is required.
        }
        finally
        {
            if (verifiedController is not null)
            {
                await manager.HandleOwnerLossAsync(
                    verifiedController,
                    "named-pipe disconnect",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async ValueTask<GateCResponse> DispatchAsync(
        GateCRequest request,
        ControllerIdentity controller,
        WatchdogLeaseManager manager,
        CancellationToken cancellationToken)
    {
        if (request.ProtocolVersion != GateCProtocol.Version)
        {
            return Error(
                request,
                "PROTOCOL_VERSION",
                $"Expected protocol version {GateCProtocol.Version}.");
        }

        try
        {
            switch (request.Type)
            {
                case GateCProtocol.Prepare:
                {
                    var result =
                        await manager.PrepareAsync(
                            controller,
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Lease prepared.",
                        result);
                }

                case GateCProtocol.CancelPrepared:
                    await manager.CancelPreparedAsync(
                        RequiredSession(request),
                        RequiredGeneration(request),
                        cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Prepared lease cancelled without hardware write.");

                case GateCProtocol.WriteIntent:
                {
                    var result =
                        await manager.WriteIntentAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            RequiredSetpoint(request),
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Write intent durably armed.",
                        result);
                }

                case GateCProtocol.AbortWriteIntent:
                {
                    var result =
                        await manager.AbortWriteIntentAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Write intent safely rolled back before hardware dispatch.",
                        result);
                }

                case GateCProtocol.Commit:
                {
                    var result =
                        await manager.CommitAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            RequiredSetpoint(request),
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Owned target committed.",
                        result);
                }

                case GateCProtocol.Heartbeat:
                {
                    var result =
                        await manager.HeartbeatAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Heartbeat accepted.",
                        result);
                }

                case GateCProtocol.RestoreBegin:
                {
                    var result =
                        await manager.RestoreBeginAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Restore takeover remains armed.",
                        result);
                }

                case GateCProtocol.Release:
                    await manager.ReleaseAsync(
                        RequiredSession(request),
                        RequiredGeneration(request),
                        cancellationToken).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Lease released after FF/FF verification.");

                default:
                    return Error(
                        request,
                        "UNKNOWN_MESSAGE",
                        $"Unknown protocol message '{request.Type}'.");
            }
        }
        catch (LeaseProtocolException ex)
        {
            return Error(
                request,
                ex.Code,
                ex.Message);
        }
        catch (Exception ex)
        {
            return Error(
                request,
                "INTERNAL_ERROR",
                ex.Message);
        }
    }

    private static Guid RequiredSession(
        GateCRequest request) =>
        request.SessionId ??
        throw new LeaseProtocolException(
            "MISSING_FIELD",
            "sessionId is required.");

    private static long RequiredGeneration(
        GateCRequest request) =>
        request.Generation ??
        throw new LeaseProtocolException(
            "MISSING_FIELD",
            "generation is required.");

    private static FanSetpoint RequiredSetpoint(
        GateCRequest request)
    {
        if (!request.CpuLevel.HasValue ||
            !request.GpuLevel.HasValue ||
            request.CpuLevel.Value is < 0 or > 255 ||
            request.GpuLevel.Value is < 0 or > 255)
        {
            throw new LeaseProtocolException(
                "MISSING_FIELD",
                "Valid cpuLevel and gpuLevel are required.");
        }

        return new FanSetpoint(
            (byte)request.CpuLevel.Value,
            (byte)request.GpuLevel.Value);
    }

    private static GateCResponse Success(
        GateCRequest request,
        string code,
        string message,
        LeaseOperationResult? result = null) =>
        new(
            GateCProtocol.Version,
            request.RequestId,
            Ok: true,
            code,
            message,
            result?.SessionId,
            result?.Generation,
            result?.Phase.ToString());

    private static GateCResponse Error(
        GateCRequest request,
        string code,
        string message) =>
        new(
            GateCProtocol.Version,
            request.RequestId,
            Ok: false,
            code,
            message);
}

internal static class WindowsNamedPipeIdentity
{
    public static ControllerIdentity GetClientIdentity(
        NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Named-pipe client PID validation requires Windows.");
        }

        if (!GetNamedPipeClientProcessId(
                pipe.SafePipeHandle,
                out var pid))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "GetNamedPipeClientProcessId failed.");
        }

        using var process =
            Process.GetProcessById(
                checked((int)pid));

        return new ControllerIdentity(
            checked((int)pid),
            process.StartTime.ToUniversalTime().Ticks);
    }

    public static ControllerIdentity CurrentProcessIdentity()
    {
        using var process = Process.GetCurrentProcess();

        return new ControllerIdentity(
            process.Id,
            process.StartTime.ToUniversalTime().Ticks);
    }

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(
        SafePipeHandle pipe,
        out uint clientProcessId);
}
