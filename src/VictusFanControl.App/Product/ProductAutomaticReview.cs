using VictusFanControl.Control.Adaptive;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

/// <summary>Explicit physical review admission; never changes the normal Automatic gate.</summary>
internal sealed class ProductAutomaticReview
{
    internal const int MaximumSeconds = 300;
    private readonly Func<long> _milliseconds;
    private readonly object _sync = new();
    private long? _started;
    private DateTimeOffset? _lastSample;
    private int _healthySamples;
    internal ProductAutomaticReview(Func<long>? milliseconds = null) => _milliseconds = milliseconds ?? (() => Environment.TickCount64);
    internal static bool IsAuthorized(bool requested, string? target) => requested && Hp8C40AutomaticFinalQualificationGate.IsAuthorizedForTarget(target);
    internal void Start() { lock (_sync) { _started = _milliseconds(); _lastSample = null; _healthySamples = 0; } }
    internal void Stop() { lock (_sync) _started = null; }
    internal int? RemainingSeconds { get { lock (_sync) return _started.HasValue ? (int)Math.Clamp((MaximumSeconds * 1000L - (_milliseconds() - _started.Value) + 999) / 1000, 0, MaximumSeconds) : null; } }
    internal bool Expired { get { lock (_sync) return _started.HasValue && (_milliseconds() < _started.Value || _milliseconds() - _started.Value >= MaximumSeconds * 1000L); } }
    internal void EnsureDispatchAllowed(TelemetrySnapshot snapshot)
    {
        lock (_sync)
        {
            if (!_started.HasValue || Expired) throw new InvalidOperationException("Prueba Automatic finalizada; volver a Firmware.");
            static bool Within(double? value, double maximum) => value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 && value.Value <= maximum;
            if (!snapshot.IsComplete ||
                !Within(snapshot.CpuControlTemperatureC, Hp8C40AutomaticFinalQualificationGate.MaximumCpuPhysicalC) ||
                !Within(snapshot.GpuTemperatureC, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPhysicalC) ||
                !Within(snapshot.CpuPackagePowerW, Hp8C40AutomaticFinalQualificationGate.MaximumCpuPackagePowerW) ||
                !Within(snapshot.GpuPowerW, Hp8C40AutomaticFinalQualificationGate.MaximumGpuPowerW))
                throw new InvalidOperationException(EnvelopeFailure(snapshot));
        }
    }
    internal static string EnvelopeFailure(TelemetrySnapshot snapshot)
    {
        var failures = new List<string>();
        void Check(string name, double? value, double maximum, string unit)
        {
            if (!value.HasValue || !double.IsFinite(value.Value) || value.Value < 0) failures.Add(name + " no disponible o inválido");
            else if (value.Value > maximum) failures.Add($"{name} {value.Value:0.##} {unit} > {maximum:0.##} {unit}");
        }
        Check("CPU temperatura", snapshot.CpuControlTemperatureC, Hp8C40AutomaticFinalQualificationGate.MaximumCpuPhysicalC, "°C");
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
            EnsureDispatchAllowed(snapshot);
            if (!safety.CustomControlPermitted || safety.SnapshotTimestamp != snapshot.Timestamp || safety.EvaluationSequence <= 0)
                throw new InvalidOperationException("Prueba Automatic requiere SafetyGate vigente y Healthy.");
            if (_lastSample.HasValue && (snapshot.Timestamp <= _lastSample.Value || snapshot.Timestamp - _lastSample.Value > SafetyGate.MaximumTelemetryAge))
                throw new InvalidOperationException("Prueba Automatic recibió una adquisición repetida o discontinua.");
            _lastSample = snapshot.Timestamp;
            return ++_healthySamples >= Hp8C40AutomaticFinalQualificationGate.RequiredHealthyPreWriteSamples;
        }
    }
}
