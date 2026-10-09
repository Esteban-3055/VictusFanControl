using System.Text.Json;
using System.Windows.Forms;

namespace VictusFanControl.ModernStandbyProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(
                argument =>
                    string.Equals(
                        argument,
                        "--self-test",
                        StringComparison.Ordinal)))
        {
            return RunSelfTest();
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine(
                "The Modern Standby M0 observer requires Windows.");
            return 2;
        }

        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        if (!NativeClocks.TryReadUnbiasedMilliseconds(
                out _,
                out var clockFailure))
        {
            Console.Error.WriteLine(
                $"QueryUnbiasedInterruptTime preflight failed: {clockFailure}");
            return 3;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var form =
            new ModernStandbyProbeForm(options);

        Application.Run(form);
        return form.ExitCode;
    }

    private static int RunSelfTest()
    {
        var failures = new List<string>();

        if (!string.Equals(
                NativePowerEvents.DescribeBroadcast(
                    NativePowerEvents.PbtApmSuspend),
                "PBT_APMSUSPEND",
                StringComparison.Ordinal))
        {
            failures.Add("PBT_APMSUSPEND mapping");
        }

        if (!string.Equals(
                NativePowerEvents.DescribeBroadcast(
                    NativePowerEvents.PbtPowerSettingChange),
                "PBT_POWERSETTINGCHANGE",
                StringComparison.Ordinal))
        {
            failures.Add("PBT_POWERSETTINGCHANGE mapping");
        }

        if (!string.Equals(
                NativePowerEvents.DescribeSetting(
                    NativePowerEvents.GuidSessionDisplayStatus),
                "GUID_SESSION_DISPLAY_STATUS",
                StringComparison.Ordinal))
        {
            failures.Add("session-display GUID mapping");
        }

        if (!string.Equals(
                NativePowerEvents.DescribeDisplayState(0),
                "Off",
                StringComparison.Ordinal) ||
            !string.Equals(
                NativePowerEvents.DescribeDisplayState(1),
                "On",
                StringComparison.Ordinal) ||
            !string.Equals(
                NativePowerEvents.DescribeDisplayState(2),
                "Dimmed",
                StringComparison.Ordinal))
        {
            failures.Add("display-state mapping");
        }

        var synthetic = new ProbeReport(
            SchemaVersion: 1,
            ProbeName: "M0 synthetic",
            ReadOnly: true,
            StartedAtUtc: DateTimeOffset.UnixEpoch,
            CompletedAtUtc: DateTimeOffset.UnixEpoch,
            ProcessId: 1,
            SessionId: 1,
            ExitReason: "self-test",
            RegistrationSucceeded: true,
            SessionDisplayOffSeen: true,
            SessionDisplayOnAfterOffSeen: true,
            SuspendSeen: false,
            ResumeAutomaticSeen: false,
            ResumeSuspendSeen: false,
            ResumeCriticalSeen: false,
            DroppedEventCount: 0,
            CycleTiming: null,
            Events: Array.Empty<ProbeEvent>());

        var json =
            JsonSerializer.Serialize(
                synthetic,
                ProbeJson.Options);

        var roundTrip =
            JsonSerializer.Deserialize<ProbeReport>(
                json,
                ProbeJson.Options);

        if (roundTrip is null ||
            !roundTrip.ReadOnly ||
            !roundTrip.SessionDisplayOffSeen ||
            !roundTrip.SessionDisplayOnAfterOffSeen)
        {
            failures.Add("report JSON round-trip");
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(
                "Modern Standby M0 observer self-test: FAIL");
            foreach (var failure in failures)
            {
                Console.Error.WriteLine($" - {failure}");
            }

            return 1;
        }

        Console.WriteLine(
            "Modern Standby M0 observer self-test: PASS");
        return 0;
    }
}
