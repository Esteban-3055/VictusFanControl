using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceGuardian;

internal static class PerformanceGuardianProcessFixture
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    private static string Executable =>
        Path.Combine(
            AppContext.BaseDirectory,
            "VictusFanControl.PerformanceGuardian.exe");

    internal static async Task<int> RunOuterAsync(
        string root)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Performance Guardian process fixture requires Windows.");
            }

            root =
                Path.GetFullPath(
                    root);

            Directory.CreateDirectory(
                root);

            var supervisorStart =
                new ProcessStartInfo(
                    Executable)
                {
                    UseShellExecute =
                        false,
                    CreateNoWindow =
                        true
                };

            supervisorStart.ArgumentList.Add(
                "--fixture-supervisor");

            supervisorStart.ArgumentList.Add(
                root);

            using var supervisor =
                Process.Start(
                    supervisorStart) ??
                throw new IOException(
                    "Performance Guardian fixture supervisor did not start.");

            using var timeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(30));

            await supervisor.WaitForExitAsync(
                timeout.Token).ConfigureAwait(false);

            if (supervisor.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Performance Guardian fixture supervisor failed with exit code {supervisor.ExitCode}.");
            }

            var supervisorPath =
                Path.Combine(
                    root,
                    "supervisor.json");

            var supervisorEvidence =
                JsonSerializer.Deserialize<SupervisorEvidence>(
                    File.ReadAllText(
                        supervisorPath)) ??
                throw new InvalidDataException(
                    "Performance Guardian supervisor evidence is missing.");

            try
            {
                using var guardianProcess =
                    Process.GetProcessById(
                        supervisorEvidence.GuardianPid);

                if (guardianProcess.StartTime
                        .ToUniversalTime()
                        .Ticks ==
                    supervisorEvidence.GuardianStartUtcTicks)
                {
                    await guardianProcess.WaitForExitAsync(
                        timeout.Token).ConfigureAwait(false);
                }
            }
            catch (ArgumentException)
            {
                // Guardian already exited after handling owner death.
            }

            await WaitForFileAsync(
                supervisorEvidence.GuardianReportPath,
                timeout.Token).ConfigureAwait(false);

            var guardian =
                JsonSerializer.Deserialize<GuardianReportView>(
                    File.ReadAllText(
                        supervisorEvidence.GuardianReportPath)) ??
                throw new InvalidDataException(
                    "Performance Guardian process report is missing.");

            Require(
                supervisorEvidence.BadNonceRejected,
                "invalid nonce must be rejected");

            Require(
                supervisorEvidence.SessionEnabled,
                "explicit session enable must succeed");

            Require(
                supervisorEvidence.BadDisableNoncePreservedSession,
                "wrong/stale nonce cleanup command must be rejected while listener remains active");

            Require(
                supervisorEvidence.ReconnectStatusPreserved,
                "same nonce reconnect must preserve active session");

            Require(
                supervisorEvidence.DuplicateGuardianExitCode ==
                    PerformanceGuardianHost.ExitDuplicateGuardian,
                "second guardian must lose the single-instance gate");

            Require(
                guardian.ParentLostDetected,
                "guardian must detect owner process exit through held Process handle");

            Require(
                string.Equals(
                    guardian.ExitReason,
                    "PARENT_LOST_RELEASED",
                    StringComparison.Ordinal),
                "parent death must release the live session");

            Require(
                string.Equals(
                    guardian.FinalPhase,
                    PerformanceGuardianAuthorityPhase.ParentLost.ToString(),
                    StringComparison.Ordinal),
                "guardian final phase must be ParentLost");

            Require(
                guardian.EnableCalls == 1 &&
                guardian.ReleaseCalls == 1,
                "guardian must enable once and release once");

            Require(
                guardian.SourceRuntimeStartCalls == 1 &&
                guardian.SourceListenerRegistrations == 1,
                "explicit ENABLE_SESSION must prime/register exactly one Guardian source runtime");

            Require(
                guardian.SourceRuntimeStopCalls == 1 &&
                !guardian.SourceRuntimeActive,
                "parent death must stop the Guardian source listener exactly once");

            Require(
                string.Equals(
                    guardian.SourceLastStopReason,
                    "PARENT_PROCESS_EXIT",
                    StringComparison.Ordinal),
                "parent death must fence source notifications before release");

            Require(
                string.Equals(
                    guardian.LastReleaseReason,
                    "PARENT_PROCESS_EXIT",
                    StringComparison.Ordinal),
                "parent death release reason");

            Require(
                guardian.RejectedRequests >= 1,
                "bad nonce rejection must be observed by server");

            Require(
                !guardian.HardwareWritesPerformed,
                "guardian fixture must perform zero hardware writes");

            using var mutex =
                new Mutex(
                    initiallyOwned: true,
                    supervisorEvidence.MutexName,
                    out var createdNew);

            Require(
                createdNew,
                "guardian must release its named mutex on exit");

            mutex.ReleaseMutex();

            Console.WriteLine(
                "PASS Performance Guardian native process fixture: invalid nonce rejected, reconnect preserved, duplicate guardian blocked, parent-handle exit released one simulated session, zero hardware I/O.");

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Performance Guardian native process fixture: FAIL - " +
                ex);

            return 1;
        }
    }

    internal static async Task<int> RunSupervisorAsync(
        string root)
    {
        try
        {
            root =
                Path.GetFullPath(
                    root);

            Directory.CreateDirectory(
                root);

            using var owner =
                Process.GetCurrentProcess();

            var ownerStartTicks =
                owner.StartTime
                    .ToUniversalTime()
                    .Ticks;

            var nonce =
                Guid.NewGuid();

            var suffix =
                Guid.NewGuid()
                    .ToString("N");

            var pipeName =
                "VictusFanControl.PerformanceGuardian.Fixture." +
                suffix;

            var mutexName =
                @"Global\VictusFanControl.PerformanceGuardian.Fixture." +
                suffix;

            var readyPath =
                Path.Combine(
                    root,
                    "guardian-ready.txt");

            var reportPath =
                Path.Combine(
                    root,
                    "guardian-report.json");

            var guardianStart =
                NewGuardianStart(
                    pipeName,
                    mutexName,
                    nonce,
                    owner.Id,
                    ownerStartTicks,
                    reportPath,
                    readyPath);

            using var guardian =
                Process.Start(
                    guardianStart) ??
                throw new IOException(
                    "Performance Guardian fixture process did not start.");

            await WaitForFileAsync(
                readyPath,
                CancellationToken.None,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            var badNonceRejected =
                await VerifyBadNonceRejectedAsync(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks).ConfigureAwait(false);

            var sessionEnabled =
                await EnableSessionAsync(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks).ConfigureAwait(false);

            var badDisableNoncePreservedSession =
                await VerifyBadDisableNonceRejectedAsync(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks).ConfigureAwait(false);

            var reconnectStatusPreserved =
                await VerifyReconnectAsync(
                    pipeName,
                    nonce,
                    owner.Id,
                    ownerStartTicks).ConfigureAwait(false);

            var duplicateReport =
                Path.Combine(
                    root,
                    "duplicate-report.json");

            var duplicateReady =
                Path.Combine(
                    root,
                    "duplicate-ready.txt");

            var duplicateStart =
                NewGuardianStart(
                    "VictusFanControl.PerformanceGuardian.Duplicate." +
                    suffix,
                    mutexName,
                    nonce,
                    owner.Id,
                    ownerStartTicks,
                    duplicateReport,
                    duplicateReady);

            using var duplicate =
                Process.Start(
                    duplicateStart) ??
                throw new IOException(
                    "Duplicate Performance Guardian fixture process did not start.");

            using var duplicateTimeout =
                new CancellationTokenSource(
                    TimeSpan.FromSeconds(10));

            await duplicate.WaitForExitAsync(
                duplicateTimeout.Token).ConfigureAwait(false);

            var evidence =
                new SupervisorEvidence(
                    GuardianPid:
                        guardian.Id,
                    GuardianStartUtcTicks:
                        guardian.StartTime
                            .ToUniversalTime()
                            .Ticks,
                    GuardianReportPath:
                        reportPath,
                    MutexName:
                        mutexName,
                    BadNonceRejected:
                        badNonceRejected,
                    SessionEnabled:
                        sessionEnabled,
                    BadDisableNoncePreservedSession:
                        badDisableNoncePreservedSession,
                    ReconnectStatusPreserved:
                        reconnectStatusPreserved,
                    DuplicateGuardianExitCode:
                        duplicate.ExitCode);

            DurableJson(
                Path.Combine(
                    root,
                    "supervisor.json"),
                evidence);

            // Deliberately bypass managed cleanup. The detached guardian must
            // survive long enough to observe the owner process handle signal,
            // release its simulated session and exit by itself.
            Environment.Exit(0);

            return 0;
        }
        catch (Exception ex)
        {
            DurableText(
                Path.Combine(
                    root,
                    "supervisor-error.txt"),
                ex.ToString());

            return 1;
        }
    }

    private static ProcessStartInfo NewGuardianStart(
        string pipeName,
        string mutexName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks,
        string reportPath,
        string readyPath)
    {
        var start =
            new ProcessStartInfo(
                Executable)
            {
                UseShellExecute =
                    false,
                CreateNoWindow =
                    true
            };

        start.ArgumentList.Add(
            "--run-fixture");

        Add(
            start,
            "--target",
            TargetProfileId);

        Add(
            start,
            "--pipe",
            pipeName);

        Add(
            start,
            "--mutex",
            mutexName);

        Add(
            start,
            "--nonce",
            nonce.ToString("D"));

        Add(
            start,
            "--owner-pid",
            ownerPid.ToString());

        Add(
            start,
            "--owner-start-ticks",
            ownerStartTicks.ToString());

        Add(
            start,
            "--report",
            reportPath);

        Add(
            start,
            "--ready",
            readyPath);

        return start;
    }

    private static async Task<bool> VerifyBadNonceRejectedAsync(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks)
    {
        using var pipe =
            await ConnectAsync(
                pipeName).ConfigureAwait(false);

        var request =
            NewRequest(
                Guid.NewGuid(),
                PerformanceGuardianProtocol.Hello,
                ownerPid,
                ownerStartTicks);

        await PerformanceGuardianCodec.WriteRequestAsync(
            pipe,
            request,
            CancellationToken.None).ConfigureAwait(false);

        var response =
            await PerformanceGuardianCodec.ReadResponseAsync(
                pipe,
                CancellationToken.None).ConfigureAwait(false);

        return response is not null &&
            !response.Ok &&
            string.Equals(
                response.Code,
                "AUTH_NONCE",
                StringComparison.Ordinal);
    }

    private static async Task<bool> EnableSessionAsync(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks)
    {
        using var pipe =
            await ConnectAsync(
                pipeName).ConfigureAwait(false);

        var hello =
            NewRequest(
                nonce,
                PerformanceGuardianProtocol.Hello,
                ownerPid,
                ownerStartTicks);

        var helloResponse =
            await RoundTripAsync(
                pipe,
                hello).ConfigureAwait(false);

        if (!helloResponse.Ok ||
            !string.Equals(
                helloResponse.Code,
                "HELLO_OK",
                StringComparison.Ordinal))
        {
            return false;
        }

        var enable =
            NewRequest(
                nonce,
                PerformanceGuardianProtocol.EnableSession,
                cpuEnabled: true,
                gpuEnabled: true);

        var response =
            await RoundTripAsync(
                pipe,
                enable).ConfigureAwait(false);

        return response.Ok &&
            response.SessionEnabled &&
            response.CpuEnabled &&
            response.GpuEnabled &&
            string.Equals(
                response.Code,
                "SESSION_ENABLED",
                StringComparison.Ordinal);
    }

    private static async Task<bool> VerifyBadDisableNonceRejectedAsync(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks)
    {
        using var pipe =
            await ConnectAsync(
                pipeName).ConfigureAwait(false);

        var hello =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    nonce,
                    PerformanceGuardianProtocol.Hello,
                    ownerPid,
                    ownerStartTicks)).ConfigureAwait(false);

        if (!hello.Ok ||
            !hello.SessionEnabled)
        {
            return false;
        }

        var badDisable =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    Guid.NewGuid(),
                    PerformanceGuardianProtocol.DisableSession)).ConfigureAwait(false);

        var status =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    nonce,
                    PerformanceGuardianProtocol.Status)).ConfigureAwait(false);

        return !badDisable.Ok &&
            string.Equals(
                badDisable.Code,
                "AUTH_NONCE",
                StringComparison.Ordinal) &&
            status.Ok &&
            status.SessionEnabled &&
            status.CpuEnabled &&
            status.GpuEnabled;
    }

    private static async Task<bool> VerifyReconnectAsync(
        string pipeName,
        Guid nonce,
        int ownerPid,
        long ownerStartTicks)
    {
        using var pipe =
            await ConnectAsync(
                pipeName).ConfigureAwait(false);

        var helloResponse =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    nonce,
                    PerformanceGuardianProtocol.Hello,
                    ownerPid,
                    ownerStartTicks)).ConfigureAwait(false);

        if (!helloResponse.Ok ||
            !helloResponse.SessionEnabled)
        {
            return false;
        }

        var status =
            await RoundTripAsync(
                pipe,
                NewRequest(
                    nonce,
                    PerformanceGuardianProtocol.Status)).ConfigureAwait(false);

        return status.Ok &&
            status.SessionEnabled &&
            status.CpuEnabled &&
            status.GpuEnabled &&
            string.Equals(
                status.Code,
                "STATUS_OK",
                StringComparison.Ordinal);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName)
    {
        var pipe =
            new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous |
                PipeOptions.WriteThrough);

        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(5));

        await pipe.ConnectAsync(
            timeout.Token).ConfigureAwait(false);

        return pipe;
    }

    private static async Task<PerformanceGuardianResponse> RoundTripAsync(
        Stream stream,
        PerformanceGuardianRequest request)
    {
        await PerformanceGuardianCodec.WriteRequestAsync(
            stream,
            request,
            CancellationToken.None).ConfigureAwait(false);

        return
            await PerformanceGuardianCodec.ReadResponseAsync(
                stream,
                CancellationToken.None).ConfigureAwait(false) ??
            throw new EndOfStreamException(
                "Performance Guardian fixture pipe closed before response.");
    }

    private static PerformanceGuardianRequest NewRequest(
        Guid nonce,
        string type,
        int? ownerPid = null,
        long? ownerStartTicks = null,
        bool? cpuEnabled = null,
        bool? gpuEnabled = null) =>
        new(
            PerformanceGuardianProtocol.Version,
            Guid.NewGuid(),
            TargetProfileId,
            nonce,
            type,
            ownerPid,
            ownerStartTicks,
            cpuEnabled,
            gpuEnabled);

    private static void Add(
        ProcessStartInfo start,
        string name,
        string value)
    {
        start.ArgumentList.Add(
            name);

        start.ArgumentList.Add(
            value);
    }

    private static async Task WaitForFileAsync(
        string path,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var ownTimeout =
            timeout.HasValue
                ? new CancellationTokenSource(
                    timeout.Value)
                : null;

        using var linked =
            ownTimeout is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    ownTimeout.Token);

        var token =
            linked?.Token ??
            cancellationToken;

        while (!File.Exists(path))
        {
            token.ThrowIfCancellationRequested();

            await Task.Delay(
                25,
                token).ConfigureAwait(false);
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private static void DurableJson<T>(
        string path,
        T value) =>
        DurableText(
            path,
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));

    private static void DurableText(
        string path,
        string content)
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

        writer.Write(
            content);

        writer.Flush();
        stream.Flush(true);
    }

    internal sealed record SupervisorEvidence(
        int GuardianPid,
        long GuardianStartUtcTicks,
        string GuardianReportPath,
        string MutexName,
        bool BadNonceRejected,
        bool SessionEnabled,
        bool BadDisableNoncePreservedSession,
        bool ReconnectStatusPreserved,
        int DuplicateGuardianExitCode);

    private sealed record GuardianReportView(
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
        int? EnableCalls,
        int? ReleaseCalls,
        string? LastReleaseReason,
        bool SourceRuntimeActive,
        int SourceRuntimeStartCalls,
        int SourceRuntimeStopCalls,
        int SourceListenerRegistrations,
        int SourceNotificationSignals,
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
