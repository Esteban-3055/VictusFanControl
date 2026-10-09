using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using VictusFanControl.Performance;

namespace VictusFanControl.PerformanceProbe;

internal static class PowerSourceNotificationQualification
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        string[] args)
    {
        if (!TryParse(
                args,
                out var options,
                out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 2;
        }

        var reader =
            new WindowsPerformancePowerSourceReader();

        var initial =
            reader.Read();

        if (!initial.Succeeded)
        {
            Console.Error.WriteLine(
                "Initial direct power-source query failed: " +
                initial.Status);

            return 3;
        }

        if (initial.Source ==
            options.ExpectedSource)
        {
            Console.Error.WriteLine(
                "Initial source already equals the expected destination. " +
                "No notification qualification was attempted.");

            return 4;
        }

        var events =
            new List<NotificationObservation>();

        var startedAt =
            DateTimeOffset.UtcNow;

        var matched =
            false;

        string? listenerError =
            null;

        using var context =
            new ApplicationContext();

        using var timeout =
            new System.Windows.Forms.Timer
            {
                Interval =
                    checked(
                        options.TimeoutSeconds *
                        1000)
            };

        timeout.Tick +=
            (_, _) =>
            {
                timeout.Stop();
                context.ExitThread();
            };

        try
        {
            using var window =
                new PowerNotificationWindow(
                    () =>
                    {
                        var observation =
                            reader.Read();

                        events.Add(
                            new NotificationObservation(
                                DateTimeOffset.UtcNow,
                                observation));

                        Console.WriteLine(
                            $"Power notification -> direct query: {observation.Source} " +
                            $"raw={observation.RawAcLineStatus?.ToString() ?? "null"} " +
                            $"status={observation.Status}");

                        if (observation.Succeeded &&
                            observation.Source ==
                                options.ExpectedSource)
                        {
                            matched = true;
                            context.ExitThread();
                        }
                    });

            timeout.Start();

            Console.WriteLine(
                $"Initial direct source: {initial.Source} " +
                $"raw={initial.RawAcLineStatus?.ToString() ?? "null"}.");

            Console.WriteLine(
                $"Waiting up to {options.TimeoutSeconds}s for Windows GUID_ACDC_POWER_SOURCE notification, " +
                $"then confirming destination {options.ExpectedSource} with GetSystemPowerStatus.");

            Application.Run(
                context);
        }
        catch (Exception ex)
        {
            listenerError =
                ex.ToString();
        }
        finally
        {
            timeout.Stop();
        }

        var report =
            new QualificationReport(
                SchemaVersion: 1,
                TargetProfileId,
                ExpectedSource:
                    options.ExpectedSource,
                TimeoutSeconds:
                    options.TimeoutSeconds,
                StartedAtUtc:
                    startedAt,
                CompletedAtUtc:
                    DateTimeOffset.UtcNow,
                Initial:
                    initial,
                Notifications:
                    events.ToArray(),
                ExpectedSourceConfirmed:
                    matched,
                ListenerError:
                    listenerError,
                HardwareWritesPerformed:
                    false);

        DurableJson(
            options.OutputPath,
            report);

        Console.WriteLine(
            "Power-source notification report: " +
            options.OutputPath);

        if (listenerError is not null)
        {
            Console.Error.WriteLine(
                listenerError);

            return 5;
        }

        if (!matched)
        {
            Console.Error.WriteLine(
                "Expected source was not confirmed before timeout.");

            return 6;
        }

        Console.WriteLine(
            "Power-source notification qualification: PASS. " +
            "Notification was treated only as a trigger; destination came from a direct GetSystemPowerStatus query.");

        return 0;
    }

    internal static int SelfTest(
        TextWriter output)
    {
        try
        {
            Require(
                TryParse(
                    new[]
                    {
                        "--watch-power-source",
                        "--expect",
                        "battery",
                        "--timeout-seconds",
                        "30",
                        "--output",
                        Path.Combine(
                            Path.GetTempPath(),
                            "vfc-source-notification.json")
                    },
                    out var battery,
                    out _),
                "battery notification args parse");

            Require(
                battery.ExpectedSource ==
                    PerformancePowerSourceKind.Battery &&
                battery.TimeoutSeconds == 30,
                "battery notification parse values");

            Require(
                TryParse(
                    new[]
                    {
                        "--watch-power-source",
                        "--expect",
                        "ac",
                        "--timeout-seconds",
                        "5",
                        "--output",
                        Path.Combine(
                            Path.GetTempPath(),
                            "vfc-source-notification-ac.json")
                    },
                    out var ac,
                    out _),
                "AC notification args parse");

            Require(
                ac.ExpectedSource ==
                    PerformancePowerSourceKind.Ac,
                "AC notification expected source");

            Require(
                !TryParse(
                    new[]
                    {
                        "--watch-power-source",
                        "--expect",
                        "unknown",
                        "--output",
                        "x.json"
                    },
                    out _,
                    out _),
                "Unknown cannot be an expected destination");

            Require(
                !TryParse(
                    new[]
                    {
                        "--watch-power-source",
                        "--expect",
                        "battery",
                        "--timeout-seconds",
                        "121",
                        "--output",
                        "x.json"
                    },
                    out _,
                    out _),
                "excessive timeout rejected");

            output.WriteLine(
                "Performance power-source notification harness self-test: PASS (argument gates only, no listener registration, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance power-source notification harness self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static bool TryParse(
        string[] args,
        out Options options,
        out string error)
    {
        options =
            default;

        error =
            "Invalid power-source notification qualification arguments.";

        if (args.Length < 1 ||
            args[0] !=
                "--watch-power-source")
        {
            return false;
        }

        PerformancePowerSourceKind?
            expected = null;

        var timeoutSeconds =
            60;

        string? outputPath =
            null;

        for (var index = 1;
             index < args.Length;
             index++)
        {
            switch (args[index])
            {
                case "--expect"
                    when index + 1 <
                         args.Length:
                {
                    var value =
                        args[++index]
                            .Trim()
                            .ToLowerInvariant();

                    expected =
                        value switch
                        {
                            "ac" =>
                                PerformancePowerSourceKind.Ac,

                            "battery" =>
                                PerformancePowerSourceKind.Battery,

                            _ => null
                        };

                    if (!expected.HasValue)
                    {
                        error =
                            "Expected destination must be 'ac' or 'battery'.";

                        return false;
                    }

                    break;
                }

                case "--timeout-seconds"
                    when index + 1 <
                         args.Length &&
                         int.TryParse(
                             args[index + 1],
                             out var parsed):
                    index++;
                    timeoutSeconds =
                        parsed;
                    break;

                case "--output"
                    when index + 1 <
                         args.Length:
                    outputPath =
                        Path.GetFullPath(
                            args[++index]);
                    break;

                default:
                    error =
                        "Unknown or incomplete notification qualification argument: " +
                        args[index];

                    return false;
            }
        }

        if (!expected.HasValue)
        {
            error =
                "--expect ac|battery is required.";

            return false;
        }

        if (timeoutSeconds is < 5 or > 120)
        {
            error =
                "TimeoutSeconds must be between 5 and 120.";

            return false;
        }

        if (string.IsNullOrWhiteSpace(
                outputPath))
        {
            error =
                "--output is required.";

            return false;
        }

        options =
            new Options(
                expected.Value,
                timeoutSeconds,
                outputPath!);

        return true;
    }

    private static void DurableJson<T>(
        string path,
        T value)
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

        var json =
            JsonSerializer.Serialize(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });

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
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier:
                        false),
                4096,
                leaveOpen: true);

        writer.Write(json);
        writer.Flush();
        stream.Flush(
            flushToDisk: true);
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            "VictusFanControl.PerformanceProbe --watch-power-source --expect <ac|battery> --timeout-seconds <5..120> --output <json-path>");
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }

    private readonly record struct Options(
        PerformancePowerSourceKind ExpectedSource,
        int TimeoutSeconds,
        string OutputPath);

    private readonly record struct NotificationObservation(
        DateTimeOffset CapturedAtUtc,
        PerformancePowerSourceObservation Observation);

    private readonly record struct QualificationReport(
        int SchemaVersion,
        string TargetProfileId,
        PerformancePowerSourceKind ExpectedSource,
        int TimeoutSeconds,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset CompletedAtUtc,
        PerformancePowerSourceObservation Initial,
        NotificationObservation[] Notifications,
        bool ExpectedSourceConfirmed,
        string? ListenerError,
        bool HardwareWritesPerformed);

    internal sealed class PowerNotificationWindow :
        NativeWindow,
        IDisposable
    {
        private const int WmPowerBroadcast =
            0x0218;

        private const int PbtPowerSettingChange =
            0x8013;

        private const uint DeviceNotifyWindowHandle =
            0;

        private static readonly Guid GuidAcDcPowerSource =
            new(
                "5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");

        private readonly Action _onSignal;

        private IntPtr _notificationHandle;

        internal PowerNotificationWindow(
            Action onSignal)
        {
            _onSignal =
                onSignal ??
                throw new ArgumentNullException(
                    nameof(onSignal));

            CreateHandle(
                new CreateParams
                {
                    Caption =
                        "VictusFanControl.PerformanceProbe.PowerNotificationWindow"
                });

            var setting =
                GuidAcDcPowerSource;

            _notificationHandle =
                RegisterPowerSettingNotification(
                    Handle,
                    ref setting,
                    DeviceNotifyWindowHandle);

            if (_notificationHandle ==
                IntPtr.Zero)
            {
                var error =
                    Marshal.GetLastWin32Error();

                DestroyHandle();

                throw new Win32Exception(
                    error,
                    "RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE) failed.");
            }
        }

        protected override void WndProc(
            ref Message message)
        {
            if (message.Msg ==
                    WmPowerBroadcast &&
                message.WParam.ToInt32() ==
                    PbtPowerSettingChange &&
                message.LParam !=
                    IntPtr.Zero)
            {
                var settingGuid =
                    Marshal.PtrToStructure<Guid>(
                        message.LParam);

                if (settingGuid ==
                    GuidAcDcPowerSource)
                {
                    // Deliberately ignore POWERBROADCAST_SETTING.Data.
                    // The notification is only a trigger. The callback performs
                    // a fresh GetSystemPowerStatus query.
                    _onSignal();
                }
            }

            base.WndProc(
                ref message);
        }

        public void Dispose()
        {
            if (_notificationHandle !=
                IntPtr.Zero)
            {
                _ =
                    UnregisterPowerSettingNotification(
                        _notificationHandle);

                _notificationHandle =
                    IntPtr.Zero;
            }

            if (Handle !=
                IntPtr.Zero)
            {
                DestroyHandle();
            }
        }

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        private static extern IntPtr RegisterPowerSettingNotification(
            IntPtr recipient,
            ref Guid powerSettingGuid,
            uint flags);

        [DllImport(
            "user32.dll",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterPowerSettingNotification(
            IntPtr handle);
    }
}
