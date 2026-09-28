using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VictusFanControl.ModernStandbyProbe;

internal sealed record ProbeOptions(
    string OutputPath,
    string ReadyPath,
    bool AutoExitAfterDisplayCycle,
    int TimeoutMinutes)
{
    public static ProbeOptions Parse(string[] args)
    {
        string? outputPath = null;
        string? readyPath = null;
        var autoExitAfterDisplayCycle = false;
        var timeoutMinutes = 15;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output":
                    outputPath =
                        ReadValue(
                            args,
                            ref index,
                            "--output");
                    break;

                case "--ready":
                    readyPath =
                        ReadValue(
                            args,
                            ref index,
                            "--ready");
                    break;

                case "--auto-exit-after-display-cycle":
                    autoExitAfterDisplayCycle = true;
                    break;

                case "--timeout-minutes":
                {
                    var raw =
                        ReadValue(
                            args,
                            ref index,
                            "--timeout-minutes");

                    if (!int.TryParse(raw, out timeoutMinutes) ||
                        timeoutMinutes is < 1 or > 60)
                    {
                        throw new ArgumentException(
                            "--timeout-minutes must be an integer from 1 through 60.");
                    }

                    break;
                }

                default:
                    throw new ArgumentException(
                        $"Unknown Modern Standby M0 observer argument: {args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException(
                "--output <path> is required.");
        }

        if (string.IsNullOrWhiteSpace(readyPath))
        {
            throw new ArgumentException(
                "--ready <path> is required.");
        }

        return new ProbeOptions(
            Path.GetFullPath(outputPath),
            Path.GetFullPath(readyPath),
            autoExitAfterDisplayCycle,
            timeoutMinutes);
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException(
                $"Missing value for {option}.");
        }

        return args[index];
    }
}

internal sealed record ProbeEvent(
    int Sequence,
    DateTimeOffset Utc,
    ulong? UnbiasedMilliseconds,
    ulong TickCountMilliseconds,
    long QueryPerformanceCounterTicks,
    string Source,
    string Event,
    string Detail);

internal sealed record ProbeCycleTiming(
    double WallMilliseconds,
    double? UnbiasedMilliseconds,
    double TickCountMilliseconds,
    long QueryPerformanceCounterTicks,
    double QueryPerformanceCounterMilliseconds);

internal sealed record ProbeReport(
    int SchemaVersion,
    string ProbeName,
    bool ReadOnly,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    int ProcessId,
    int SessionId,
    string ExitReason,
    bool RegistrationSucceeded,
    bool SessionDisplayOffSeen,
    bool SessionDisplayOnAfterOffSeen,
    bool SuspendSeen,
    bool ResumeAutomaticSeen,
    bool ResumeSuspendSeen,
    bool ResumeCriticalSeen,
    int DroppedEventCount,
    ProbeCycleTiming? CycleTiming,
    IReadOnlyList<ProbeEvent> Events);

internal sealed record ProbeReadyDocument(
    int SchemaVersion,
    bool Ready,
    bool ReadOnly,
    DateTimeOffset RegisteredAtUtc,
    int ProcessId,
    int SessionId,
    string OutputPath,
    string Detail);

internal static class ProbeJson
{
    public static readonly JsonSerializerOptions Options =
        new()
        {
            WriteIndented = true
        };
}

internal readonly record struct ClockSample(
    DateTimeOffset Utc,
    ulong? UnbiasedMilliseconds,
    ulong TickCountMilliseconds,
    long QueryPerformanceCounterTicks)
{
    public static ClockSample Capture()
    {
        ulong? unbiased = null;

        if (NativeClocks.TryReadUnbiasedMilliseconds(
                out var unbiasedMilliseconds,
                out _))
        {
            unbiased = unbiasedMilliseconds;
        }

        return new ClockSample(
            DateTimeOffset.UtcNow,
            unbiased,
            NativeClocks.GetTickCount64(),
            Stopwatch.GetTimestamp());
    }
}

internal static class NativeClocks
{
    private const ulong HundredNanosecondsPerMillisecond =
        10_000UL;

    public static bool TryReadUnbiasedMilliseconds(
        out ulong milliseconds,
        out string? failure)
    {
        milliseconds = 0;
        failure = null;

        try
        {
            if (!QueryUnbiasedInterruptTime(
                    out var unbiasedTime100ns))
            {
                failure =
                    "QueryUnbiasedInterruptTime returned FALSE.";
                return false;
            }

            milliseconds =
                unbiasedTime100ns /
                HundredNanosecondsPerMillisecond;

            return true;
        }
        catch (Exception ex)
        {
            failure =
                $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(
        out ulong unbiasedTime100ns);

    [DllImport("kernel32.dll")]
    public static extern ulong GetTickCount64();
}

internal static class NativePowerEvents
{
    public const int WmPowerBroadcast = 0x0218;

    public const int PbtApmSuspend = 0x0004;
    public const int PbtApmResumeCritical = 0x0006;
    public const int PbtApmResumeSuspend = 0x0007;
    public const int PbtApmPowerStatusChange = 0x000A;
    public const int PbtApmResumeAutomatic = 0x0012;
    public const int PbtPowerSettingChange = 0x8013;

    public static readonly Guid GuidSessionDisplayStatus =
        new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");

    public static readonly Guid GuidConsoleDisplayState =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    public static readonly Guid GuidAcDcPowerSource =
        new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

    public static readonly Guid GuidLidSwitchStateChange =
        new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    public static string DescribeBroadcast(int code) =>
        code switch
        {
            PbtApmSuspend => "PBT_APMSUSPEND",
            PbtApmResumeCritical => "PBT_APMRESUMECRITICAL",
            PbtApmResumeSuspend => "PBT_APMRESUMESUSPEND",
            PbtApmPowerStatusChange => "PBT_APMPOWERSTATUSCHANGE",
            PbtApmResumeAutomatic => "PBT_APMRESUMEAUTOMATIC",
            PbtPowerSettingChange => "PBT_POWERSETTINGCHANGE",
            _ => $"WM_POWERBROADCAST_0x{code:X4}"
        };

    public static string DescribeSetting(Guid setting) =>
        setting == GuidSessionDisplayStatus
            ? "GUID_SESSION_DISPLAY_STATUS"
            : setting == GuidConsoleDisplayState
                ? "GUID_CONSOLE_DISPLAY_STATE"
                : setting == GuidAcDcPowerSource
                    ? "GUID_ACDC_POWER_SOURCE"
                    : setting == GuidLidSwitchStateChange
                        ? "GUID_LIDSWITCH_STATE_CHANGE"
                        : setting.ToString("D");

    public static string DescribeDisplayState(uint value) =>
        value switch
        {
            0 => "Off",
            1 => "On",
            2 => "Dimmed",
            _ => $"Unknown({value})"
        };

    public static string DescribePowerSource(uint value) =>
        value switch
        {
            0 => "AC",
            1 => "DC",
            2 => "Hot/UPS",
            _ => $"Unknown({value})"
        };

    public static string DescribeLidState(uint value) =>
        value switch
        {
            0 => "Closed",
            1 => "Open",
            _ => $"Unknown({value})"
        };
}
