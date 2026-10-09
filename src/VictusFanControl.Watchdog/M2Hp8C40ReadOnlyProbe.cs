using System.Diagnostics;
using System.Security.Principal;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Watchdog;

/// <summary>
/// Deliberately read-only hardware surface for the HP 8C40 M2 Session-0 gate.
/// No SetFanLevel, restore, EC-register write, watchdog lease or policy method is
/// exposed through this interface.
/// </summary>
internal interface IM2Hp8C40ReadOnlyHardware
{
    HardwareIdentity ReadHardwareIdentity();

    (byte CpuSetpoint, byte GpuSetpoint) ReadEcSetpoint();

    (byte CpuLevel, byte GpuLevel) ReadBiosCurrentLevels();
}

internal sealed class M2Hp8C40ReadOnlyHardware :
    IM2Hp8C40ReadOnlyHardware
{
    private readonly string _modulesDirectory;

    public M2Hp8C40ReadOnlyHardware(
        string modulesDirectory)
    {
        _modulesDirectory = modulesDirectory;
    }

    public HardwareIdentity ReadHardwareIdentity() =>
        HardwareIdentityReader.ReadCurrent();

    public (byte CpuSetpoint, byte GpuSetpoint) ReadEcSetpoint() =>
        new Hp8C40EcControlStateProbe(
            _modulesDirectory).ReadSetpoint();

    public (byte CpuLevel, byte GpuLevel) ReadBiosCurrentLevels() =>
        new Hp8C40BiosFanControl().GetCurrentFanLevels();
}

internal sealed record M2Hp8C40ProbeContext(
    int ProcessId,
    int SessionId,
    string AccountName,
    string? UserSid,
    string ModulesDirectory);

internal sealed record M2Hp8C40ReadOnlyResult(
    bool Success,
    DateTimeOffset Timestamp,
    Guid RunId,
    int ProcessId,
    int SessionId,
    string AccountName,
    string? UserSid,
    string ModulesDirectory,
    HardwareIdentity? Hardware,
    string TargetProfileId,
    bool TargetMatched,
    string? TargetReason,
    bool EcReadAttempted,
    bool EcReadSucceeded,
    int? CpuSetpoint,
    int? GpuSetpoint,
    bool WmiReadAttempted,
    bool WmiReadSucceeded,
    int? BiosCpuCurrentLevel,
    int? BiosGpuCurrentLevel,
    string? Failure);

/// <summary>
/// M2 proves that the exact HP 8C40 dependencies are readable from a real
/// LocalSystem Windows service in Session 0. It intentionally performs only:
/// - registry hardware identity reads;
/// - the narrow EC ownership read (0x34/0x35 through the ACPI RD_EC protocol);
/// - HP BIOS/WMI GetFanLevel.
///
/// It never acquires a watchdog lease and never calls any fan write/restore API.
/// </summary>
internal static class M2Hp8C40ReadOnlyProbe
{
    private const string LocalSystemSid = "S-1-5-18";

    public static M2Hp8C40ReadOnlyResult Run(
        WatchdogOptions options,
        WatchdogFileLog log)
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();

        var context = new M2Hp8C40ProbeContext(
            ProcessId: process.Id,
            SessionId: process.SessionId,
            AccountName:
                identity?.Name ??
                Environment.UserName,
            UserSid:
                identity?.User?.Value,
            ModulesDirectory:
                options.ModulesDirectory);

        var runId = Guid.NewGuid();
        var timestamp = DateTimeOffset.Now;

        log.Write(
            $"M2 8C40 START run={runId}; pid={context.ProcessId}; " +
            $"session={context.SessionId}; account={context.AccountName}; " +
            $"sid={context.UserSid ?? "unknown"}; modules={context.ModulesDirectory}");

        var result =
            Evaluate(
                context,
                new M2Hp8C40ReadOnlyHardware(
                    options.ModulesDirectory),
                modulePresent:
                    File.Exists(
                        Path.Combine(
                            options.ModulesDirectory,
                            "LpcACPIEC.bin")),
                timestamp,
                runId);

