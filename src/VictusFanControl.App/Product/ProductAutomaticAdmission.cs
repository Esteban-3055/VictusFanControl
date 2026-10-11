using System.Globalization;
using VictusFanControl.Product;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed record ProductAutomaticAdmissionIssue(string Code, string Detail);

/// <summary>Read-only explanations shared by startup and runtime admission. Never samples or writes hardware.</summary>
internal static class ProductAutomaticAdmission
{
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    internal static IReadOnlyList<ProductAutomaticAdmissionIssue> TelemetryIssues(TelemetrySnapshot? s, DateTimeOffset now, bool startup = false)
    {
        var issues = new List<ProductAutomaticAdmissionIssue>();
        if (s is null) { issues.Add(new("no-snapshot", "Todavía no hay una muestra de sensores.")); return issues; }
        void Value(string code, string name, double? value, double min, double max, string unit)
        {
            if (!value.HasValue) issues.Add(new(code + "-missing", name + " sin lectura."));
            else if (!double.IsFinite(value.Value) || value < min || value > max)
                issues.Add(new(code + "-invalid", $"{name} inválida: {Number(value.Value)} {unit}; rango de coherencia {Number(min)}–{Number(max)} {unit}."));
        }
        Value("cpu-temperature", "Temperatura de paquete CPU", s.CpuTemperatureC, 10, 110, "°C");
        Value("gpu-temperature", "Temperatura GPU", s.GpuTemperatureC, 10, 105, "°C");
        Value("cpu-power", "Potencia CPU", s.CpuPackagePowerW, 0, 500, "W");
        Value("gpu-power", "Potencia GPU", s.GpuPowerW, 0, 300, "W");
        Value("cpu-rpm", "RPM CPU", s.CpuFanRpm, 0, 10000, "RPM");
        Value("gpu-rpm", "RPM GPU", s.GpuFanRpm, 0, 10000, "RPM");
        if (!s.ProductTelemetryTolerance || s.CpuLoadPercent.HasValue) Value("cpu-load", "Utilización CPU", s.CpuLoadPercent, 0, 100, "%");
        if (!s.ProductTelemetryTolerance || s.GpuLoadPercent.HasValue) Value("gpu-load", "Utilización GPU", s.GpuLoadPercent, 0, 100, "%");
        if (!s.CpuCoreTelemetryComplete)
            issues.Add(new("cpu-cores-incomplete", $"Temperaturas de núcleos incompletas: {s.CpuCoreTemperatures.Count}/{s.CpuExpectedPhysicalCoreCount?.ToString() ?? "cantidad esperada desconocida"}."));
        if (s.CpuCoreTemperatures.Any(c => c.CoreIndex < 0 || c.LogicalProcessorIndex < 0 || !double.IsFinite(c.TemperatureC) || c.TemperatureC is < 0 or > 125) ||
            s.CpuCoreTemperatures.Select(c => c.CoreIndex).Distinct().Count() != s.CpuCoreTemperatures.Count)
            issues.Add(new("cpu-cores-invalid", "Temperaturas o identidades de núcleos inválidas o duplicadas."));
        void Age(string code, string name, DateTimeOffset sampled, double maximumMs, bool strict)
        {
            var age = (now - sampled).TotalMilliseconds;
            if (age < 0) issues.Add(new(code + "-future", name + " tiene una fecha futura."));
            else if (strict ? age >= maximumMs : age > maximumMs)
                issues.Add(new(code + "-stale", $"{name} antigua: {Number(age)} ms; se requiere {(strict ? "<" : "≤")} {Number(maximumMs)} ms."));
        }
        Age("snapshot", "Muestra general", s.Timestamp, startup ? 3000 : s.MaximumControlAge.TotalMilliseconds, startup);
        Age("cpu-thermal", "Temperatura CPU", s.CpuThermalSampledAtUtc ?? s.Timestamp, s.MaximumControlAge.TotalMilliseconds, s.ProductTelemetryTolerance);
        Age("gpu-thermal", "Temperatura GPU", s.GpuThermalSampledAtUtc ?? s.Timestamp, s.MaximumControlAge.TotalMilliseconds, s.ProductTelemetryTolerance);
        Age("cpu-power", "Potencia CPU", s.CpuPowerSampledAtUtc ?? s.Timestamp, s.MaximumControlAge.TotalMilliseconds, s.ProductTelemetryTolerance);
        Age("gpu-power", "Potencia GPU", s.GpuPowerSampledAtUtc ?? s.Timestamp, s.MaximumControlAge.TotalMilliseconds, s.ProductTelemetryTolerance);
        if (s.RetainedTelemetry is not null) issues.Add(new("retained-fields", "Campos críticos conservados: " + s.RetainedTelemetry + ". Se requieren adquisiciones nuevas para entrar."));
        if (s.FanTelemetrySource == "HP-WMI-ACPI-2D")
        {
            var captured = s.FanAgeCapturedAtUtc ?? s.Timestamp;
            if (s.FanSampleAgeMilliseconds is not (>= 0) || !s.FanSampledAtUtc.HasValue || s.FanRpmResolution != HpWmiFanTelemetrySample.ResolutionRpm)
                issues.Add(new("fan-metadata", "RPM sin metadata válida de adquisición WMI (fecha, antigüedad o resolución)."));
            else if (captured < s.Timestamp || now < captured)
                issues.Add(new("fan-clock", "Fechas de adquisición RPM incoherentes o futuras."));
            else
            {
                var age = s.FanSampleAgeMilliseconds.Value + (now - captured).TotalMilliseconds;
                var maximum = Math.Min(3000, s.FanMaximumAgeMilliseconds);
                if (age >= maximum) issues.Add(new("fan-stale", $"RPM antiguas para entrar: {Number(age)} ms; se requiere < {maximum} ms (el margen en marcha es {s.FanMaximumAgeMilliseconds} ms)."));
            }
        }
        return issues;
    }
    internal static IReadOnlyList<ProductAutomaticAdmissionIssue> StartupIssues(ProductRuntimeState state, ProductProtectionSettings protections, DateTimeOffset now)
    {
        var issues = TelemetryIssues(state.Snapshot, now, startup: true).ToList();
        if (!ProductRelease.IsAutomaticAuthorized(state.Target) || !state.AutomaticAuthorized) issues.Add(new("target", "Automático no está autorizado para el equipo detectado."));
        if (state.Runtime != "Healthy") issues.Add(new("runtime", "Estado del controlador: " + state.Runtime + "; se requiere Healthy."));
        if (state.Source is not ("Ac" or "Battery")) issues.Add(new("source", "Fuente de energía sin confirmar: " + state.Source + "."));
        if (!state.CanApplyPerformance) issues.Add(new("performance", "CPU/GPU no están disponibles para preparar sus límites; revisa el estado del controlador."));
        if (state.Snapshot is { } s)
        {
            void Limit(string code, string name, double? value, double max, string unit)
            {
                if (value.HasValue && double.IsFinite(value.Value) && value > max)
                    issues.Add(new(code, $"{name} {Number(value.Value)} {unit} > {Number(max)} {unit}, límite de entrada."));
            }
            Limit("cpu-entry-temperature", "Temperatura CPU de control", s.CpuControlTemperatureC, protections.CpuThermalHandoff ? 90 : 110, "°C");
            Limit("gpu-entry-temperature", "Temperatura GPU", s.GpuTemperatureC, protections.GpuThermalHandoff ? 82 : 105, "°C");
            if (protections.PowerEnvelopeHandoff) Limit("cpu-entry-power", "Potencia CPU", s.CpuPackagePowerW, 60, "W");
        }
        return issues;
    }
    internal static IReadOnlyList<ProductAutomaticAdmissionIssue> RuntimeIssues(TelemetrySnapshot? snapshot, DateTimeOffset now, SafetyGateResult admission, ProductProtectionSettings? protections = null, string? runtime = null)
    {
        var issues = TelemetryIssues(snapshot, now).ToList();
        if (!admission.BoardAllowed) issues.Add(new("target", "La identidad del equipo no coincide con el destino autorizado."));
        if (!admission.RuntimeHealthy) issues.Add(new("runtime", "Estado del controlador: " + (runtime ?? "sin confirmar") + "; se requiere Healthy."));
        if (!admission.TelemetryDeviceIdentityValid) issues.Add(new("gpu-identity", "Identidad GPU sin confirmar: " + (snapshot?.GpuName ?? "sin lectura") + "."));
        if (!admission.FanWritePathPresent) issues.Add(new("fan-backend", "No hay una ruta de control y liberación de ventiladores disponible."));
        protections ??= new();
        if (admission.ThermalEmergency)
        {
            if(protections.CpuThermalHandoff && snapshot?.CpuControlTemperatureC >= SafetyGate.CpuEmergencyC)
                issues.Add(new("cpu-thermal-handoff", $"Temperatura CPU de control {snapshot.CpuControlTemperatureC:0.##} °C ≥95 °C; protección térmica activa."));
            if(protections.GpuThermalHandoff && snapshot?.GpuTemperatureC >= SafetyGate.GpuEmergencyC)
                issues.Add(new("gpu-thermal-handoff", $"Temperatura GPU {snapshot.GpuTemperatureC:0.##} °C ≥87 °C; protección térmica activa."));
        }
        if (!admission.CustomControlPermitted && issues.Count == 0)
            issues.Add(new("control-gate", "La comprobación de control rechazó la entrada: " + string.Join("; ", admission.Reasons)));
        return issues;
    }
}
