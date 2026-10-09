using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// M3 is a one-shot HP 8C40 restore-only service gate. It waits for one fresh
/// handoff produced by the bounded VFC armer, validates the live armer and the
/// exact 30/30 EC ownership state, issues one FF/FF -> LegacyDefault restore,
/// verifies EC FF/FF, publishes the result and exits.
///
/// It exposes no ordinary fan-level write and no watchdog lease.
/// </summary>
internal sealed class M3Hp8C40RestoreOnlyWorker :
    BackgroundService
{
    private static readonly TimeSpan HandoffWaitTimeout =
        TimeSpan.FromSeconds(30);

    private static readonly TimeSpan VerificationTimeout =
        TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly WatchdogOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public M3Hp8C40RestoreOnlyWorker(
        WatchdogOptions options,
        IHostApplicationLifetime lifetime)
    {
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var log =
            new WatchdogFileLog(
                _options.LogDirectory,
                "watchdog-m3-8c40");

        using var process =
            Process.GetCurrentProcess();

        using var identity =
            WindowsIdentity.GetCurrent();

        var timestamp =
            DateTimeOffset.Now;
        var serviceRunId =
            Guid.NewGuid();
        var accountName =
            identity?.Name ??
            Environment.UserName;
        var userSid =
            identity?.User?.Value;
        var sessionId =
            process.SessionId;

        HardwareIdentity? hardwareIdentity = null;
        var targetMatched = false;
        string? targetReason = null;
        var handoffClaimed = false;
        var handoffValidated = false;
        var armerValidated = false;
        string? claimedPath = null;
        Hp8C40M3HandoffRecord? handoff = null;
        M3Hp8C40RestoreExecution? execution = null;
        string? failure = null;

        try
        {
            log.Write(
                $"M3 START run={serviceRunId}; pid={process.Id}; " +
                $"session={sessionId}; account={accountName}; sid={userSid ?? "unknown"}");

            if (sessionId != 0)
            {
                throw new InvalidOperationException(
                    $"M3 requires Windows Session 0; observed {sessionId}.");
            }

            if (!string.Equals(
                    userSid,
                    "S-1-5-18",
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"M3 requires LocalSystem (S-1-5-18); observed '{userSid ?? "unknown"}'.");
            }

            hardwareIdentity =
                HardwareIdentityReader.ReadCurrent();

            targetMatched =
                Hp8C40TargetProfile.Matches(
                    hardwareIdentity,
                    out var reason);
            targetReason = reason;

            if (!targetMatched)
            {
                throw new InvalidOperationException(
                    $"Exact HP 8C40 target fingerprint refused: {targetReason}");
            }

            var modulePath =
                Path.Combine(
                    _options.ModulesDirectory,
                    "LpcACPIEC.bin");

            if (!File.Exists(modulePath))
            {
                throw new FileNotFoundException(
                    "M3 requires the signed LpcACPIEC.bin module.",
                    modulePath);
            }

            if (string.IsNullOrWhiteSpace(
                    _options.M3HandoffPath))
            {
                throw new InvalidOperationException(
                    "M3 handoff path was not configured.");
            }

            claimedPath =
                await WaitAndClaimHandoffAsync(
                    _options.M3HandoffPath,
                    serviceRunId,
                    stoppingToken)
                .ConfigureAwait(false);

            handoffClaimed = true;

            handoff =
                Hp8C40M3JsonFile.ReadHandoff(
                    claimedPath);

            log.Write(
                $"M3 handoff claimed: {claimedPath}; armRun={handoff.ArmRunId}; nonce={handoff.Nonce}");

            execution =
                await M3Hp8C40RestoreExecutor.ExecuteAsync(
                    handoff,
                    new M3Hp8C40RestoreHardware(
                        _options.ModulesDirectory),
                    new M3ArmProcessValidator(),
                    DateTimeOffset.UtcNow,
                    VerificationTimeout,
                    PollInterval,
                    log.Write,
                    stoppingToken)
                .ConfigureAwait(false);

            handoffValidated =
                execution.HandoffValidated;
            armerValidated =
                execution.ArmerIdentityValidated;

            if (!execution.Success)
            {
                failure =
                    execution.Failure ??
                    "M3 restore executor failed without detail.";
            }
        }
        catch (Exception ex)
        {
            failure =
                $"{ex.GetType().Name}: {ex.Message}";

            log.Write(
                $"M3 FAIL before completion: {failure}");
        }

        var result =
            new Hp8C40M3ServiceResult(
                Success:
                    failure is null &&
                    execution?.Success == true,
                Timestamp:
                    timestamp,
                ServiceRunId:
                    serviceRunId,
                ArmRunId:
                    handoff?.ArmRunId,
                Nonce:
                    handoff?.Nonce,
                ProcessId:
                    process.Id,
                SessionId:
                    sessionId,
                AccountName:
                    accountName,
                UserSid:
                    userSid,
                Hardware:
                    hardwareIdentity,
                TargetProfileId:
                    Hp8C40TargetProfile.Instance.Id,
                TargetMatched:
                    targetMatched,
                TargetReason:
                    targetReason,
                HandoffClaimed:
                    handoffClaimed,
                ClaimedHandoffPath:
                    claimedPath,
                HandoffValidated:
                    handoffValidated,
                ArmerIdentityValidated:
                    armerValidated,
                BeforeCpuSetpoint:
                    execution?.Before?.CpuSetpoint,
                BeforeGpuSetpoint:
                    execution?.Before?.GpuSetpoint,
                BeforeMaxFan:
                    execution?.Before?.MaxFan,
                BeforeFanSwitch:
                    execution?.Before?.FanSwitch,
                BeforeCpuRpm:
                    execution?.Before?.CpuRpm,
                BeforeGpuRpm:
                    execution?.Before?.GpuRpm,
                RestoreCallSucceeded:
                    execution?.RestoreCallSucceeded ??
                    false,
                VerifiedFfFf:
                    execution?.VerifiedFfFf ??
                    false,
                AfterCpuSetpoint:
                    execution?.After?.CpuSetpoint,
                AfterGpuSetpoint:
                    execution?.After?.GpuSetpoint,
                BiosCpuCurrentLevelAfter:
                    execution?.BiosCpuCurrentLevelAfter,
                BiosGpuCurrentLevelAfter:
                    execution?.BiosGpuCurrentLevelAfter,
                ElapsedMilliseconds:
                    execution?.ElapsedMilliseconds ??
                    0,
                Failure:
                    failure ??
                    execution?.Failure);

        try
        {
            AtomicJsonFile.Write(
                _options.ResultPath,
                result);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 151;
            log.Write(
                $"M3 result write failed: {ex.GetType().Name}: {ex.Message}");
            _lifetime.StopApplication();
            return;
        }

        if (result.Success)
        {
            Environment.ExitCode = 0;
            log.Write(
                $"M3 PASS run={serviceRunId}; armRun={result.ArmRunId}; " +
                "service issued one restore transaction and verified EC FF/FF.");
        }
        else
        {
            Environment.ExitCode = 150;
            log.Write(
                $"M3 FAIL run={serviceRunId}; {result.Failure ?? "unknown"}; " +
                $"restoreCall={result.RestoreCallSucceeded}; verifiedFF={result.VerifiedFfFf}");
        }

        _lifetime.StopApplication();
    }

    private static async Task<string> WaitAndClaimHandoffAsync(
        string handoffPath,
        Guid serviceRunId,
        CancellationToken cancellationToken)
    {
        var fullPath =
            Path.GetFullPath(
                handoffPath);

        var directory =
            Path.GetDirectoryName(
                fullPath) ??
            throw new InvalidOperationException(
                "M3 handoff path has no parent directory.");

        Directory.CreateDirectory(
            directory);

        var started =
            Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) <
               HandoffWaitTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(fullPath))
            {
                var claimed =
                    fullPath +
                    ".claimed." +
                    serviceRunId.ToString("N") +
                    ".json";

                try
                {
                    File.Move(
                        fullPath,
                        claimed,
                        overwrite: false);

                    return claimed;
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(
                    PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"M3 service did not receive a handoff within {HandoffWaitTimeout.TotalSeconds:0} s.");
    }
}
