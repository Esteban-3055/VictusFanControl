using System.ServiceProcess;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;

namespace VictusFanControl.App;

internal readonly record struct Hp8C40WatchdogBootstrapEvidence(
    int ProcessId,
    long ProcessStartUtcTicks,
    bool StartedByApplication,
    string Detail);

/// <summary>
/// Last-mile service bootstrap for the already-qualified HP 8C40 M4 watchdog.
///
/// This class never installs, deletes, reconfigures or writes fan state. It may
/// only start the existing Manual service after an explicit production/M9D
/// authorization gate, then requires the target-bound Ready marker, live
/// Session-0 LocalSystem PID+creation time and an absent durable journal.
/// </summary>
internal static class Hp8C40WatchdogServiceBootstrap
{
    internal const string ServiceName = "VictusFanControlWatchdogM4";

    private static readonly TimeSpan ServiceStartTimeout =
        TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ReadyTimeout =
        TimeSpan.FromSeconds(20);

    public static Hp8C40WatchdogBootstrapEvidence EnsureProductionReady(
        HardwareIdentity hardware)
    {
        Hp8C40ProductionWatchdogGate
            .RequireProductionConstructionAuthorized(hardware);

        return EnsureReadyCore("M9 production");
    }

    public static Hp8C40WatchdogBootstrapEvidence EnsureM9DQualificationReady(
        HardwareIdentity hardware)
    {
        ArgumentNullException.ThrowIfNull(hardware);

        if (!Hp8C40TargetProfile.Matches(
                hardware,
                out var targetReason))
        {
            throw new NotSupportedException(
                $"M9D watchdog bootstrap exact-target refusal: {targetReason}");
        }

        if (!Hp8C40M9DProductionLifecycleQualificationTest
                .PhysicalExecutionAuthorized ||
            !Hp8C40ProductionWatchdogGate
                .M9DPhysicalQualificationConstructionAuthorized)
        {
            throw new NotSupportedException(
                "M9D watchdog bootstrap is blocked by the physical/construction gates.");
        }

        if (Hp8C40TargetProfile.Instance.WatchdogRecoveryValidated ||
            Hp8C40ProductionWatchdogGate.ProductionConstructionAuthorized)
        {
            throw new InvalidOperationException(
                "M9D qualification bootstrap is forbidden after production promotion.");
        }

        return EnsureReadyCore("M9D qualification");
    }

    private static Hp8C40WatchdogBootstrapEvidence EnsureReadyCore(
        string scope)
    {
        using var service =
            new ServiceController(ServiceName);

        ServiceControllerStatus initialStatus;
        ServiceStartMode startType;

        try
        {
            service.Refresh();
            initialStatus = service.Status;
            startType = service.StartType;
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"{scope} requires the already-installed {ServiceName} service.",
                ex);
        }

        if (startType != ServiceStartMode.Manual)
        {
            throw new InvalidOperationException(
                $"{scope} requires {ServiceName} startup type Manual; observed {startType}.");
        }

        var startedByApplication = false;

        switch (initialStatus)
        {
            case ServiceControllerStatus.Stopped:
                service.Start();
                startedByApplication = true;
                service.WaitForStatus(
                    ServiceControllerStatus.Running,
                    ServiceStartTimeout);
                break;

            case ServiceControllerStatus.StartPending:
                service.WaitForStatus(
                    ServiceControllerStatus.Running,
                    ServiceStartTimeout);
                break;

            case ServiceControllerStatus.Running:
                break;

            default:
                throw new InvalidOperationException(
                    $"{scope} refuses watchdog service state {initialStatus}; expected Stopped/StartPending/Running.");
        }

        var deadline =
            DateTimeOffset.UtcNow +
            ReadyTimeout;

        Exception? lastFailure = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var snapshot =
                    M6WatchdogStateReader.Read();

                M6WatchdogStateReader.RequireReady(snapshot);

                if (snapshot.JournalPresent)
                {
                    throw new InvalidOperationException(
                        $"{scope} watchdog reached Ready with retained journal '{snapshot.JournalPath}'.");
                }

                return new Hp8C40WatchdogBootstrapEvidence(
                    snapshot.ProcessId,
                    snapshot.ProcessStartUtcTicks,
                    startedByApplication,
                    $"{scope}: {ServiceName} Ready, Session 0/LocalSystem, target-bound, journal absent.");
            }
            catch (Exception ex)
                when (ex is IOException or
                      InvalidOperationException or
                      UnauthorizedAccessException)
            {
                lastFailure = ex;
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException(
            $"{scope} watchdog did not reach exact Ready/journal-absent state within {ReadyTimeout.TotalSeconds:0} s.",
            lastFailure);
    }
}
