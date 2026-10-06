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
            EnsureCurrent(ticket); await admit(); EnsureCurrent(ticket);
            await performance(); EnsureCurrent(ticket); await fans();
        }
        finally { lock (_sync) if (_pending == ticket.Generation) _pending = null; }
    }
    internal static async Task PreparePerformanceAsync(PerformanceGuiSessionConfiguration expected, bool hasProcess,
        PerformanceGuiSessionConfiguration? applied, Func<Task> refreshStatus, Func<Task> enable)
    {
        if (hasProcess)
        {
            if (applied != expected) throw new InvalidOperationException("Libera CPU/GPU antes de activar Automatic con límites diferentes o una sesión parcial.");
            await refreshStatus();
        }
        else await enable();
    }
    internal async Task ReleaseLimitsAsync(Func<Task> restoreFans, Func<Task> releaseLimits)
    {
        Cancel(); await restoreFans(); await releaseLimits();
    }
    internal static bool PerformanceReady(PerformanceGuiSessionConfiguration expected,
        PerformanceGuiSessionConfiguration? applied, PerformanceGuardianResponse? status, bool fresh, string source) =>
        expected.CpuEnabled && expected.GpuEnabled && applied == expected && fresh && source is "Ac" or "Battery" &&
        status is { Ok: true, SessionEnabled: true, CpuEnabled: true, GpuEnabled: true,
            CpuState: "Active", GpuState: "ActiveUnverified", RuntimeFailure: null } && status.PowerSource == source;
}
