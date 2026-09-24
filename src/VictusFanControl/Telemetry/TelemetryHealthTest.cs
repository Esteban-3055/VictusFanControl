namespace VictusFanControl.Telemetry;

public static class TelemetryHealthTest
{
    private sealed class Counter
    {
        public Counter(string name) => Name = name;

        public string Name { get; }
        public long Good { get; private set; }
        public long Missing { get; private set; }
        public int ConsecutiveMissing { get; private set; }
        public int MaxConsecutiveMissing { get; private set; }

        public void Observe(bool present)
        {
            if (present)
            {
                Good++;
                ConsecutiveMissing = 0;
                return;
            }

            Missing++;
            ConsecutiveMissing++;
            MaxConsecutiveMissing = Math.Max(MaxConsecutiveMissing, ConsecutiveMissing);
        }
    }

    public static async Task<int> RunAsync(
        HardwareTelemetryReader reader,
        int minutes,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        if (minutes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes));
        }

        Console.WriteLine($"Telemetry health soak: {minutes} minute(s), {intervalMs} ms interval.");
        Console.WriteLine("Pass criterion: zero missing sensor values after warm-up.");
        Console.WriteLine();

        // Prime differential CPU power/load counters. This sample is intentionally
        // excluded from the health statistics.
        _ = reader.ReadSnapshot();
        await Task.Delay(intervalMs, cancellationToken);

        var counters = new[]
        {
            new Counter("CPU temperature"),
            new Counter("CPU package power"),
            new Counter("CPU load"),
            new Counter("GPU temperature"),
            new Counter("GPU power"),
            new Counter("GPU load"),
            new Counter("CPU fan RPM"),
            new Counter("GPU fan RPM")
        };

        var started = DateTimeOffset.UtcNow;
        var deadline = started.AddMinutes(minutes);
        long samples = 0;

        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var snapshot = reader.ReadSnapshot();
            samples++;

            counters[0].Observe(snapshot.CpuTemperatureC.HasValue);
            counters[1].Observe(snapshot.CpuPackagePowerW.HasValue);
            counters[2].Observe(snapshot.CpuLoadPercent.HasValue);
            counters[3].Observe(snapshot.GpuTemperatureC.HasValue);
            counters[4].Observe(snapshot.GpuPowerW.HasValue);
            counters[5].Observe(snapshot.GpuLoadPercent.HasValue);
            counters[6].Observe(snapshot.CpuFanRpm.HasValue);
            counters[7].Observe(snapshot.GpuFanRpm.HasValue);

            if (!snapshot.IsComplete)
            {
                Console.WriteLine(
                    $"{snapshot.Timestamp.ToLocalTime():HH:mm:ss}  INCOMPLETE  " +
                    string.Join(", ", MissingNames(snapshot)));
                foreach (var line in reader.GetReadDiagnostics())
                {
                    Console.WriteLine($"  {line}");
                }
            }

            await Task.Delay(intervalMs, cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine("Telemetry health results:");
        Console.WriteLine($"Samples: {samples}");
        foreach (var counter in counters)
        {
            var total = counter.Good + counter.Missing;
            var success = total == 0 ? 0.0 : counter.Good * 100.0 / total;
            Console.WriteLine(
                $"{counter.Name,-20} {success,7:0.000}%  " +
                $"missing={counter.Missing}  max-streak={counter.MaxConsecutiveMissing}");
        }

        foreach (var line in reader.GetHealthSummary())
        {
            Console.WriteLine(line);
        }

        var passed = counters.All(counter => counter.Missing == 0);
        Console.WriteLine();
        Console.WriteLine(passed ? "RESULT: PASS" : "RESULT: FAIL");
        return passed ? 0 : 4;
    }

    private static IEnumerable<string> MissingNames(TelemetrySnapshot s)
    {
        if (!s.CpuTemperatureC.HasValue) yield return "cpu_temp";
        if (!s.CpuPackagePowerW.HasValue) yield return "cpu_power";
        if (!s.CpuLoadPercent.HasValue) yield return "cpu_load";
        if (!s.GpuTemperatureC.HasValue) yield return "gpu_temp";
        if (!s.GpuPowerW.HasValue) yield return "gpu_power";
        if (!s.GpuLoadPercent.HasValue) yield return "gpu_load";
        if (!s.CpuFanRpm.HasValue) yield return "cpu_fan";
        if (!s.GpuFanRpm.HasValue) yield return "gpu_fan";
    }
}
