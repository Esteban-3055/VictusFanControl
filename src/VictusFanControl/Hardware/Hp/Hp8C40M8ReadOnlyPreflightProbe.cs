using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

/// <summary>
/// M8 no-write hardware readiness probe for the exact HP 8C40 target.
/// This probe only reads telemetry and evaluates the production SafetyGate.
/// It never constructs a fan-control backend or watchdog lease.
/// </summary>
public static class Hp8C40M8ReadOnlyPreflightProbe
{
    public const double CpuPhysicalAbortC = 90.0;
    public const double GpuPhysicalAbortC = 82.0;
    public const int RequiredConsecutiveHealthySamples = 3;
    public const int MaximumSamples = 6;
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(
        string modulesDirectory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var hardware = HardwareIdentityReader.ReadCurrent();
        if (!Hp8C40TargetProfile.Matches(hardware, out var targetReason))
        {
            await output.WriteLineAsync(
                $"M8 preflight probe refused: exact HP 8C40 target mismatch: {targetReason}");
            return 151;
        }

        using var reader = new HardwareTelemetryReader(modulesDirectory);

        foreach (var line in reader.GetBackendDiagnostics())
        {
            await output.WriteLineAsync(line);
        }

        if (!reader.BackendsInitialized ||
            !string.Equals(
                reader.TargetProfile?.Id,
                Hp8C40TargetProfile.Instance.Id,
                StringComparison.Ordinal))
        {
            await output.WriteLineAsync(
                "M8 preflight probe refused: exact-target telemetry backends are not fully initialized.");
            return 152;
        }

        // Warm differential CPU power/load counters. No control backend exists in this path.
        var warmup = reader.ReadSnapshot();
        if (AtPhysicalAbortLimit(warmup, out var warmupAbort))
        {
            await output.WriteLineAsync($"M8 preflight probe refused: {warmupAbort}");
            return 155;
        }

        reader.ResetHealthWindow();
        await Task.Delay(SampleInterval, cancellationToken);

        var consecutiveHealthy = 0;

        for (var sampleIndex = 1; sampleIndex <= MaximumSamples; sampleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = reader.ReadSnapshot();
            var now = DateTimeOffset.UtcNow;
            var safety = SafetyGate.Evaluate(
                hardware,
                SystemState.Healthy,
                snapshot,
                now,
                fanWritePathPresent: false);

            var effectiveCpu = snapshot.CpuControlTemperatureC;
            var gpuTemperature = snapshot.GpuTemperatureC;

            if (AtPhysicalAbortLimit(snapshot, out var abortDetail))
            {
                await output.WriteLineAsync(
                    $"M8_PREFLIGHT_ABORT sample={sampleIndex} {abortDetail}");
                return 155;
            }

            var gpuTemperaturePositive =
                gpuTemperature.HasValue &&
                gpuTemperature.Value > 0;

            var accepted =
                safety.PreconditionsReady &&
                safety.TelemetryDeviceIdentityValid &&
                gpuTemperaturePositive &&
                snapshot.IsComplete &&
                snapshot.CpuCoreTelemetryComplete;

            if (accepted)
            {
                consecutiveHealthy++;
            }
            else
            {
                consecutiveHealthy = 0;
            }

            await output.WriteLineAsync(
                $"M8_PREFLIGHT_SAMPLE sample={sampleIndex}/{MaximumSamples} " +
                $"accepted={accepted} consecutive={consecutiveHealthy}/{RequiredConsecutiveHealthySamples} " +
                $"cpuEffective={Format(effectiveCpu)}C cpuPackage={Format(snapshot.CpuTemperatureC)}C " +
                $"cpuCoreMax={Format(snapshot.CpuCoreMaxTemperatureC)}C gpu={Format(gpuTemperature)}C " +
                $"cpuPower={Format(snapshot.CpuPackagePowerW)}W gpuPower={Format(snapshot.GpuPowerW)}W " +
                $"cpuLoad={Format(snapshot.CpuLoadPercent)}% gpuLoad={Format(snapshot.GpuLoadPercent)}% " +
                $"cpuFan={Format(snapshot.CpuFanRpm)}rpm gpuFan={Format(snapshot.GpuFanRpm)}rpm " +
                $"preconditionsReady={safety.PreconditionsReady} " +
                $"gpuIdentityValid={safety.TelemetryDeviceIdentityValid} " +
                $"thermalEmergency={safety.ThermalEmergency}");

            if (!accepted)
            {
                foreach (var reason in safety.Reasons.Where(
                    reason => !reason.Contains(
                        "Fan write/restore backend is intentionally absent",
                        StringComparison.Ordinal)))
                {
                    await output.WriteLineAsync($"  safety: {reason}");
                }

                if (!gpuTemperaturePositive)
                {
                    await output.WriteLineAsync(
                        "  safety: GPU temperature must be present and > 0 C for M8 preflight.");
                }
            }

            if (consecutiveHealthy >= RequiredConsecutiveHealthySamples)
            {
                foreach (var line in reader.GetHealthSummary())
                {
                    await output.WriteLineAsync(line);
                }

                await output.WriteLineAsync(
                    "M8_PREFLIGHT_TELEMETRY_PASS " +
                    $"samples={RequiredConsecutiveHealthySamples} " +
                    $"target={Hp8C40TargetProfile.Instance.Id} " +
                    $"cpuAbort={CpuPhysicalAbortC:0}C gpuAbort={GpuPhysicalAbortC:0}C");
                return 0;
            }

            if (sampleIndex < MaximumSamples)
            {
                await Task.Delay(SampleInterval, cancellationToken);
            }
        }

        foreach (var line in reader.GetReadDiagnostics())
        {
            await output.WriteLineAsync(line);
        }

        await output.WriteLineAsync(
            $"M8 preflight probe failed to obtain {RequiredConsecutiveHealthySamples} consecutive " +
            $"SafetyGate-ready telemetry samples within {MaximumSamples} attempts.");
        return 153;
    }

    private static bool AtPhysicalAbortLimit(
        TelemetrySnapshot snapshot,
        out string detail)
    {
        var effectiveCpu = snapshot.CpuControlTemperatureC;
        var gpu = snapshot.GpuTemperatureC;

        if (effectiveCpu.HasValue &&
            effectiveCpu.Value >= CpuPhysicalAbortC)
        {
            detail =
                $"effective CPU {effectiveCpu.Value:0.0} C reached M8 physical abort limit " +
                $"{CpuPhysicalAbortC:0} C.";
            return true;
        }

        if (gpu.HasValue &&
            gpu.Value >= GpuPhysicalAbortC)
        {
            detail =
                $"GPU {gpu.Value:0.0} C reached M8 physical abort limit " +
                $"{GpuPhysicalAbortC:0} C.";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    private static string Format(double? value) =>
        value?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a";
}
