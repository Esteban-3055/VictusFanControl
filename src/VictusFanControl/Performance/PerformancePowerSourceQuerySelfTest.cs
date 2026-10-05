namespace VictusFanControl.Performance;

internal static class PerformancePowerSourceQuerySelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            Require(
                WindowsPerformancePowerSourceReader
                    .MapRawAcLineStatus(1) ==
                PerformancePowerSourceKind.Ac,
                "ACLineStatus=1 maps to AC");

            Require(
                WindowsPerformancePowerSourceReader
                    .MapRawAcLineStatus(0) ==
                PerformancePowerSourceKind.Battery,
                "ACLineStatus=0 maps to Battery");

            Require(
                WindowsPerformancePowerSourceReader
                    .MapRawAcLineStatus(0xFF) ==
                PerformancePowerSourceKind.Unknown,
                "ACLineStatus=255 maps to Unknown");

            Require(
                WindowsPerformancePowerSourceReader
                    .MapRawAcLineStatus(2) ==
                PerformancePowerSourceKind.Unknown,
                "unexpected ACLineStatus fails closed");

            output.WriteLine(
                "Performance Windows power-source query self-test: PASS (0=Battery, 1=AC, unknown fails closed, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance Windows power-source query self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
