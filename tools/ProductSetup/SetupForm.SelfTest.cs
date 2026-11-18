namespace VictusSetup;

internal sealed partial class SetupForm
{
    internal static int TestWizard()
    {
        var root = Path.Combine(Path.GetTempPath(), "VictusSetup-ui-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var profiles = Path.Combine(root, "profiles.json");
        var exitCode = Environment.ExitCode;
        try
        {
            using (var fresh = new SetupForm([], profiles))
                Require(fresh._startupAutomatic.Checked && !fresh._desktopShortcut.Checked, "Unexpected fresh-install defaults.");
            File.WriteAllText(profiles, "{\"activateAutomaticOnStart\":false}");
            var installCalls = 0;
            var launchCalls = 0;
            Options? received = null;
            var app = Path.Combine(root, "app");
            using var update = new SetupForm([], profiles, options =>
            {
                received = options;
                return ++installCalls == 1 ? Task.FromException<string>(new IOException("Fixture blocked install.")) : Task.FromResult(app);
            }, installed =>
            {
                Require(installed == app, "Finish opened a different package.");
                launchCalls++;
            });
            void ClickNext() => update.NextAsync().GetAwaiter().GetResult();
            Require(update._page == Page.Welcome && !update._startupAutomatic.Checked, "Update changed saved startup by default.");
            ClickNext();
            Require(update._page == Page.Options, "Welcome did not advance to options.");
            update._desktopShortcut.Checked = true;
            ClickNext();
            update.GoBack();
            Require(update._page == Page.Options && update._desktopShortcut.Checked, "Back discarded the selected options.");
            ClickNext();
            Require(update._page == Page.Ready && installCalls == 0, "Setup ran before the Install step.");
            ClickNext();
            Require(update._page == Page.Failed && update._errorDetails.Text.Contains("Fixture blocked install.") &&
                launchCalls == 0 && received is { EnableStartupAutomatic: false, DesktopShortcut: true }, "Failure flow or option handoff changed.");
            ClickNext();
            Require(update._page == Page.Finished && update._launch.Checked && launchCalls == 0, "Setup launched the app before Finish.");
            ClickNext();
            Require(installCalls == 2 && launchCalls == 1, "Finish or retry ran the wrong number of times.");
            Console.WriteLine("Installer wizard: PASS (welcome, options, back, confirmation, error/retry, finish, preserved startup and opt-in shortcuts; no installation/hardware IO).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            Environment.ExitCode = exitCode;
            File.Delete(profiles);
            Directory.Delete(root);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
