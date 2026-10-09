using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal readonly record struct GuardianHostOptions(
    string TargetProfileId,
    string PipeName,
    string MutexName,
    Guid SessionNonce,
    int OwnerPid,
    long OwnerStartUtcTicks,
    string ReportPath,
    string? ReadyPath);

internal readonly record struct GuardianDomainLifecycleSnapshot(
    int EnableCalls,
    int ReleaseCalls,
    bool LastCpuEnabled,
    bool LastGpuEnabled,
    PerformancePowerSourceKind? LastInitialSource,
    string? LastReleaseReason,
    int CpuHardwareWriteAttempts,
    int GpuHardwareWriteAttempts,
    string? CpuState,
    string? GpuState,
    string? CpuStatus,
    string? GpuStatus);

internal interface IGuardianDomainLifecycle
{
    GuardianDomainLifecycleSnapshot Snapshot { get; }

    ValueTask EnableAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        PerformancePowerSourceKind initialSource,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        string reason,
        CancellationToken cancellationToken);
}

internal sealed class RecordingGuardianDomainLifecycle :
    IGuardianDomainLifecycle
{
    internal int EnableCalls { get; private set; }
    internal int ReleaseCalls { get; private set; }
    internal bool LastCpuEnabled { get; private set; }
    internal bool LastGpuEnabled { get; private set; }
    internal string? LastReleaseReason { get; private set; }
    internal PerformancePowerSourceKind? LastInitialSource { get; private set; }

    public GuardianDomainLifecycleSnapshot Snapshot =>
        new(
            EnableCalls,
            ReleaseCalls,
            LastCpuEnabled,
            LastGpuEnabled,
            LastInitialSource,
            LastReleaseReason,
            CpuHardwareWriteAttempts: 0,
            GpuHardwareWriteAttempts: 0,
            CpuState: null,
            GpuState: null,
            CpuStatus: null,
            GpuStatus: null);

    public ValueTask EnableAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        PerformancePowerSourceKind initialSource,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        EnableCalls++;
        LastCpuEnabled =
            cpuEnabled;
        LastGpuEnabled =
            gpuEnabled;
        LastInitialSource =
            initialSource;

        return ValueTask.CompletedTask;
    }

    public ValueTask ReleaseAsync(
        bool cpuEnabled,
        bool gpuEnabled,
        string reason,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ReleaseCalls++;
        LastCpuEnabled =
            cpuEnabled;
        LastGpuEnabled =
            gpuEnabled;
        LastReleaseReason =
            reason;

        return ValueTask.CompletedTask;
    }
}

internal sealed class PerformanceGuardianHost
{
    internal const int ExitSuccess = 0;
    internal const int ExitDuplicateGuardian = 20;
    internal const int ExitOwnerIdentityInvalid = 21;
    internal const int ExitRuntimeFailure = 22;

    private readonly GuardianHostOptions _options;
    private readonly IGuardianDomainLifecycle _domains;
    private readonly IGuardianPowerSourceRuntime _sourceRuntime;

    private int _acceptedRequests;
    private int _rejectedRequests;
    private int _connections;
    private bool _parentLost;
    private string _exitReason =
        "NOT_STARTED";

    internal PerformanceGuardianHost(
        GuardianHostOptions options,
        IGuardianDomainLifecycle domains,
        IGuardianPowerSourceRuntime sourceRuntime)
    {
        _options =
            options;

        _domains =
            domains ??
            throw new ArgumentNullException(
                nameof(domains));

        _sourceRuntime =
            sourceRuntime ??
            throw new ArgumentNullException(
                nameof(sourceRuntime));
    }

