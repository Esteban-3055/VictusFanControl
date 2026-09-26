using VictusFanControl.Control;

namespace VictusFanControl.Watchdog;

internal static class GateCProtocol
{
    public const int Version =
        FanControlWatchdogLeaseContract.ProtocolVersion;

    public const string Hello =
        FanControlWatchdogLeaseContract.Hello;

    public const string Prepare =
        FanControlWatchdogLeaseContract.Prepare;

    public const string CancelPrepared =
        FanControlWatchdogLeaseContract.CancelPrepared;

    public const string WriteIntent =
        FanControlWatchdogLeaseContract.WriteIntent;

    public const string AbortWriteIntent =
        FanControlWatchdogLeaseContract.AbortWriteIntent;

    public const string Commit =
        FanControlWatchdogLeaseContract.Commit;

    public const string Probe =
        FanControlWatchdogLeaseContract.Probe;

    public const string Heartbeat =
        FanControlWatchdogLeaseContract.Heartbeat;

    public const string RestoreBegin =
        FanControlWatchdogLeaseContract.RestoreBegin;

    public const string Release =
        FanControlWatchdogLeaseContract.Release;
}
