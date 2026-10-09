using VictusFanControl.Control.Adaptive;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal enum ProductAutomaticReviewMode { Short, Extended, Habitual }

/// <summary>Explicit physical review admission; never changes the normal Automatic gate.</summary>
internal sealed class ProductAutomaticReview
{
    internal const int MaximumSeconds = 300;
    internal const int ExtendedMaximumSeconds = 2700;
    internal int MaximumDurationSeconds { get; }
    private readonly bool _bounded;
    internal const int MaximumCpuSpikeMilliseconds = 2000;
    internal const double CpuSpikeThresholdC = SafetyGate.CpuEmergencyC;
    internal const string CpuSpikeDeadlineFailure = "Confirmación de pico CPU vencida: sin adquisición fresca de recuperación <95 °C en 2000 ms; volver a Firmware.";
    internal const double CpuImmediateHandoffC = Hp8C40ThermalEmergencyConfirmation.CpuHardEmergencyC;
    private long? _cpuHighSince;
    private long? _lastClock;
    private TelemetrySnapshot? _observed;
    private readonly Func<long> _milliseconds;
    private readonly object _sync = new();
    private long? _started;
    private DateTimeOffset? _lastSample;
    private int _healthySamples;
    internal ProductAutomaticReview(Func<long>? milliseconds = null, ProductAutomaticReviewMode mode = ProductAutomaticReviewMode.Short)
    {
        _bounded=mode!=ProductAutomaticReviewMode.Habitual;
        MaximumDurationSeconds = mode switch
        {
            ProductAutomaticReviewMode.Short => MaximumSeconds,
            ProductAutomaticReviewMode.Extended => ExtendedMaximumSeconds,
            ProductAutomaticReviewMode.Habitual => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
        _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    }
    internal static ProductAutomaticReviewMode? ResolveEntry(bool shortRequested, bool extendedRequested, int otherHardwareModes)
    {
        if (shortRequested && extendedRequested || (shortRequested || extendedRequested) && otherHardwareModes != 0)
            throw new ArgumentException("Selecciona una sola revisión Automatic, sin combinarla con otra prueba de hardware.");
        return extendedRequested ? ProductAutomaticReviewMode.Extended : shortRequested ? ProductAutomaticReviewMode.Short : null;
    }
    internal static bool IsAuthorized(bool requested, string? target) => requested && Hp8C40AutomaticFinalQualificationGate.IsAuthorizedForTarget(target);
    internal void Start() { lock (_sync) { _started = _milliseconds(); _lastSample = null; _healthySamples = 0; _cpuHighSince = null; _lastClock = _started; _observed = null; } }
    internal void Stop() { lock (_sync) { _started = null; _cpuHighSince = null; _observed = null; } }
    internal int? RemainingSeconds { get { lock (_sync) return _bounded && _started.HasValue ? (int)Math.Clamp((MaximumDurationSeconds * 1000L - (_milliseconds() - _started.Value) + 999) / 1000, 0, MaximumDurationSeconds) : null; } }
    internal bool Expired { get { lock (_sync) return _bounded && _started.HasValue && (_milliseconds() < _started.Value || _milliseconds() - _started.Value >= MaximumDurationSeconds * 1000L); } }
    internal int? RemainingCpuSpikeMilliseconds
    {
        get { lock (_sync) return _started.HasValue && _cpuHighSince.HasValue
            ? (int)Math.Clamp(MaximumCpuSpikeMilliseconds - (_milliseconds() - _cpuHighSince.Value), 0, MaximumCpuSpikeMilliseconds) : null; }
    }
    internal static int? AcquisitionBudget(int? coreBudget, int? reviewBudget) =>
        coreBudget.HasValue && reviewBudget.HasValue ? Math.Min(coreBudget.Value, reviewBudget.Value) : coreBudget ?? reviewBudget;
    private void CheckCpuDeadline()
    {
        var now = _milliseconds();
        if (_lastClock.HasValue && now < _lastClock.Value)
            throw new InvalidOperationException("Prueba Automatic recibió un reloj regresivo.");
        _lastClock = now;
        if (_cpuHighSince.HasValue && now - _cpuHighSince.Value >= MaximumCpuSpikeMilliseconds)
            throw new InvalidOperationException(CpuSpikeDeadlineFailure);
    }
    internal void EnsureDispatchAllowed(TelemetrySnapshot snapshot)
    {
        lock (_sync)
        {
            if (!_started.HasValue || Expired) throw new InvalidOperationException("Prueba Automatic finalizada; volver a Firmware.");
            CheckCpuDeadline();
            static bool Within(double? value, double maximum) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= maximum;
            var cpuSpikeAdmitted = _healthySamples >= Hp8C40AutomaticFinalQualificationGate.RequiredHealthyPreWriteSamples &&
                ReferenceEquals(snapshot, _observed);
            if (!snapshot.IsComplete ||
                !Within(snapshot.CpuControlTemperatureC, 110) || snapshot.CpuControlTemperatureC >= CpuImmediateHandoffC ||
                (snapshot.CpuControlTemperatureC > Hp8C40AutomaticFinalQualificationGate.MaximumCpuPhysicalC &&
                    !cpuSpikeAdmitted) ||
                !Within(snapshot.GpuTemperatureC, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPhysicalC) ||
                !Within(snapshot.CpuPackagePowerW, Hp8C40AutomaticFinalQualificationGate.MaximumCpuPackagePowerW) ||
                !Within(snapshot.GpuPowerW, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPowerW))
                throw new InvalidOperationException(EnvelopeFailure(snapshot, cpuSpikeAdmitted));
        }
    }
    internal static string EnvelopeFailure(TelemetrySnapshot snapshot, bool cpuSpikeAdmitted = false)
    {
        var failures = new List<string>();
        void Check(string name, double? value, double maximum, string unit)
        {
            if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0) failures.Add(name + " no disponible o inválido");
            else if (value.Value > maximum) failures.Add($"{name} {value.Value:0.##} {unit} > {maximum:0.##} {unit}");
        }
        if (snapshot.CpuControlTemperatureC >= CpuImmediateHandoffC)
            failures.Add($"CPU temperatura de control {snapshot.CpuControlTemperatureC:0.##} °C >= {CpuImmediateHandoffC:0} °C " +
                $"(paquete {snapshot.CpuTemperatureC:0.##} °C; núcleo más caliente {snapshot.CpuCoreMaxTemperatureC:0.##} °C; retorno inmediato a Firmware)");
        else Check("CPU temperatura", snapshot.CpuControlTemperatureC,
            cpuSpikeAdmitted ? CpuImmediateHandoffC : Hp8C40AutomaticFinalQualificationGate.MaximumCpuPhysicalC, "°C");
        Check("CPU potencia", snapshot.CpuPackagePowerW, Hp8C40AutomaticFinalQualificationGate.MaximumCpuPackagePowerW, "W");
        Check("GPU temperatura", snapshot.GpuTemperatureC, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPhysicalC, "°C");
        Check("GPU potencia", snapshot.GpuPowerW, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPowerW, "W");
        if (!snapshot.IsComplete) failures.Add("telemetría incompleta");
        return "Prueba Automatic fuera de su margen: " + string.Join("; ", failures) + ".";
    }
    internal bool Observe(TelemetrySnapshot snapshot, SafetyGateResult safety)
    {
        lock (_sync)
        {
            // Only real acquisitions open/reset the spike window. Preview never counts
            // a sample; a late cool acquisition cannot rescue an expired window.
            CheckCpuDeadline();
            if (safety.SnapshotTimestamp != snapshot.Timestamp || safety.EvaluationSequence <= 0)
                throw new InvalidOperationException("Prueba Automatic requiere una evaluación de control vigente y de la misma adquisición.");
            if (!safety.CustomControlPermitted)
            {
                // Effective admission deliberately rejects a critical raw core
                // before the review envelope. Preserve that actual cause instead
                // of calling a Healthy runtime an unspecified SafetyGate fault.
                if (safety.ThermalEmergency && snapshot.CpuControlTemperatureC >= CpuImmediateHandoffC)
                    throw new InvalidOperationException(EnvelopeFailure(snapshot));
                var reasons = safety.Reasons.Count > 0 ? string.Join("; ", safety.Reasons) : "SafetyGate no permite control Custom.";
                throw new InvalidOperationException("Prueba Automatic interrumpida por SafetyGate: " + reasons);
            }
            if (_lastSample.HasValue && (snapshot.Timestamp <= _lastSample.Value || snapshot.Timestamp - _lastSample.Value > SafetyGate.MaximumTelemetryAge))
                throw new InvalidOperationException("Prueba Automatic recibió una adquisición repetida o discontinua.");
            var previous = _observed;
            _observed = snapshot;
            try { EnsureDispatchAllowed(snapshot); }
            catch { _observed = previous; throw; }
            // The <=90 C envelope is for startup, not a second emergency threshold.
            // Established control follows the same >=95 C confirmation as the core;
            // 90..94.x C keeps raw maximum cooling without starting a false deadline.
            if (snapshot.CpuControlTemperatureC >= CpuSpikeThresholdC)
                _cpuHighSince ??= _milliseconds();
            else _cpuHighSince = null;
            _lastSample = snapshot.Timestamp;
            return ++_healthySamples >= Hp8C40AutomaticFinalQualificationGate.RequiredHealthyPreWriteSamples;
        }
    }
}
