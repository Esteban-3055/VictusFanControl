using System.Diagnostics;
using System.Security.Principal;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Gate A is intentionally read-only. It validates Session 0 access to the
/// exact hardware fingerprint, PawnIO EC reads and HP BIOS/WMI reads.
/// </summary>
internal static class GateAProbe
{
    public static GateAProbeResult Run(GateAOptions options, GateAFileLog log)
    {
        var timestamp = DateTimeOffset.Now;
        var runId = Guid.NewGuid();
        var process = Process.GetCurrentProcess();
        var sessionId = process.SessionId;
        var accountName = WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName;

        HardwareIdentity? hardware = null;
        var targetMatched = false;
        string? targetReason = null;
        Hp88F8EcControlState? ec = null;
        (byte CpuLevel, byte GpuLevel)? biosLevels = null;

        try
        {
            log.Write(
                $"GATE A START run={runId}; pid={process.Id}; session={sessionId}; " +
                $"account={accountName}; base={AppContext.BaseDirectory}; " +
                $"modules={options.ModulesDirectory}");

            hardware = HardwareIdentityReader.ReadCurrent();
            targetMatched = Hp88F8TargetProfile.Matches(hardware, out var reason);
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

            var ecModulePath = Path.Combine(options.ModulesDirectory, "LpcACPIEC.bin");
            if (!File.Exists(ecModulePath))
            {
                throw new FileNotFoundException(
                    "Gate A requires the signed LpcACPIEC.bin module.",
                    ecModulePath);
            }

            ec = new Hp88F8EcControlStateProbe(options.ModulesDirectory).Read();
            log.Write($"PawnIO/EC READ PASS: {ec}");

            biosLevels = new Hp88F8BiosFanControl().GetCurrentFanLevels();
            log.Write(
                $"HP BIOS/WMI READ PASS: GetFanLevel CPU={biosLevels.Value.CpuLevel} " +
                $"GPU={biosLevels.Value.GpuLevel}");

            var success = new GateAProbeResult(
                Success: true,
                Timestamp: timestamp,
                RunId: runId,
                ProcessId: process.Id,
                SessionId: sessionId,
                AccountName: accountName,
                ModulesDirectory: options.ModulesDirectory,
                Hardware: hardware,
                TargetMatched: targetMatched,
                TargetReason: targetReason,
                EcState: ec.ToString(),
                CpuSetpoint: ec.CpuSetpoint,
                GpuSetpoint: ec.GpuSetpoint,
                CpuRpm: ec.CpuRpm,
                GpuRpm: ec.GpuRpm,
                BiosCpuCurrentLevel: biosLevels.Value.CpuLevel,
                BiosGpuCurrentLevel: biosLevels.Value.GpuLevel,
                Failure: null);

            log.Write(
                $"GATE A PASS run={runId}; Session0={sessionId == 0}; " +
                "NO FAN WRITES WERE ISSUED.");

            return success;
        }
        catch (Exception ex)
        {
            log.Write($"GATE A FAIL run={runId}; {ex.GetType().Name}: {ex.Message}");

            return new GateAProbeResult(
                Success: false,
                Timestamp: timestamp,
                RunId: runId,
                ProcessId: process.Id,
                SessionId: sessionId,
                AccountName: accountName,
                ModulesDirectory: options.ModulesDirectory,
                Hardware: hardware,
                TargetMatched: targetMatched,
                TargetReason: targetReason,
                EcState: ec?.ToString(),
                CpuSetpoint: ec?.CpuSetpoint,
                GpuSetpoint: ec?.GpuSetpoint,
                CpuRpm: ec?.CpuRpm,
                GpuRpm: ec?.GpuRpm,
                BiosCpuCurrentLevel: biosLevels?.CpuLevel,
                BiosGpuCurrentLevel: biosLevels?.GpuLevel,
                Failure: ex.ToString());
        }
    }
}
