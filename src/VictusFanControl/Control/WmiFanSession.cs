using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Control;

internal enum WmiFanSessionPhase { Normal, Recovering, ReleaseAccepted, RecoveryFailed }

/// <summary>
/// Experimental command lifecycle. WMI acceptance is never EC readback or
/// proof of firmware ownership. Recovery permanently closes normal writes.
/// </summary>
internal sealed class WmiFanSession
{
    private readonly Func<HpBiosRequest, int> _send;
    private readonly Action<int> _persistIntent;
    private readonly int _minimumLevel;
    internal WmiFanSessionPhase Phase { get; private set; }
    internal int? LastAcceptedLevel { get; private set; }
    internal bool MayHaveWritten { get; private set; }

    internal WmiFanSession(Func<HpBiosRequest, int> send, Action<int> persistIntent, int minimumLevel = 30)
    {
        if (minimumLevel is not (10 or 30)) throw new ArgumentOutOfRangeException(nameof(minimumLevel));
        _minimumLevel = minimumLevel;
        _send = send;
        _persistIntent = persistIntent;
    }

    internal bool Apply(int level, bool admissionPermitted)
    {
        if (Phase != WmiFanSessionPhase.Normal || !admissionPermitted)
            throw new InvalidOperationException("Normal WMI fan command admission is closed.");
        // The supervised experiment keeps 30..50; the exact-target GUI uses
        // the previously characterized equal Manual range 10..50.
        if (level < _minimumLevel || level > 50) throw new ArgumentOutOfRangeException(nameof(level));
        if (LastAcceptedLevel == level) return false;
        _persistIntent(level); // Failure here prevents hardware dispatch.
        MayHaveWritten = true; // Even a failed WMI call may have changed hardware.
        try
        {
            var rc = _send(Hp8C40BiosFanControl.BuildSetFanLevelRequest((byte)level, (byte)level));
            if (rc != 0) throw new HpBiosCallException($"WMI fan command rejected: rc={rc}.");
            LastAcceptedLevel = level;
            return true;
        }
        catch
        {
            Phase = WmiFanSessionPhase.Recovering;
            throw;
        }
    }

    internal void Recover()
    {
        if (Phase == WmiFanSessionPhase.ReleaseAccepted) return;
        Phase = WmiFanSessionPhase.Recovering;
        LastAcceptedLevel = null;
        try
        {
            Hp8C40BiosFanControl.ExecuteRestoreSequence(
                () => SendRecovery(Hp8C40BiosFanControl.BuildReleaseFanLevelRequest()),
                () => SendRecovery(Hp8C40BiosFanControl.BuildLegacyDefaultRequest()));
            Phase = WmiFanSessionPhase.ReleaseAccepted;
        }
        catch { Phase = WmiFanSessionPhase.RecoveryFailed; throw; }
    }

    private void SendRecovery(HpBiosRequest request)
    {
        var rc = _send(request);
        if (rc != 0) throw new HpBiosCallException($"WMI release request rejected: rc={rc}.");
    }
}
