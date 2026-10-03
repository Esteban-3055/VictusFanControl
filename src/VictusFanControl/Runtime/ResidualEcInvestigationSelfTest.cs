using VictusFanControl.Cli;
using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Runtime;

internal static class ResidualEcInvestigationSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            var options = CliOptions.Parse(["--residual-ec-investigation", "--ec-interval-ms", "5000"]);
            if (!options.ReadOnlyInvestigation || options.WmiOnlyInvestigation || !options.ResidualEcInvestigation || options.InvestigationEcIntervalMs != 5000)
                throw new Exception("C options lost.");
            foreach (var forbidden in new string[][] { ["--wmi-only-investigation"], ["--restore-hp-auto"],
                ["--probe-8c40-setpoint"], ["--fan-wmi-telemetry-self-test"], ["--ec-interval-ms", "1999"],
                ["--ec-interval-ms", "60001"], ["--residual-ec-investigation-self-test"] })
                Reject(() => CliOptions.Parse(["--residual-ec-investigation", .. forbidden]));
            Reject(() => CliOptions.Parse(["--wmi-only-investigation", "--ec-interval-ms", "5000"]));
            Reject(() => CliOptions.Parse(["--ec-interval-ms", "5000"]));

            WmiOnlyInvestigationPolicy.Enable(residualEc: true);
            WmiOnlyInvestigationPolicy.EnsureDirectEcAllowed();
            WmiOnlyInvestigationPolicy.EnsureTargetAllowed(Hp8C40TargetProfile.Instance);
            Reject(() => WmiOnlyInvestigationPolicy.EnsureTargetAllowed(Hp88F8TargetProfile.Instance));
            Reject(() => WmiOnlyInvestigationPolicy.EnsureTargetAllowed(null));
            Reject(() => WmiOnlyInvestigationPolicy.Enable());
            for (var i = 0; i <= 255; i++)
            {
                var register = (byte)i;
                if (WmiOnlyInvestigationPolicy.IsResidualRegister(register))
                {
                    WmiOnlyInvestigationPolicy.EnsureRegisterReadAllowed(register);
                    WmiOnlyInvestigationPolicy.EnsureProtocolWriteAllowed(0x62, register);
                }
                else
                {
                    Reject(() => WmiOnlyInvestigationPolicy.EnsureRegisterReadAllowed(register));
                    Reject(() => WmiOnlyInvestigationPolicy.EnsureProtocolWriteAllowed(0x62, register));
                }
                if (register == 0x80) WmiOnlyInvestigationPolicy.EnsureProtocolWriteAllowed(0x66, register);
                else Reject(() => WmiOnlyInvestigationPolicy.EnsureProtocolWriteAllowed(0x66, register));
            }
            Reject(() => WmiOnlyInvestigationPolicy.EnsureProtocolWriteAllowed(0x64, 0x80));
            WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(Hp8C40BiosFanControl.BuildGetFanLevelRequest());
            foreach (var request in new[] { Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 30),
                Hp8C40BiosFanControl.BuildReleaseFanLevelRequest(), Hp8C40BiosFanControl.BuildLegacyDefaultRequest() })
                Reject(() => WmiOnlyInvestigationPolicy.EnsureWmiRequestAllowed(request));

            using var csv = new StringWriter();
            var reads = 0;
            using var sampler = new ResidualEcInvestigationSampler(() =>
            {
                reads++;
                if (reads == 3) throw new IOException("fixture, \"failure\"");
                return new(0xFF, 0xFF, 0, 0);
            }, csv, TimeSpan.FromSeconds(5));
            if (!sampler.ReadIfDue(TimeSpan.Zero) || !sampler.ReadIfDue(TimeSpan.FromSeconds(4)) || reads != 1)
                throw new Exception("Sampler polled too early.");
            if (!sampler.ReadIfDue(TimeSpan.FromSeconds(40)) || reads != 2 || sampler.Samples != 2)
                throw new Exception("Sampler accumulated catch-up reads.");
            if (sampler.ReadIfDue(TimeSpan.FromSeconds(46)) || sampler.ReadIfDue(TimeSpan.FromSeconds(60)) || reads != 3 || sampler.Fault is null)
                throw new Exception("Sampler did not retain its first failure and stop.");
            if (!csv.ToString().Contains("failed") || !csv.ToString().Contains("\"\"failure\"\""))
                throw new Exception("Failed sample evidence/CSV escaping lost.");
            output.WriteLine("PASS: scenario C CLI exclusivity, exact target, all 256 EC addresses/commands, HP write denial, no catch-up and first-failure capture; no hardware I/O.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL: scenario C: " + ex); return 1; }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("Forbidden C action was accepted.");
    }
}
