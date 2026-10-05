using VictusFanControl.Control;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Hardware.Hp;

public sealed record FanWmiReleaseEvidence(bool ReleaseRequestAccepted,
    bool LegacyDefaultRequestAccepted, bool GuardianLeaseRetired,
    bool IndependentFirmwareOwnershipVerified, string ReportPath, string Reason);

internal interface IWmiFanGuiGuardian : IAsyncDisposable
{
    string SessionDirectory { get; }
    string ReportPath { get; }
    Task StartAsync(CancellationToken token);
    void EnsureAlive();
    void PersistIntent(int level);
    void Heartbeat();
    Task<FanWmiReleaseEvidence> ReleaseAsync(CancellationToken token);
}

/// <summary>
/// Exact 8C40 GUI path matching the supervised WMI experiment: setters are
/// accepted native requests, RPM is feedback, neither is EC ownership proof.
/// The detached guardian is armed durably before the first setter.
/// </summary>
internal sealed class Hp8C40WmiFanControlBackend : IFanControlBackend, IFanControlRestoreEvidenceSource
{
    private readonly IWmiFanGuiGuardian _guardian;
    private readonly Func<CancellationToken, ValueTask<HpWmiFanProofSample>> _read;
    private readonly Func<HpBiosRequest, int> _send;
    private WmiFanSession _session;
    private bool _active;
    private bool _releaseInProgress;
    private bool _reentryBlocked;
    internal FanWmiReleaseEvidence? ReleaseEvidence { get; private set; }
    internal string GuardianReportPath => _guardian.ReportPath;
    internal string GuardianSessionDirectory => _guardian.SessionDirectory;
    internal int? LastAcceptedLevel => _session.LastAcceptedLevel;
    public event EventHandler<string>? CommandAccepted;
    public string Name => "HP 8C40 WMI-only / supervised requests; hardware ownership unverified";
    public bool CanWrite => true;
    public FanBackendCapabilities Capabilities => new("8C40", Hp8C40TargetProfile.MinimumValidatedFanLevel, Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel, false);
    public FanFirmwareRestoreEvidence LastRestoreEvidence { get; private set; }

    internal Hp8C40WmiFanControlBackend(IWmiFanGuiGuardian guardian,
        Func<HpBiosRequest, int>? send = null,
        Func<CancellationToken, ValueTask<HpWmiFanProofSample>>? read = null)
    {
        _guardian = guardian;
        HpOmenBiosWmiClient? client = null;
        _send = send ?? (r => (client ??= new HpOmenBiosWmiClient()).Send(r));
        _session = new WmiFanSession(_send, guardian.PersistIntent, Capabilities.MinimumLevel);
        var fans = new HpWmiFanProofReader();
        _read = read ?? fans.ReadFreshAsync;
    }

    public ValueTask ProbeControlDependencyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_active) _guardian.EnsureAlive();
        return ValueTask.CompletedTask;
    }

    public async ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken token)
    {
        if (_active) _guardian.EnsureAlive();
        var sample = await _read(token).ConfigureAwait(false);
        if (_active) { _guardian.EnsureAlive(); _guardian.Heartbeat(); }
        return new(Name, true, _active, true, true,
            $"Fresh WMI RPM {sample.Speeds.CpuNominalRpm}/{sample.Speeds.GpuNominalRpm}; " +
            $"accepted target={_session.LastAcceptedLevel}; local supervised session valid; " +
            "setpoint readback=false; hardware ownership unverified; direct EC prohibited.");
    }

    public async ValueTask EnterCustomModeAsync(CancellationToken token)
    {
        if (_reentryBlocked)
            throw new FanControlAdmissionException("WMI session recovery is unresolved; re-entry remains blocked.");
        if (_releaseInProgress)
            throw new FanControlAdmissionException("WMI session release is still in progress.");
        if (_active) return;
        await _guardian.StartAsync(token).ConfigureAwait(false);
        _guardian.EnsureAlive();
        _guardian.Heartbeat();
        ReleaseEvidence = null;
        LastRestoreEvidence = default;
        _active = true; // Local admission only; no native setter yet.
    }

    public async ValueTask ApplyAsync(FanCommand command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!_active || _releaseInProgress || _reentryBlocked)
            throw new InvalidOperationException("WMI fan admission is closed.");
        if (command.CpuLevel != command.GpuLevel) throw new ArgumentException("WMI session requires equal fan levels.");
        _guardian.EnsureAlive();
        FanDispatchAdmissionScope.EnsureAllowed();
        // Native call remains serialized until actual completion. No repeated
        // setter, fabricated EC acknowledgement, or mechanical waiting loop.
        var sent = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            _guardian.EnsureAlive();
            FanDispatchAdmissionScope.EnsureAllowed();
            return _session.Apply(command.CpuLevel, admissionPermitted: true);
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _guardian.EnsureAlive();
        if (sent) CommandAccepted?.Invoke(this,
            $"target={command.CpuLevel}/{command.GpuLevel}; WMI return=0; " +
            "setpoint readback=false; hardware completion/ownership unverified");
    }

    public async ValueTask RestoreFirmwareAutoAsync(CancellationToken token)
    {
        if (!_active)
            return;
        if (_reentryBlocked)
            throw new InvalidOperationException("WMI session recovery is unresolved; release cannot be retried blindly.");

        _releaseInProgress = true;
        WmiFanExperimentBoundary.BeginRecovery();
        try
        {
            using var quiescence = CancellationTokenSource.CreateLinkedTokenSource(token);
            quiescence.CancelAfter(TimeSpan.FromSeconds(10));
            await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(quiescence.Token).ConfigureAwait(false);
            ReleaseEvidence = await _guardian.ReleaseAsync(token).ConfigureAwait(false);
        }
        catch (Exception guardianFailure)
        {
            _releaseInProgress = false;
            _reentryBlocked = true;
            if (!_session.MayHaveWritten) throw;
            // Match the experiment's local fallback after supervisor loss.
            // Never overlap a pending native RPM query/setter, never retire the
            // guardian lease or claim independent ownership from this fallback.
            try
            {
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(drain.Token).ConfigureAwait(false);
                await Task.Run(_session.Recover, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception localFailure)
            {
                throw new InvalidOperationException("WMI guardian release failed; local recovery also failed; lease retained: " +
                    localFailure.Message, guardianFailure);
            }
            throw new InvalidOperationException("Local WMI release/default requests accepted after guardian failure; " +
                "guardian lease retained and re-entry blocked: " + guardianFailure.Message, guardianFailure);
        }

        if (!ReleaseEvidence.GuardianLeaseRetired ||
            (_session.MayHaveWritten && (!ReleaseEvidence.ReleaseRequestAccepted || !ReleaseEvidence.LegacyDefaultRequestAccepted)))
        {
            _releaseInProgress = false;
            _reentryBlocked = true;
            throw new InvalidOperationException("WMI guardian release is incomplete; retain fail-closed re-entry block.");
        }

        _active = false;
        _session = new WmiFanSession(_send, _guardian.PersistIntent, Capabilities.MinimumLevel);
        _releaseInProgress = false;
        _reentryBlocked = false;
        LastRestoreEvidence = new(false, true, true, DateTimeOffset.UtcNow,
            "Guardian release requests accepted; independent firmware ownership unverified (WMI-only); a new supervised session may be armed later.");
    }

    public async ValueTask DisposeAsync()
    {
        await RestoreFirmwareAutoAsync(CancellationToken.None).ConfigureAwait(false);
        await _guardian.DisposeAsync().ConfigureAwait(false);
    }
}