        if (result.Success)
        {
            log.Write(
                $"M2 8C40 PASS run={runId}; target={result.TargetProfileId}; " +
                $"EC={result.CpuSetpoint}/{result.GpuSetpoint}; " +
                $"GetFanLevel={result.BiosCpuCurrentLevel}/{result.BiosGpuCurrentLevel}; " +
                "NO FAN/RESTORE/LEASE WRITE AUTHORITY WAS USED.");
        }
        else
        {
            log.Write(
                $"M2 8C40 FAIL run={runId}; {result.Failure}");
        }

        return result;
    }

    internal static M2Hp8C40ReadOnlyResult Evaluate(
        M2Hp8C40ProbeContext context,
        IM2Hp8C40ReadOnlyHardware hardwareAccess,
        bool modulePresent,
        DateTimeOffset? timestamp = null,
        Guid? runId = null)
    {
        HardwareIdentity? hardware = null;
        var targetMatched = false;
        string? targetReason = null;
        var ecAttempted = false;
        var ecSucceeded = false;
        (byte CpuSetpoint, byte GpuSetpoint)? setpoint = null;
        var wmiAttempted = false;
        var wmiSucceeded = false;
        (byte CpuLevel, byte GpuLevel)? biosLevels = null;

        try
        {
            if (context.SessionId != 0)
            {
                throw new InvalidOperationException(
                    $"M2 HP 8C40 requires Windows Session 0; observed SessionId={context.SessionId}.");
            }

            if (!string.Equals(
                    context.UserSid,
                    LocalSystemSid,
                    StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException(
                    $"M2 HP 8C40 requires LocalSystem ({LocalSystemSid}); observed SID='{context.UserSid ?? "unknown"}'.");
            }

            if (!modulePresent)
            {
                throw new FileNotFoundException(
                    "M2 HP 8C40 requires the signed LpcACPIEC.bin module.",
                    Path.Combine(
                        context.ModulesDirectory,
                        "LpcACPIEC.bin"));
            }

            hardware =
                hardwareAccess.ReadHardwareIdentity();

            targetMatched =
                Hp8C40TargetProfile.Matches(
                    hardware,
                    out var reason);
            targetReason = reason;

            if (!targetMatched)
            {
                throw new InvalidOperationException(
                    $"Exact HP 8C40 target fingerprint refused: {targetReason}");
            }

            ecAttempted = true;
            setpoint =
                hardwareAccess.ReadEcSetpoint();
            ecSucceeded = true;

            wmiAttempted = true;
            biosLevels =
                hardwareAccess.ReadBiosCurrentLevels();
            wmiSucceeded = true;

            return BuildResult(
                success: true,
                failure: null);
        }
        catch (Exception ex)
        {
            return BuildResult(
                success: false,
                failure:
                    $"{ex.GetType().Name}: {ex.Message}");
        }

        M2Hp8C40ReadOnlyResult BuildResult(
            bool success,
            string? failure) =>
            new(
                Success: success,
                Timestamp:
                    timestamp ??
                    DateTimeOffset.Now,
                RunId:
                    runId ??
                    Guid.NewGuid(),
                ProcessId:
                    context.ProcessId,
                SessionId:
                    context.SessionId,
                AccountName:
                    context.AccountName,
                UserSid:
                    context.UserSid,
                ModulesDirectory:
                    context.ModulesDirectory,
                Hardware:
                    hardware,
                TargetProfileId:
                    Hp8C40TargetProfile.Instance.Id,
                TargetMatched:
                    targetMatched,
                TargetReason:
                    targetReason,
                EcReadAttempted:
                    ecAttempted,
                EcReadSucceeded:
                    ecSucceeded,
                CpuSetpoint:
                    setpoint?.CpuSetpoint,
                GpuSetpoint:
                    setpoint?.GpuSetpoint,
                WmiReadAttempted:
                    wmiAttempted,
                WmiReadSucceeded:
                    wmiSucceeded,
                BiosCpuCurrentLevel:
                    biosLevels?.CpuLevel,
                BiosGpuCurrentLevel:
                    biosLevels?.GpuLevel,
                Failure:
                    failure);
    }
}