    internal async Task<int> RunAsync(
        CancellationToken cancellationToken)
    {
        Mutex? mutex =
            null;

        Process? owner =
            null;

        PerformanceGuardianAuthority? authority =
            null;

        Exception? failure =
            null;

        try
        {
            mutex =
                new Mutex(
                    initiallyOwned: true,
                    _options.MutexName,
                    out var createdNew);

            if (!createdNew)
            {
                _exitReason =
                    "DUPLICATE_GUARDIAN";

                return ExitDuplicateGuardian;
            }

            try
            {
                owner =
                    Process.GetProcessById(
                        _options.OwnerPid);

                if (owner.HasExited ||
                    owner.StartTime
                        .ToUniversalTime()
                        .Ticks !=
                    _options.OwnerStartUtcTicks)
                {
                    _exitReason =
                        "OWNER_IDENTITY_INVALID";

                    return ExitOwnerIdentityInvalid;
                }
            }
            catch (ArgumentException)
            {
                _exitReason =
                    "OWNER_PROCESS_NOT_FOUND";

                return ExitOwnerIdentityInvalid;
            }

            authority =
                new PerformanceGuardianAuthority(
                    _options.TargetProfileId,
                    _options.OwnerPid,
                    _options.OwnerStartUtcTicks,
                    _options.SessionNonce);

            if (!string.IsNullOrWhiteSpace(
                    _options.ReadyPath))
            {
                DurableText(
                    _options.ReadyPath!,
                    "READY");
            }

            using var stop =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            var ownerExitTask =
                MonitorOwnerExitAsync(
                    owner,
                    stop);

            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var pipe =
                        new NamedPipeServerStream(
                            _options.PipeName,
                            PipeDirection.InOut,
                            maxNumberOfServerInstances: 1,
                            PipeTransmissionMode.Byte,
                            PipeOptions.Asynchronous |
                            PipeOptions.WriteThrough | PipeOptions.CurrentUserOnly);

                    try
                    {
                        await pipe.WaitForConnectionAsync(
                            stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                        when (stop.IsCancellationRequested)
                    {
                        break;
                    }

                    _connections++;

                    await ProcessConnectionAsync(
                        pipe,
                        authority,
                        stop).ConfigureAwait(false);
                }
            }
            finally
            {
                stop.Cancel();

                try
                {
                    await ownerExitTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                    when (!_parentLost)
                {
                }
            }

            if (_parentLost)
            {
                var cpu =
                    authority.CpuEnabled;

                var gpu =
                    authority.GpuEnabled;

                if (authority.SessionEnabled)
                {
                    var cleanup =
                        await AttemptSessionCleanupAsync(
                            _sourceRuntime,
                            _domains,
                            cpu,
                            gpu,
                            "PARENT_PROCESS_EXIT",
                            CancellationToken.None).ConfigureAwait(false);

                    failure =
                        cleanup.Failure;
                }

                _ =
                    authority.MarkParentLost();

                _exitReason =
                    failure is null
                        ? "PARENT_LOST_RELEASED"
                        : "PARENT_LOST_RELEASE_FAILED";

                return failure is null
                    ? ExitSuccess
                    : ExitRuntimeFailure;
            }

            // External cancellation is also an ownership boundary. Do not leave
            // an enabled hardware session behind merely because the IPC loop ended.
            if (authority.SessionEnabled)
            {
                var cleanup = await AttemptSessionCleanupAsync(_sourceRuntime, _domains,
                    authority.CpuEnabled, authority.GpuEnabled, "HOST_CANCELLED", CancellationToken.None).ConfigureAwait(false);
                if (!cleanup.Succeeded)
                {
                    failure = cleanup.Failure;
                    _exitReason = "CANCELLED_RELEASE_FAILED";
                    return ExitRuntimeFailure;
                }
                _ = authority.Handle(new PerformanceGuardianRequest(PerformanceGuardianProtocol.Version, Guid.NewGuid(),
                    _options.TargetProfileId, _options.SessionNonce, PerformanceGuardianProtocol.DisableSession));
            }
            _exitReason =
                authority.Phase ==
                    PerformanceGuardianAuthorityPhase.Stopped
                    ? "CLIENT_SHUTDOWN"
                    : "CANCELLED";

            return ExitSuccess;
        }
        catch (Exception ex)
        {
            failure =
                ex;

            _exitReason = "RUNTIME_FAILURE";
            if (authority?.SessionEnabled == true)
            {
                var cleanup = await AttemptSessionCleanupAsync(_sourceRuntime, _domains,
                    authority.CpuEnabled, authority.GpuEnabled, "HOST_RUNTIME_FAILURE", CancellationToken.None).ConfigureAwait(false);
                if (cleanup.Failure is not null) failure = new AggregateException(ex, cleanup.Failure);
            }
            return ExitRuntimeFailure;
        }
        finally
        {
            try
            {
                _sourceRuntime.Dispose();
            }
            catch (Exception ex)
            {
                failure =
                    failure is null
                        ? ex
                        : new AggregateException(
                            failure,
                            ex);
            }

            try
            {
                WriteReport(
                    authority,
                    failure);
            }
            catch
            {
            }

            owner?.Dispose();

            if (mutex is not null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                }

                mutex.Dispose();
            }
        }
    }

