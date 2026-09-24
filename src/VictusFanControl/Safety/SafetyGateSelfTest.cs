using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Safety;

public static class SafetyGateSelfTest
{
    public static int Run(TextWriter output)
    {
        var now = DateTimeOffset.UtcNow;
        var goodHardware = new HardwareIdentity(
            "HP",
            "88F8",
            "88.58",
            "HP",
            "Victus by HP Laptop 16-d0xxx",
            "62C37LA",
            "test");

        var good = Snapshot(now, 50, 15, 10, 45, 25, 5, 2200, 2400);

        var cases = new[]
        {
            Case(
                "healthy/fresh/allowed board",
                SafetyGate.Evaluate(goodHardware, SystemState.Healthy, good, now),
                expectedReady: true),

            Case(
                "unsupported board",
                SafetyGate.Evaluate(
                    goodHardware with { BoardProduct = "FFFF" },
                    SystemState.Healthy,
                    good,
                    now),
                expectedReady: false),

            Case(
                "degraded runtime",
                SafetyGate.Evaluate(goodHardware, SystemState.Degraded, good, now),
                expectedReady: false),

            Case(
                "stale telemetry",
                SafetyGate.Evaluate(
                    goodHardware,
                    SystemState.Healthy,
                    good with { Timestamp = now - TimeSpan.FromSeconds(10) },
                    now),
                expectedReady: false),

            Case(
                "missing telemetry",
                SafetyGate.Evaluate(
                    goodHardware,
                    SystemState.Healthy,
                    good with { GpuFanRpm = null },
                    now),
                expectedReady: false),

            Case(
                "implausible sensor",
                SafetyGate.Evaluate(
                    goodHardware,
                    SystemState.Healthy,
                    good with { CpuTemperatureC = 150 },
                    now),
                expectedReady: false),

            Case(
                "thermal handoff",
                SafetyGate.Evaluate(
                    goodHardware,
                    SystemState.Healthy,
                    good with { CpuTemperatureC = SafetyGate.CpuEmergencyC },
                    now),
                expectedReady: false)
        };

        var failed = 0;
        foreach (var testCase in cases)
        {
            var pass =
                testCase.Result.PreconditionsReady == testCase.ExpectedReady &&
                !testCase.Result.CustomControlPermitted &&
                !testCase.Result.FanWritePathPresent;

            output.WriteLine(
                $"{(pass ? "PASS" : "FAIL")}  {testCase.Name}  " +
                $"ready={testCase.Result.PreconditionsReady} custom={testCase.Result.CustomControlPermitted}");

            if (!pass)
            {
                failed++;
                foreach (var reason in testCase.Result.Reasons)
                {
                    output.WriteLine($"      {reason}");
                }
            }
        }

        output.WriteLine();
        output.WriteLine(failed == 0
            ? "SafetyGate self-test: PASS"
            : $"SafetyGate self-test: FAIL ({failed} case(s))");

        return failed == 0 ? 0 : 5;
    }

    private static TestCase Case(
        string name,
        SafetyGateResult result,
        bool expectedReady) =>
        new(name, result, expectedReady);

    private static TelemetrySnapshot Snapshot(
        DateTimeOffset timestamp,
        double cpuTemp,
        double cpuPower,
        double cpuLoad,
        double gpuTemp,
        double gpuPower,
        double gpuLoad,
        double cpuFan,
        double gpuFan) =>
        new(
            Timestamp: timestamp,
            CpuName: "Intel test CPU",
            CpuTemperatureC: cpuTemp,
            CpuPackagePowerW: cpuPower,
            CpuLoadPercent: cpuLoad,
            GpuName: "NVIDIA test GPU",
            GpuTemperatureC: gpuTemp,
            GpuPowerW: gpuPower,
            GpuLoadPercent: gpuLoad,
            CpuFanRpm: cpuFan,
            GpuFanRpm: gpuFan);

    private sealed record TestCase(
        string Name,
        SafetyGateResult Result,
        bool ExpectedReady);
}
