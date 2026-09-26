using VictusFanControl.Control;

namespace VictusFanControl.App;

/// <summary>
/// Gate F2-only watchdog client wrapper. Every production lease operation is
/// forwarded unchanged except Commit: once the HP backend has already completed
/// the real WMI write plus EC and dual-tach acknowledgement, CommitAsync is the
/// next call. F2 durably records that exact pre-Commit boundary and deliberately
/// holds the controller there until the external harness force-kills it.
///
/// This type is reachable only through the explicit Gate F2 application mode.
/// It does not alter the production HP backend transaction ordering.
/// </summary>
internal sealed class GateF2CommitHoldWatchdogLeaseClient :
    IFanControlWatchdogLeaseClient
{
    private readonly IFanControlWatchdogLeaseClient _inner;
    private readonly string _readyPath;
    private readonly int _expectedCpu;
    private readonly int _expectedGpu;
    private int _commitHeld;

    public GateF2CommitHoldWatchdogLeaseClient(
        IFanControlWatchdogLeaseClient inner,
        string readyPath,
        int expectedCpu,
        int expectedGpu)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _readyPath = Path.GetFullPath(
            readyPath ?? throw new ArgumentNullException(nameof(readyPath)));
        _expectedCpu = expectedCpu;
        _expectedGpu = expectedGpu;
    }

    public ValueTask PrepareAsync(
        CancellationToken cancellationToken) =>
        _inner.PrepareAsync(cancellationToken);

    public ValueTask CancelPreparedAsync(
        CancellationToken cancellationToken) =>
        _inner.CancelPreparedAsync(cancellationToken);

    public ValueTask WriteIntentAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken) =>
        _inner.WriteIntentAsync(
            cpuLevel,
            gpuLevel,
            cancellationToken);

    public ValueTask AbortWriteIntentAsync(
        CancellationToken cancellationToken) =>
        _inner.AbortWriteIntentAsync(cancellationToken);

    public async ValueTask CommitAsync(
        int cpuLevel,
        int gpuLevel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (cpuLevel != _expectedCpu ||
            gpuLevel != _expectedGpu)
        {
            throw new InvalidOperationException(
                $"Gate F2 Commit hold expected {_expectedCpu}/{_expectedGpu}, " +
                $"received {cpuLevel}/{gpuLevel}.");
        }

        if (Interlocked.Exchange(ref _commitHeld, 1) != 0)
        {
            throw new InvalidOperationException(
                "Gate F2 Commit hold was entered more than once.");
        }

        var directory =
            Path.GetDirectoryName(_readyPath) ??
            throw new InvalidOperationException(
                "Gate F2 READY marker has no parent directory.");

        Directory.CreateDirectory(directory);

        var marker =
            $"READY|{DateTimeOffset.Now:O}|phase=WriteArmed|" +
            $"cpu={cpuLevel}|gpu={gpuLevel}|" +
            $"pid={Environment.ProcessId}|" +
            "ack=backend-ec+tachs-before-commit";

        await using (var stream = new FileStream(
            _readyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough))
        await using (var writer = new StreamWriter(stream))
        {
            await writer.WriteAsync(marker).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // Intentionally never forward Commit. The watchdog remains durably
        // WRITE_ARMED while the external harness destroys both original
        // processes. Ignore cancellation after the marker: allowing the live
        // controller to unwind could execute its normal local restore and would
        // destroy the double-failure causality that F2 is designed to prove.
        await Task.Delay(
            Timeout.InfiniteTimeSpan,
            CancellationToken.None).ConfigureAwait(false);
    }

    public ValueTask ProbeAsync(
        CancellationToken cancellationToken) =>
        _inner.ProbeAsync(cancellationToken);

    public ValueTask HeartbeatAsync(
        CancellationToken cancellationToken) =>
        _inner.HeartbeatAsync(cancellationToken);

    public ValueTask RestoreBeginAsync(
        CancellationToken cancellationToken) =>
        _inner.RestoreBeginAsync(cancellationToken);

    public ValueTask ReleaseAsync(
        CancellationToken cancellationToken) =>
        _inner.ReleaseAsync(cancellationToken);

    public ValueTask DisposeAsync() =>
        _inner.DisposeAsync();
}
