using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal static class PerformanceUiSettingsSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-PerformanceUi-" +
                Guid.NewGuid().ToString("N"));

        var path =
            Path.Combine(
                root,
                "settings.json");

        try
        {
            var defaults =
                PerformanceUiSettingsStore.Load(
                    path);

            Require(
                defaults.AcPl1Watts == 35 &&
                defaults.AcPl2Watts == 60 &&
                defaults.BatteryPl1Watts == 8 &&
                defaults.BatteryPl2Watts == 15,
                "missing settings load exact product defaults");

            var custom =
                defaults with
                {
                    AcPl1Watts = 30,
                    AcPl2Watts = 55,
                    BatteryPl1Watts = 10,
                    BatteryPl2Watts = 20
                };

            PerformanceUiSettingsStore.Save(
                custom,
                path);

            Require(
                PerformanceUiSettingsStore.Load(path) ==
                    custom,
                "valid custom settings round-trip");

            RequireThrows<ArgumentOutOfRangeException>(
                () =>
                    PerformanceUiSettingsStore.Save(
                        custom with
                        {
                            BatteryPl1Watts = 7
                        },
                        path),
                "below-floor value rejected");

            RequireThrows<ArgumentOutOfRangeException>(
                () =>
                    PerformanceUiSettingsStore.Save(
                        custom with
                        {
                            AcPl1Watts = 35,
                            AcPl2Watts = 30
                        },
                        path),
                "PL2 below PL1 rejected");

            File.WriteAllText(
                path,
                "{ invalid json");

            var fallback =
                PerformanceUiSettingsStore.Load(
                    path);

            Require(
                fallback ==
                    PerformanceUiSettingsStore.Default(),
                "corrupt settings fail closed to product defaults");

            output.WriteLine(
                "Performance CPU settings self-test: PASS (persistence/validation only, zero hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance CPU settings self-test: FAIL - " +
                ex.Message);

            return 1;
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private static void RequireThrows<T>(
        Action action,
        string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(
            label);
    }
}
