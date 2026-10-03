using VictusFanControl.Cli;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;

namespace VictusFanControl.Runtime;

internal static class WmiOnlyInvestigationPolicySelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            var valid = CliOptions.Parse(["--wmi-only-investigation", "--duration-seconds", "60",
                "--stop-file", "stop.txt", "--ready-file", "ready.json"]);
            if (!valid.WmiOnlyInvestigation || valid.DurationSeconds != 60) throw new Exception("Isolation options lost.");
            foreach (var forbidden in new string[][] { ["--restore-hp-auto"], ["--probe-8c40-setpoint"],
                ["--fan-wmi-telemetry-self-test"], ["--health-test-minutes", "1"],
                ["--restore-hp-auto", "--skip-ec-snapshots"] })
            {
                _ = CliOptions.Parse(forbidden); // Each fixture is otherwise a valid invocation.
                Reject(() => CliOptions.Parse(["--wmi-only-investigation", .. forbidden]));
            }
            Reject(() => CliOptions.Parse(["--stop-file", "stop.txt"]));
            Reject(() => CliOptions.Parse(["--ready-file", "ready.json"]));

            // Sticky policy is installed last in the hardware-free fan self-test process.
            WmiOnlyInvestigationPolicy.Enable();
            WmiOnlyInvestigationPolicy.EnsureTargetAllowed(Hp8C40TargetProfile.Instance);
            Reject(() => WmiOnlyInvestigationPolicy.EnsureTargetAllowed(null));
            Reject(() => WmiOnlyInvestigationPolicy.EnsureTargetAllowed(Hp88F8TargetProfile.Instance));
            WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(Hp8C40BiosFanControl.BuildGetFanLevelRequest());
            foreach (var request in new[] { Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 30),
                Hp8C40BiosFanControl.BuildReleaseFanLevelRequest(), Hp8C40BiosFanControl.BuildLegacyDefaultRequest(),
                new HpBiosRequest(0x20008, 0x2D, [1, 0, 0, 0], 128),
                new HpBiosRequest(0x20008, 0x2D, [0, 0, 0, 0], 0),
                new HpBiosRequest(0x20008, 0x26, [0, 0, 0, 0], 4) })
                Reject(() => WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(request));

            // Real constructor must refuse BEFORE attempting to load a nonexistent module.
            Reject(() => { using var ec = new AcpiEcReader("missing-isolation-fixture.bin"); });
            if (WmiOnlyInvestigationPolicy.DeniedEcAccesses != 1 ||
                WmiOnlyInvestigationPolicy.DeniedWmiRequests != 6) throw new Exception("Denied-operation evidence lost.");
            output.WriteLine("PASS: WMI-only mode rejects mixed CLI actions, foreign targets, direct EC construction and HP writes before hardware I/O.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: WMI-only isolation boundary: " + ex); return 1; }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("Forbidden isolation action was accepted.");
    }
}