    internal readonly record struct SessionCleanupResult(
        bool SourceStopAttempted,
        bool ReleaseAttempted,
        Exception? Failure)
    {
        internal bool Succeeded =>
            Failure is null;
    }

    internal static async ValueTask<SessionCleanupResult>
        AttemptSessionCleanupAsync(
            IGuardianPowerSourceRuntime sourceRuntime,
            IGuardianDomainLifecycle domains,
            bool cpuEnabled,
            bool gpuEnabled,
            string reason,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(
            sourceRuntime);

        ArgumentNullException.ThrowIfNull(
            domains);

        Exception? failure =
            null;

        var sourceStopAttempted =
            false;

        var releaseAttempted =
            false;

        try
        {
            sourceStopAttempted =
                true;

            sourceRuntime.Stop(
                reason);
        }
        catch (Exception ex)
        {
            failure =
                ex;
        }

        try
        {
            releaseAttempted =
                true;

            await domains.ReleaseAsync(
                cpuEnabled,
                gpuEnabled,
                reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure =
                failure is null
                    ? ex
                    : new AggregateException(
                        failure,
                        ex);
        }

        return new SessionCleanupResult(
            sourceStopAttempted,
            releaseAttempted,
            failure);
    }

    private async Task ProcessConnectionAsync(
        NamedPipeServerStream pipe,
        PerformanceGuardianAuthority authority,
        CancellationTokenSource stop)
    {
        var connectionHello =
            false;

        while (pipe.IsConnected &&
               !stop.IsCancellationRequested)
        {
            PerformanceGuardianRequest? request;

            try
            {
                request =
                    await PerformanceGuardianCodec
                        .ReadRequestAsync(
                            pipe,
                            stop.Token)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            if (request is null)
                return;

            if (!connectionHello &&
                request.Type !=
                    PerformanceGuardianProtocol.Hello)
            {
                _rejectedRequests++;

                await SendResponseAsync(
                    pipe,
                    request,
                    new PerformanceGuardianAuthorityResult(
                        Accepted: false,
                        Code:
                            "CONNECTION_HELLO_REQUIRED",
                        Message:
                            "Each Performance Guardian transport connection requires HELLO.",
                        Phase:
                            authority.Phase,
                        SessionEnabled:
                            authority.SessionEnabled,
                        CpuEnabled:
                            authority.CpuEnabled,
                        GpuEnabled:
                            authority.GpuEnabled),
                    stop.Token).ConfigureAwait(false);

                continue;
            }

            var wasEnabled =
                authority.SessionEnabled;

            var previousCpu =
                authority.CpuEnabled;

            var previousGpu =
                authority.GpuEnabled;

            PerformanceGuardianAuthorityResult result;

            if (request.Type is
                    PerformanceGuardianProtocol.DisableSession or
                    PerformanceGuardianProtocol.Shutdown)
            {
                var envelope =
                    ValidateSideEffectEnvelope(
                        authority,
                        request);

                if (!envelope.Accepted)
                {
                    _rejectedRequests++;

                    await SendResponseAsync(
                        pipe,
                        request,
                        envelope,
                        stop.Token).ConfigureAwait(false);

                    continue;
                }
            }

            if (request.Type is
                    PerformanceGuardianProtocol.DisableSession or
                    PerformanceGuardianProtocol.Shutdown &&
                wasEnabled)
            {
                var cleanupReason =
                    request.Type ==
                        PerformanceGuardianProtocol.DisableSession
                        ? "CLIENT_DISABLE_SESSION"
                        : "CLIENT_SHUTDOWN";

                var cleanup =
                    await AttemptSessionCleanupAsync(
                        _sourceRuntime,
                        _domains,
                        previousCpu,
                        previousGpu,
                        cleanupReason,
                        stop.Token).ConfigureAwait(false);

                if (!cleanup.Succeeded)
                {
                    _rejectedRequests++;

                    await SendResponseAsync(
                        pipe,
                        request,
                        new PerformanceGuardianAuthorityResult(
                            Accepted: false,
                            Code:
                                cleanup.ReleaseAttempted
                                    ? "SESSION_CLEANUP_FAILED"
                                    : "SESSION_CLEANUP_RELEASE_NOT_ATTEMPTED",
                            Message:
                                cleanup.Failure?.Message ??
                                "Performance Guardian cleanup failed.",
                            Phase:
                                authority.Phase,
                            SessionEnabled:
                                authority.SessionEnabled,
                            CpuEnabled:
                                authority.CpuEnabled,
                            GpuEnabled:
                                authority.GpuEnabled),
                        stop.Token).ConfigureAwait(false);

                    continue;
                }
            }

            result =
                authority.Handle(
                    request);

            if (result.Accepted)
                _acceptedRequests++;
            else
                _rejectedRequests++;

            if (request.Type ==
                    PerformanceGuardianProtocol.Hello &&
                result.Accepted)
            {
                connectionHello =
                    true;
            }

            if (request.Type == PerformanceGuardianProtocol.UpdateConfiguration && result.Accepted)
            {
                try
                {
                    if (_domains is not IGuardianConfigurableDomains configured || _sourceRuntime is not IGuardianConfigurationRuntime runtime)
                        throw new InvalidOperationException("This Guardian does not support live configuration updates.");
                    var next = request.Configuration ?? throw new InvalidOperationException("Missing validated presets.");
                    next.Validate();
                    runtime.ExecuteConfigurationUpdate(source => configured.UpdateConfiguration(next, source));
                    result = result with { Code = "CONFIGURATION_UPDATED", Message = "Live presets updated in the original ownership session." };
                }
                catch (Exception ex)
                {
                    _acceptedRequests--;
                    _rejectedRequests++;
                    result = result with { Accepted = false, Code = "CONFIGURATION_UPDATE_FAILED", Message = ex.Message +
                        (ex is AggregateException aggregate ? " " + string.Join("; ", aggregate.InnerExceptions.Select(e => e.Message)) : "") };
                }
            }

            if (request.Type ==
                    PerformanceGuardianProtocol.EnableSession &&
                result.Accepted &&
                !wasEnabled &&
                result.SessionEnabled)
            {
                var domainsEnabled =
                    false;

                var sourcePrimed =
                    false;

                try
                {
                    var prime =
                        _sourceRuntime.Prime(
                            result.CpuEnabled,
                            result.GpuEnabled);

                    sourcePrimed =
                        true;

                    await _domains.EnableAsync(
                        result.CpuEnabled,
                        result.GpuEnabled,
                        prime.Observation.Source,
                        stop.Token).ConfigureAwait(false);

                    domainsEnabled =
                        true;

                    _sourceRuntime.ActivateListener();
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (sourcePrimed)
                        {
                            _sourceRuntime.Stop(
                                "ENABLE_SESSION_ROLLBACK");
                        }
                    }
                    catch
                    {
                    }

                    if (domainsEnabled)
                    {
                        try
                        {
                            await _domains.ReleaseAsync(
                                result.CpuEnabled,
                                result.GpuEnabled,
                                "ENABLE_SESSION_ROLLBACK",
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }

                    _ =
                        authority.Handle(
                            new PerformanceGuardianRequest(
                                PerformanceGuardianProtocol.Version,
                                Guid.NewGuid(),
                                _options.TargetProfileId,
                                _options.SessionNonce,
                                PerformanceGuardianProtocol.DisableSession));

                    result =
                        new PerformanceGuardianAuthorityResult(
                            Accepted: false,
                            Code:
                                domainsEnabled
                                    ? "SOURCE_RUNTIME_START_FAILED"
                                    : "DOMAIN_ENABLE_FAILED",
                            Message:
                                ex.Message,
                            Phase:
                                authority.Phase,
                            SessionEnabled:
                                authority.SessionEnabled,
                            CpuEnabled:
                                authority.CpuEnabled,
                            GpuEnabled:
                                authority.GpuEnabled);

                    _rejectedRequests++;
                }
            }

            await SendResponseAsync(
                pipe,
                request,
                result,
                stop.Token).ConfigureAwait(false);

            if (request.Type ==
                    PerformanceGuardianProtocol.Shutdown &&
                result.Accepted)
            {
                stop.Cancel();
                return;
            }
        }
    }

    private async Task MonitorOwnerExitAsync(
        Process owner,
        CancellationTokenSource stop)
    {
        try
        {
            await owner.WaitForExitAsync(
                stop.Token).ConfigureAwait(false);

            _parentLost =
                true;

            stop.Cancel();
        }
        catch (OperationCanceledException)
            when (stop.IsCancellationRequested)
        {
        }
    }

    private PerformanceGuardianAuthorityResult ValidateSideEffectEnvelope(
        PerformanceGuardianAuthority authority,
        PerformanceGuardianRequest request)
    {
        if (request.ProtocolVersion !=
            PerformanceGuardianProtocol.Version)
        {
            return RejectEnvelope(
                authority,
                "PROTOCOL_VERSION",
                "Performance Guardian protocol version mismatch.");
        }

        if (request.RequestId ==
            Guid.Empty)
        {
            return RejectEnvelope(
                authority,
                "REQUEST_ID",
                "Performance Guardian request id cannot be empty.");
        }

        if (!string.Equals(
                request.TargetProfileId,
                _options.TargetProfileId,
                StringComparison.Ordinal))
        {
            return RejectEnvelope(
                authority,
                "TARGET_MISMATCH",
                "Performance Guardian target profile mismatch.");
        }

        if (request.SessionNonce !=
            _options.SessionNonce)
        {
            return RejectEnvelope(
                authority,
                "AUTH_NONCE",
                "Performance Guardian session nonce mismatch.");
        }

        if (authority.Phase is
            PerformanceGuardianAuthorityPhase.ParentLost or
            PerformanceGuardianAuthorityPhase.Stopped)
        {
            return RejectEnvelope(
                authority,
                "GUARDIAN_STOPPING",
                "Performance Guardian authority is no longer active.");
        }

        return new PerformanceGuardianAuthorityResult(
            Accepted: true,
            Code:
                "ENVELOPE_OK",
            Message:
                "Performance Guardian side-effect envelope validated.",
            Phase:
                authority.Phase,
            SessionEnabled:
                authority.SessionEnabled,
            CpuEnabled:
                authority.CpuEnabled,
            GpuEnabled:
                authority.GpuEnabled);
    }

    private static PerformanceGuardianAuthorityResult RejectEnvelope(
        PerformanceGuardianAuthority authority,
        string code,
        string message) =>
        new(
            Accepted: false,
            code,
            message,
            authority.Phase,
            authority.SessionEnabled,
            authority.CpuEnabled,
            authority.GpuEnabled);

    private async ValueTask SendResponseAsync(
        Stream stream,
        PerformanceGuardianRequest request,
        PerformanceGuardianAuthorityResult result,
        CancellationToken cancellationToken)
    {
        var response =
            new PerformanceGuardianResponse(
                PerformanceGuardianProtocol.Version,
                request.RequestId,
                _options.TargetProfileId,
                result.Accepted,
                result.Code,
                result.Message,
                result.Phase.ToString(),
                result.SessionEnabled,
                result.CpuEnabled,
                result.GpuEnabled,
                _domains.Snapshot.CpuState,
                _domains.Snapshot.GpuState,
                _domains.Snapshot.CpuStatus,
                _domains.Snapshot.GpuStatus,
                _sourceRuntime.Snapshot.LastSource.ToString(),
                _sourceRuntime.Snapshot.Failure,
                (_domains as IGuardianConfigurableDomains)?.Configuration);

        try
        {
            await PerformanceGuardianCodec.WriteResponseAsync(
                stream,
                response,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client transport loss is not guardian authority loss.
        }
    }

    private void WriteReport(
        PerformanceGuardianAuthority? authority,
        Exception? failure)
    {
        var domains =
            _domains.Snapshot;

        var source =
            _sourceRuntime.Snapshot;

        var report =
            new GuardianReport(
                SchemaVersion: 2,
                TargetProfileId:
                    _options.TargetProfileId,
                OwnerPid:
                    _options.OwnerPid,
                OwnerStartUtcTicks:
                    _options.OwnerStartUtcTicks,
                ParentLostDetected:
                    _parentLost,
                ExitReason:
                    _exitReason,
                FinalPhase:
                    authority?.Phase.ToString(),
                Connections:
                    _connections,
                AcceptedRequests:
                    _acceptedRequests,
                RejectedRequests:
                    _rejectedRequests,
                EnableCalls:
                    domains.EnableCalls,
                ReleaseCalls:
                    domains.ReleaseCalls,
                LastReleaseReason:
                    domains.LastReleaseReason,
                InitialSource:
                    domains.LastInitialSource?.ToString(),
                CpuHardwareWriteAttempts:
                    domains.CpuHardwareWriteAttempts,
                GpuHardwareWriteAttempts:
                    domains.GpuHardwareWriteAttempts,
                CpuDomainState:
                    domains.CpuState,
                GpuDomainState:
                    domains.GpuState,
                CpuDomainStatus:
                    domains.CpuStatus,
                GpuDomainStatus:
                    domains.GpuStatus,
                SourceRuntimeActive:
                    source.Active,
                SourceRuntimeStartCalls:
                    source.StartCalls,
                SourceRuntimeStopCalls:
                    source.StopCalls,
                SourceListenerRegistrations:
                    source.ListenerRegistrations,
                SourceNotificationSignals:
                    source.NotificationSignals,
                SourceReconciliationSignals:
                    source.ReconciliationSignals,
                SourceDuplicateSignals:
                    source.DuplicateSignals,
                SourceCpuDispatchAttempts:
                    source.CpuDispatchAttempts,
                SourceGpuDispatchAttempts:
                    source.GpuDispatchAttempts,
                SourceLastSource:
                    source.LastSource.ToString(),
                SourceLastStatus:
                    source.LastStatus,
                SourceLastStopReason:
                    source.LastStopReason,
                SourceFailure:
                    source.Failure,
                HardwareWritesPerformed:
                    domains.CpuHardwareWriteAttempts > 0 ||
                    domains.GpuHardwareWriteAttempts > 0,
                Failure:
                    failure?.ToString());

        DurableJson(
            _options.ReportPath,
            report);
    }

    internal static string ProductionMutexName(
        string targetProfileId) =>
        @"Global\VictusFanControl.PerformanceGuardian." +
        targetProfileId;

    private static void DurableText(
        string path,
        string text)
    {
        var directory =
            Path.GetDirectoryName(
                path);

        if (!string.IsNullOrWhiteSpace(
                directory))
        {
            Directory.CreateDirectory(
                directory);
        }

        using var stream =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);

        using var writer =
            new StreamWriter(
                stream,
                new UTF8Encoding(false),
                4096,
                leaveOpen: true);

        writer.Write(text);
        writer.Flush();
        stream.Flush(true);
    }

    private static void DurableJson<T>(
        string path,
        T value)
    {
        var json =
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

        DurableText(
            path,
            json);
    }

    private readonly record struct GuardianReport(
        int SchemaVersion,
        string TargetProfileId,
        int OwnerPid,
        long OwnerStartUtcTicks,
        bool ParentLostDetected,
        string ExitReason,
        string? FinalPhase,
        int Connections,
        int AcceptedRequests,
        int RejectedRequests,
        int EnableCalls,
        int ReleaseCalls,
        string? LastReleaseReason,
        string? InitialSource,
        int CpuHardwareWriteAttempts,
        int GpuHardwareWriteAttempts,
        string? CpuDomainState,
        string? GpuDomainState,
        string? CpuDomainStatus,
        string? GpuDomainStatus,
        bool SourceRuntimeActive,
        int SourceRuntimeStartCalls,
        int SourceRuntimeStopCalls,
        int SourceListenerRegistrations,
        int SourceNotificationSignals,
        int SourceReconciliationSignals,
        int SourceDuplicateSignals,
        int SourceCpuDispatchAttempts,
        int SourceGpuDispatchAttempts,
        string SourceLastSource,
        string? SourceLastStatus,
        string? SourceLastStopReason,
        string? SourceFailure,
        bool HardwareWritesPerformed,
        string? Failure);
}
