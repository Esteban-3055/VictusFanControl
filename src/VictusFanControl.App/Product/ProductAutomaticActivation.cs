using VictusFanControl.Product;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

/// <summary>Freezes the explicit click and fences a late fan activation after Firmware/lifecycle cancellation.</summary>
internal sealed class ProductAutomaticActivation
{
    internal sealed record Ticket(long Generation, ProductProfiles Profiles)
    {
        internal PerformanceGuiSessionConfiguration Performance => Profiles.PerformanceConfiguration();
    }
    private readonly object _sync = new();
    private long _generation;
    private long? _pending;
    internal bool Pending { get { lock (_sync) return _pending.HasValue; } }
    internal Ticket Begin(ProductProfiles profiles)
    {
        var frozen = ProductProfilesStore.Copy(profiles with { CpuEnabled = true, GpuEnabled = true });
        frozen.PerformanceConfiguration().Validate();
        lock (_sync)
        {
            if (_pending.HasValue) throw new InvalidOperationException("Automatic ya se está preparando.");
            var ticket = new Ticket(++_generation, frozen); _pending = ticket.Generation; return ticket;
        }
    }
    internal void Cancel() { lock (_sync) { ++_generation; _pending = null; } }
    internal bool IsCurrent(Ticket ticket) { lock (_sync) return ticket.Generation == _generation; }
    internal void EnsureCurrent(Ticket ticket)
    {
        lock (_sync)
            if (ticket.Generation != _generation) throw new InvalidOperationException("Activación Automatic cancelada; no se inicia el control de la curva.");
    }
    internal async Task RunAsync(Ticket ticket, Func<Task> admit, Func<Task> performance, Func<Task> fans)
    {
        try
        {
            EnsureCurrent(ticket); await admit().ConfigureAwait(false); EnsureCurrent(ticket);
            await performance().ConfigureAwait(false); EnsureCurrent(ticket); await fans().ConfigureAwait(false);
        }
        finally { lock (_sync) if (_pending == ticket.Generation) _pending = null; }
    }
    internal static async Task PreparePerformanceAsync(PerformanceGuiSessionConfiguration expected, bool hasProcess,
        PerformanceGuiSessionConfiguration? applied, Func<Task> refreshStatus, Func<Task> enable)
    {
        if (hasProcess)
        {
            if (applied != expected) throw new InvalidOperationException("Libera CPU/GPU antes de activar Automatic con límites diferentes o una sesión parcial.");
            await refreshStatus().ConfigureAwait(false);
        }
        else await enable().ConfigureAwait(false);
    }
    internal async Task ReleaseLimitsAsync(Func<Task> restoreFans, Func<Task> releaseLimits)
    {
        Cancel(); await restoreFans().ConfigureAwait(false); await releaseLimits().ConfigureAwait(false);
    }
    internal static bool PerformanceReady(PerformanceGuiSessionConfiguration expected,
        PerformanceGuiSessionConfiguration? applied, PerformanceGuardianResponse? status, bool fresh, string source) =>
        expected.CpuEnabled && expected.GpuEnabled && applied == expected && fresh && source is "Ac" or "Battery" &&
        status is { Ok: true, SessionEnabled: true, CpuEnabled: true, GpuEnabled: true,
            CpuState: "Active", GpuState: "ActiveUnverified", RuntimeFailure: null } && status.PowerSource == source;
}

/// <summary>Bounded source reconciliation. A cached pre-change response cannot authorize a handoff.</summary>
internal sealed class ProductAutomaticSourceTransition
{
    internal const int MaximumMilliseconds = 4000;
    private readonly Func<long> _clock;
    private long? _started;
    private long _candidateSince;
    private string? _candidate;
    private Guid? _previousResponse;
    private int _samples;
    internal bool Pending => _started.HasValue;
    internal string? Candidate => _candidate;
    internal ProductAutomaticSourceTransition(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);
    internal void Reset() { _started = null; _candidate = null; _previousResponse = null; _samples = 0; }
    internal bool Observe(string source, string selected, PerformanceGuiSessionConfiguration expected,
        PerformanceGuiSessionConfiguration? applied, PerformanceGuardianResponse? status, bool fresh)
    {
        if (source is not ("Ac" or "Battery") || !expected.CpuEnabled || !expected.GpuEnabled || !fresh || applied != expected ||
            status is not { Ok: true, SessionEnabled: true, CpuEnabled: true, GpuEnabled: true,
                CpuState: "Active", GpuState: "ActiveUnverified", RuntimeFailure: null })
            throw new InvalidOperationException("Transición Automatic sin fuente válida o confirmación vigente de CPU/GPU; volver a Firmware.");
        var now = _clock();
        if (!_started.HasValue && source == selected && ProductAutomaticActivation.PerformanceReady(expected, applied, status, fresh, source)) return true;
        if (!_started.HasValue) { _started = now; _previousResponse = status.RequestId; }
        if (now < _started.Value || now - _started.Value >= MaximumMilliseconds)
            throw new InvalidOperationException("Transición AC/Batería no confirmada en cuatro segundos; volver a Firmware.");
        if (_candidate != source) { _candidate = source; _candidateSince = now; _samples = 0; }
        ++_samples;
        return _samples >= 2 && now - _candidateSince >= 1000 && status.RequestId != _previousResponse &&
            ProductAutomaticActivation.PerformanceReady(expected, applied, status, fresh, source);
    }
}
