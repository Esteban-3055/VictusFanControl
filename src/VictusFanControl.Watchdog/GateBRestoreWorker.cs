using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.Hosting;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Gate B is a one-shot hardware validation mode. Its only write-capable
/// operation is the already validated HP restore primitive:
/// FF,FF -> LegacyDefault, followed by mandatory EC FF/FF verification.
/// It never exposes ordinary 14..50 fan-level writes.
/// </summary>
internal sealed class GateBRestoreWorker : BackgroundService
{
    private static readonly TimeSpan VerificationTimeout =
        TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval =
        TimeSpan.FromMilliseconds(250);

    private readonly WatchdogOptions _options;
    private readonly IHostApplicationLifetime _lifetime;

    public GateBRestoreWorker(
        WatchdogOptions options,
        IHostApplicationLifetime lifetime)
    {
        _options = options;
        _lifetime = lifetime;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var log = new WatchdogFileLog(
            _options.LogDirectory,
            "watchdog-gate-b");

        var timestamp = DateTimeOffset.Now;
        var runId = Guid.NewGuid();
        var process = Process.GetCurrentProcess();
        var identity = WindowsIdentity.GetCurrent();
        var accountName = identity?.Name ?? Environment.UserName;
        var sessionId = process.SessionId;

        HardwareIdentity? hardware = null;
        var targetMatched = false;
        string? targetReason = null;
        GateBRestoreExecution? execution = null;
        string? infrastructureFailure = null;

        try
        {
            log.Write(
                $"GATE B START run={runId}; pid={process.Id}; session={sessionId}; " +
                $"account={accountName}; modules={_options.ModulesDirectory}");

            if (sessionId != 0)
            {
                throw new InvalidOperationException(
                    $"Gate B requires Windows Session 0; observed SessionId={sessionId}.");
            }

            var sid = identity?.User?.Value;
            if (!string.Equals(sid, "S-1-5-18", StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"Gate B requires LocalSystem (S-1-5-18); observed SID='{sid ?? "unknown"}'.");
            }

            hardware = HardwareIdentityReader.ReadCurrent();
            targetMatched = Hp88F8TargetProfile.Matches(
                hardware,
                out var reason);
            targetReason = reason;

            log.Write(
                $"Hardware: {hardware.BoardDisplay}; System={hardware.SystemProductName}; " +
                $"SKU={hardware.SystemSku}; BIOS={hardware.BiosVersion}; " +
                $"targetMatched={targetMatched}; reason={targetReason}");

            if (!targetMatched)
            {
                throw new InvalidOperationException(
                    $"Exact HP 88F8 target fingerprint refused: {targetReason}");
            }

            var modulePath = Path.Combine(
                _options.ModulesDirectory,
                "LpcACPIEC.bin");

            if (!File.Exists(modulePath))
            {
                throw new FileNotFoundException(
                    "Gate B requires the signed LpcACPIEC.bin module.",
                    modulePath);
            }

            var restoreHardware =
                new GateBRestoreHardware(_options.ModulesDirectory);

            execution = await GateBRestoreExecutor.ExecuteAsync(
                restoreHardware,
                VerificationTimeout,
                PollInterval,
                log.Write,
                stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            infrastructureFailure =
                $"{ex.GetType().Name}: {ex.Message}";
            log.Write(
                $"GATE B INFRASTRUCTURE FAIL run={runId}; {infrastructureFailure}");
        }

        var success =
            infrastructureFailure is null &&
            execution is not null &&
            execution.Success;

        var result = new GateBServiceResult(
            Success: success,
            Timestamp: timestamp,
            RunId: runId,
            ProcessId: process.Id,
            SessionId: sessionId,
            AccountName: accountName,
            ModulesDirectory: _options.ModulesDirectory,
            Hardware: hardware,
            TargetMatched: targetMatched,
            TargetReason: targetReason,
            BeforeState: execution?.Before?.ToString(),
            AfterState: execution?.After?.ToString(),
            BeforeCpuSetpoint: execution?.Before?.CpuSetpoint,
            BeforeGpuSetpoint: execution?.Before?.GpuSetpoint,
            AfterCpuSetpoint: execution?.After?.CpuSetpoint,
            AfterGpuSetpoint: execution?.After?.GpuSetpoint,
            RestoreCallSucceeded: execution?.RestoreCallSucceeded ?? false,
            VerifiedFfFf: execution?.VerifiedFfFf ?? false,
            BiosCpuCurrentLevelAfter: execution?.BiosCpuCurrentLevelAfter,
            BiosGpuCurrentLevelAfter: execution?.BiosGpuCurrentLevelAfter,
            ElapsedMilliseconds: execution?.ElapsedMilliseconds ?? 0,
            Failure: infrastructureFailure ?? execution?.Failure);

        try
        {
            AtomicJsonFile.Write(_options.ResultPath, result);
        }
        catch (Exception ex)
        {
            success = false;
            Environment.ExitCode = 83;
            log.Write(
                $"GATE B RESULT WRITE FAIL run={runId}; " +
                $"{ex.GetType().Name}: {ex.Message}");
            _lifetime.StopApplication();
            return;
        }

        if (success)
        {
            log.Write(
                $"GATE B PASS run={runId}; service alone executed " +
                "FF,FF -> LegacyDefault and verified EC FF/FF.");
            Environment.ExitCode = 0;
        }
        else
        {
            log.Write(
                $"GATE B FAIL run={runId}; {result.Failure ?? "unknown failure"}; " +
                $"restoreCall={result.RestoreCallSucceeded}; verifiedFF={result.VerifiedFfFf}.");
            Environment.ExitCode = 82;
        }

        _lifetime.StopApplication();
    }
}
