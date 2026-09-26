using VictusFanControl.Control;
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
        CancellationToken cancellationToken,
        Action<string>? log = null,
        bool monitorControllerProcess = false,
        Func<FanControlWatchdogLeaseRequest, FanControlWatchdogLeaseResponse, CancellationToken, ValueTask>? beforeResponseAsync = null)
    {
        ControllerIdentity? verifiedController = null;
        Process? ownerProcess = null;
        CancellationTokenSource? ownerWaitCts = null;
        Task? ownerExitTask = null;
        var ownerLossReason = "named-pipe disconnect";

        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            var actual =
                WindowsNamedPipeIdentity.GetClientIdentity(pipe);

            var hello =
                await FanControlWatchdogLeaseCodec.ReadRequestAsync(
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
                await FanControlWatchdogLeaseCodec.WriteResponseAsync(
                    pipe,
                    Error(
                        hello,
                        "IDENTITY_MISMATCH",
                        "Hello identity/version does not match the kernel-observed named-pipe client."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            verifiedController = actual;

            if (monitorControllerProcess)
            {
                ownerProcess =
                    Process.GetProcessById(actual.ProcessId);

                var reopenedStart =
                    ownerProcess.StartTime.ToUniversalTime().Ticks;

                if (reopenedStart != actual.ProcessStartUtcTicks)
                {
                    throw new InvalidOperationException(
                        "Named-pipe client process identity changed before the lease monitor could open its process handle.");
                }
            }

            log?.Invoke(
                $"WATCHDOG PIPE: verified local controller PID={actual.ProcessId}, startTicks={actual.ProcessStartUtcTicks}, processMonitor={monitorControllerProcess}.");

            await FanControlWatchdogLeaseCodec.WriteResponseAsync(
                pipe,
                Success(
                    hello,
                    "HELLO_OK",
                    "Named-pipe client identity verified."),
                cancellationToken).ConfigureAwait(false);

            if (ownerProcess is not null)
            {
                ownerWaitCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                ownerExitTask =
                    ownerProcess.WaitForExitAsync(
                        ownerWaitCts.Token);
            }

            while (true)
            {
                FanControlWatchdogLeaseRequest? request;

                using var readCts =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);

                var readTask =
                    FanControlWatchdogLeaseCodec.ReadRequestAsync(
                        pipe,
                        readCts.Token).AsTask();

                if (ownerExitTask is not null)
                {
                    var completed =
                        await Task.WhenAny(
                            readTask,
                            ownerExitTask).ConfigureAwait(false);

                    if (ReferenceEquals(completed, ownerExitTask))
                    {
                        ownerLossReason =
                            ownerProcess!.HasExited
                                ? "controller process exited"
                                : "watchdog service cancellation";

                        readCts.Cancel();

                        try
                        {
                            await readTask.ConfigureAwait(false);
                        }
                        catch
                        {
                            // The read was cancelled solely to stop waiting on a
                            // dead/terminating owner. Durable lease recovery below
                            // is authoritative.
                        }

                        break;
                    }
                }

                try
                {
                    request = await readTask.ConfigureAwait(false);
                }
                catch (FanControlWatchdogProtocolException)
                {
                    ownerLossReason = "malformed named-pipe frame";
                    break;
                }

                if (request is null)
                {
                    ownerLossReason = "named-pipe EOF";
                    break;
                }

                var response =
                    await DispatchAsync(
                        request,
                        actual,
                        manager,
                        cancellationToken).ConfigureAwait(false);

                if (!response.Ok)
                {
                    log?.Invoke(
                        $"WATCHDOG REQUEST REJECTED type={request.Type}; code={response.Code}; message={response.Message}");
                }
                else if (string.Equals(
                             request.Type,
                             GateCProtocol.Release,
                             StringComparison.Ordinal))
                {
                    log?.Invoke(
                        $"WATCHDOG RELEASE ACK controller PID={actual.ProcessId}; durable lease cleared after verified firmware restore.");
                }

                if (beforeResponseAsync is not null)
                {
                    await beforeResponseAsync(
                        request,
                        response,
                        cancellationToken).ConfigureAwait(false);
                }

                await FanControlWatchdogLeaseCodec.WriteResponseAsync(
                    pipe,
                    response,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            ownerLossReason = "broken named pipe";
            log?.Invoke(
                $"WATCHDOG PIPE: transport loss: {ex.Message}");

            // A client can disappear after the service has durably accepted a
            // state transition but before it receives the response. The finally
            // block distinguishes proven process death from transport-only loss:
            // Gate D retains the durable lease while the exact controller
            // process is still alive, otherwise it performs owner-loss recovery.
        }
        catch (ObjectDisposedException)
        {
            ownerLossReason = "named-pipe disposed";
            // Equivalent local transport loss. The durable lease plus verified
            // controller liveness decide whether to retain for reconnect or
            // perform immediate owner-loss recovery.
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            ownerLossReason = "watchdog service cancellation";
        }
        finally
        {
            try
            {
                if (verifiedController is not null)
                {
                    var retainForLiveController = false;

                    if (monitorControllerProcess &&
                        ownerProcess is not null &&
                        !cancellationToken.IsCancellationRequested &&
                        !string.Equals(
                            ownerLossReason,
                            "controller process exited",
                            StringComparison.Ordinal))
                    {
                        try
                        {
                            retainForLiveController = !ownerProcess.HasExited;
                        }
                        catch
                        {
                            // If liveness cannot be proven, fall back to the
                            // fail-closed owner-loss recovery below.
                            retainForLiveController = false;
                        }
                    }

                    if (retainForLiveController)
                    {
                        // Critical Gate D ordering rule: a pipe can disappear
                        // after WRITE_INTENT was acknowledged but before the
                        // controller dispatches WMI. Restoring immediately here
                        // would clear the lease while the still-live controller
                        // could continue into SetFanLevel. Keep durable
                        // ownership armed instead. The persistent Gate D
                        // process monitor and state deadlines remain
                        // authoritative while the accept loop permits a fresh
                        // kernel-validated reconnect from the same process.
                        log?.Invoke(
                            $"WATCHDOG TRANSPORT LOSS: reason={ownerLossReason}; " +
                            $"controller PID={verifiedController.ProcessId} is still alive; " +
                            "durable lease retained for reconnect/process-death/deadline recovery.");
                    }
                    else
                    {
                        var recovery =
                            await manager.HandleOwnerLossAsync(
                                verifiedController,
                                ownerLossReason,
                                CancellationToken.None).ConfigureAwait(false);

                        if (recovery is not null)
                        {
                            log?.Invoke(
                                $"WATCHDOG OWNER LOSS: reason={ownerLossReason}; " +
                                $"disposition={recovery.Disposition}; observed={recovery.Observed}; " +
                                $"restoreAttempted={recovery.RestoreAttempted}; journalRetained={recovery.JournalRetained}; detail={recovery.Detail}");
                        }
                        else
                        {
                            log?.Invoke(
                                $"WATCHDOG OWNER LOSS: reason={ownerLossReason}; no active lease required recovery.");
                        }
                    }
                }
            }
            finally
            {
                if (ownerWaitCts is not null)
                {
                    ownerWaitCts.Cancel();

                    if (ownerExitTask is not null &&
                        !ownerExitTask.IsCompleted)
                    {
                        try
                        {
                            await ownerExitTask.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    ownerWaitCts.Dispose();
                }

                ownerProcess?.Dispose();
            }
        }
    }

    private static async ValueTask<FanControlWatchdogLeaseResponse> DispatchAsync(
        FanControlWatchdogLeaseRequest request,
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
                        cancellationToken,
                        controller).ConfigureAwait(false);
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
                            cancellationToken,
                            controller).ConfigureAwait(false);
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
                            cancellationToken,
                            controller).ConfigureAwait(false);
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
                            cancellationToken,
                            controller).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Owned target committed.",
                        result);
                }

                case GateCProtocol.Probe:
                {
                    var result =
                        await manager.ProbeAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            cancellationToken,
                            controller).ConfigureAwait(false);
                    return Success(
                        request,
                        "OK",
                        "Lease probe accepted without renewing heartbeat.",
                        result);
                }

                case GateCProtocol.Heartbeat:
                {
                    var result =
                        await manager.HeartbeatAsync(
                            RequiredSession(request),
                            RequiredGeneration(request),
                            cancellationToken,
                            controller).ConfigureAwait(false);
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
                            cancellationToken,
                            controller).ConfigureAwait(false);
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
                        cancellationToken,
                        controller).ConfigureAwait(false);
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
        FanControlWatchdogLeaseRequest request) =>
        request.SessionId ??
        throw new LeaseProtocolException(
            "MISSING_FIELD",
            "sessionId is required.");

    private static long RequiredGeneration(
        FanControlWatchdogLeaseRequest request) =>
        request.Generation ??
        throw new LeaseProtocolException(
            "MISSING_FIELD",
            "generation is required.");

    private static FanSetpoint RequiredSetpoint(
        FanControlWatchdogLeaseRequest request)
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

    private static FanControlWatchdogLeaseResponse Success(
        FanControlWatchdogLeaseRequest request,
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

    private static FanControlWatchdogLeaseResponse Error(
        FanControlWatchdogLeaseRequest request,
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
