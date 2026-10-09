using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Safety;

public static class Hp8C40ThermalEmergencyConfirmationSelfTest
{
    public static int Run(TextWriter output)
    {
        var failures = new List<string>();
        var hardware = new HardwareIdentity(
            "HP",
            "8C40",
            "63.43",
            "HP",
            "Victus by HP Gaming Laptop 15-fa1xxx",
            "9D0R1LA#AKH",
            Hp8C40TargetProfile.ValidatedBiosVersion);

        var now = DateTimeOffset.UtcNow;

        var confirmation = new Hp8C40ThermalEmergencyConfirmation();

        for (var i = 0; i < 4; i++)
        {
            var timestamp = now + TimeSpan.FromSeconds(i);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 96, gpuC: 70);
            var result = Apply(confirmation, hardware, snapshot, timestamp);

            Check(
                !result.ThermalEmergency &&
                result.PreconditionsReady &&
                confirmation.CurrentCpuConsecutiveHighSamples == i + 1,
                $"CPU transient sample {i + 1}/5 must remain admitted");
        }

        {
            var timestamp = now + TimeSpan.FromSeconds(4);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 96, gpuC: 70);
            var result = Apply(confirmation, hardware, snapshot, timestamp);

            Check(
                result.ThermalEmergency &&
                !result.PreconditionsReady &&
                confirmation.CurrentCpuConsecutiveHighSamples == 5,
                "fifth unique consecutive CPU >=95 C sample must confirm thermal emergency");
        }

        confirmation.Reset();

        {
            var timestamp = now + TimeSpan.FromSeconds(10);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 96, gpuC: 70);

            for (var i = 0; i < 4; i++)
            {
                var result = Apply(confirmation, hardware, snapshot, timestamp);
                Check(
                    !result.ThermalEmergency &&
                    confirmation.CurrentCpuConsecutiveHighSamples == 1,
                    "re-evaluating the same telemetry timestamp must not advance CPU streak");
            }
        }

        {
            var lowTimestamp = now + TimeSpan.FromSeconds(11);
            var low = Snapshot(lowTimestamp, cpuEffectiveC: 80, gpuC: 70);
            var lowResult = Apply(confirmation, hardware, low, lowTimestamp);

            Check(
                !lowResult.ThermalEmergency &&
                confirmation.CurrentCpuConsecutiveHighSamples == 0,
                "below-95 C CPU sample must reset confirmation streak");
        }

        {
            var timestamp = now + TimeSpan.FromSeconds(12);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 99, gpuC: 70);
            var result = Apply(confirmation, hardware, snapshot, timestamp);

            Check(
                result.ThermalEmergency &&
                !result.PreconditionsReady,
                "CPU >=99 C must hand off immediately");
        }

        confirmation.Reset();

        {
            var timestamp = now + TimeSpan.FromSeconds(20);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 75, gpuC: 87);
            var result = Apply(confirmation, hardware, snapshot, timestamp);

            Check(
                result.ThermalEmergency &&
                !result.PreconditionsReady,
                "GPU >=87 C must remain immediate");
        }

        confirmation.Reset();

        {
            var timestamp = now + TimeSpan.FromSeconds(30);
            var snapshot = Snapshot(timestamp, cpuEffectiveC: 96, gpuC: 70);
            var raw = SafetyGate.Evaluate(
                hardware,
                SystemState.Degraded,
                snapshot,
                timestamp,
                fanWritePathPresent: true);

            var result = confirmation.Apply(hardware, snapshot, raw);

            Check(
                !result.PreconditionsReady &&
                confirmation.CurrentCpuConsecutiveHighSamples == 0,
                "non-thermal unsafe state must never be made ready by CPU confirmation");
        }

        confirmation.Reset();

        {
            var otherHardware = hardware with
            {
                BoardProduct = "88F8",
                BoardVersion = "88.58",
                SystemProductName = "Victus by HP Laptop 16-d0xxx",
                SystemSku = "62C37LA#AKH",
                BiosVersion = Hp88F8TargetProfile.ValidatedBiosVersion
            };

            var timestamp = now + TimeSpan.FromSeconds(40);
            var snapshot = Snapshot(
                timestamp,
                cpuEffectiveC: 96,
                gpuC: 70,
                gpuName: Hp88F8TargetProfile.ExpectedGpuName);

            var raw = SafetyGate.Evaluate(
                otherHardware,
                SystemState.Healthy,
                snapshot,
                timestamp,
                fanWritePathPresent: true);

            var result = confirmation.Apply(
                otherHardware,
                snapshot,
                raw);

            Check(
                result.ThermalEmergency &&
                confirmation.CurrentCpuConsecutiveHighSamples == 0,
                "non-8C40 targets must retain raw SafetyGate behavior");
        }

        foreach (var failure in failures)
        {
            output.WriteLine($"FAIL  {failure}");
        }

        output.WriteLine(
            failures.Count == 0
                ? "HP 8C40 thermal confirmation self-test: PASS"
                : $"HP 8C40 thermal confirmation self-test: FAIL ({failures.Count})");

        return failures.Count == 0 ? 0 : 1;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                failures.Add(message);
            }
        }
    }

    private static SafetyGateResult Apply(
        Hp8C40ThermalEmergencyConfirmation confirmation,
        HardwareIdentity hardware,
        TelemetrySnapshot snapshot,
        DateTimeOffset now)
    {
        var raw = SafetyGate.Evaluate(
            hardware,
            SystemState.Healthy,
            snapshot,
            now,
            fanWritePathPresent: true);

        return confirmation.Apply(
            hardware,
            snapshot,
            raw);
    }

    private static TelemetrySnapshot Snapshot(
        DateTimeOffset timestamp,
        double cpuEffectiveC,
        double gpuC,
        string gpuName = Hp8C40TargetProfile.ExpectedGpuName)
    {
        var package = Math.Min(cpuEffectiveC, 80);
        var hottest = cpuEffectiveC;

        return new TelemetrySnapshot(
            Timestamp: timestamp,
            CpuName: "Intel Core i7-13700H",
            CpuTemperatureC: package,
            CpuPackagePowerW: 35,
            CpuLoadPercent: 30,
            GpuName: gpuName,
            GpuTemperatureC: gpuC,
            GpuPowerW: 55,
            GpuLoadPercent: 80,
            CpuFanRpm: 3500,
            GpuFanRpm: 3400)
        {
            CpuExpectedPhysicalCoreCount = 2,
            CpuCoreTemperatures =
            [
                new CpuCoreTemperatureSample(0, 0, "Performance", hottest),
                new CpuCoreTemperatureSample(1, 2, "Performance", 72)
            ]
        };
    }
}
