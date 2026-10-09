using System.Text.Json;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.Hardware.Hp;

public sealed record Hp8C40M3HandoffRecord(
    int SchemaVersion,
    string TargetProfileId,
    Guid ArmRunId,
    Guid Nonce,
    int ArmProcessId,
    long ArmProcessStartUtcTicks,
    DateTimeOffset CreatedAtUtc,
    byte ExpectedCpuSetpoint,
    byte ExpectedGpuSetpoint,
    bool BaselineWasFirmwareOwned,
    byte MaxFanAtArm,
    byte FanSwitchAtArm,
    ushort CpuRpmAtArm,
    ushort GpuRpmAtArm)
{
    public const int CurrentSchemaVersion = 1;
    public const byte RequiredQualificationLevel = 30;
}

public sealed record Hp8C40M3ServiceResult(
    bool Success,
    DateTimeOffset Timestamp,
    Guid ServiceRunId,
    Guid? ArmRunId,
    Guid? Nonce,
    int ProcessId,
    int SessionId,
    string AccountName,
    string? UserSid,
    HardwareIdentity? Hardware,
    string TargetProfileId,
    bool TargetMatched,
    string? TargetReason,
    bool HandoffClaimed,
    string? ClaimedHandoffPath,
    bool HandoffValidated,
    bool ArmerIdentityValidated,
    int? BeforeCpuSetpoint,
    int? BeforeGpuSetpoint,
    int? BeforeMaxFan,
    int? BeforeFanSwitch,
    int? BeforeCpuRpm,
    int? BeforeGpuRpm,
    bool RestoreCallSucceeded,
    bool VerifiedFfFf,
    int? AfterCpuSetpoint,
    int? AfterGpuSetpoint,
    int? BiosCpuCurrentLevelAfter,
    int? BiosGpuCurrentLevelAfter,
    double ElapsedMilliseconds,
    string? Failure);

public static class Hp8C40M3JsonFile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static void WriteHandoff(
        string path,
        Hp8C40M3HandoffRecord value) =>
        WriteAtomically(path, value);

    public static Hp8C40M3HandoffRecord ReadHandoff(
        string path) =>
        Read<Hp8C40M3HandoffRecord>(path);

    public static Hp8C40M3ServiceResult ReadResult(
        string path) =>
        Read<Hp8C40M3ServiceResult>(path);

    private static T Read<T>(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var value = JsonSerializer.Deserialize<T>(
            stream,
            JsonOptions);

        return value ??
            throw new InvalidDataException(
                $"JSON file '{path}' deserialized to null.");
    }

    private static void WriteAtomically<T>(
        string path,
        T value)
    {
        var fullPath = Path.GetFullPath(path);
        var directory =
            Path.GetDirectoryName(fullPath) ??
            throw new InvalidOperationException(
                "M3 JSON path has no parent directory.");

        Directory.CreateDirectory(directory);

        var tempPath =
            fullPath +
            "." +
            Guid.NewGuid().ToString("N") +
            ".tmp";

        try
        {
            using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(
                    stream,
                    value,
                    JsonOptions);

                stream.Flush(flushToDisk: true);
            }

            File.Move(
                tempPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
