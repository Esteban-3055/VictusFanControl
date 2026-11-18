namespace VictusSetup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--self-test" }))
        {
            Environment.ExitCode = SelfTest.Run();
            if (Environment.ExitCode == 0)
            {
                ApplicationConfiguration.Initialize();
                Environment.ExitCode = SetupForm.TestWizard();
            }
            if (Environment.ExitCode == 0 && typeof(Package).Assembly.GetManifestResourceInfo("payload.zip") is not null)
            {
                var directory = Path.Combine(Path.GetTempPath(), "VictusSetup-embedded-fixture-" + Guid.NewGuid().ToString("N"));
                try { Package.ExtractEmbedded(directory); Console.WriteLine("Embedded release payload: PASS (SHA-256 and every manifest file; no install/hardware IO)."); }
                catch (Exception ex) { Environment.ExitCode = 1; Console.Error.WriteLine(ex); }
                finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            }
            return;
        }
        ApplicationConfiguration.Initialize();
        try { Application.Run(new SetupForm(args)); }
        catch (Exception ex) { Environment.ExitCode = 1; MessageBox.Show(ex.Message, "Instalación no completada"); }
    }
}
