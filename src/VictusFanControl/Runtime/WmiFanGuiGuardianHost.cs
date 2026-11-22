using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using VictusFanControl.Control;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Performance;

namespace VictusFanControl.Runtime;

internal static class WmiFanGuiGuardianHost
{
    private static readonly TimeSpan CtgpObservationBudget = TimeSpan.FromSeconds(5);

    public static void RequestStop(string directory, string reason)
    {
        var path = Path.Combine(directory, "stop.signal");
        if (File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(reason); stream.Write(bytes); stream.Flush(true);
        }
        catch (IOException) when (File.Exists(path)) { /* An existing durable fence is already a stop request. */ }
    }

    internal static string LeasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl", "WmiFanGui", "lease.json");
    internal static string LegacyLeasePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VictusFanControl", "WatchdogM4", "state", "lease.json");

    internal static async Task<int> RunAsync(string[] args, bool fixture = false, Action? fixtureBeforeObservation = null,Action? fixtureCtgpQualification=null)
    {
        // Separate explicit fixture entry point never initializes hardware.
        if (!fixture && (fixtureBeforeObservation is not null||fixtureCtgpQualification is not null)) throw new ArgumentException("Injection requires fixture mode.");
        if (args.Length != 7 || args[1] != "--session-dir" || args[3] != "--owner-pid" || args[5] != "--owner-start")
            throw new ArgumentException("Invalid WMI GUI guardian arguments.");
        var directory = Path.GetFullPath(args[2]);
        var ownerPid = int.Parse(args[4]);
        var ownerStart = long.Parse(args[6]);
        using var owner = Process.GetProcessById(ownerPid);
        if (owner.HasExited || owner.StartTime.ToUniversalTime().Ticks != ownerStart)
            throw new InvalidOperationException("WMI guardian owner identity mismatch.");
        if (!fixture && HpHardwareTargetResolver.Resolve(HardwareIdentityReader.ReadCurrent(), out _) != Hp8C40TargetProfile.Instance)
            throw new InvalidOperationException("WMI guardian requires exact HP 8C40/F.18.");
        var leasePath = fixture ? Path.Combine(directory, "fixture-lease.json") : LeasePath;
        if (!fixture && (File.Exists(LegacyLeasePath) || File.Exists(WmiFanExperiment.LeasePath)))
            throw new InvalidOperationException("Pending legacy/experimental fan lease blocks WMI GUI control.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.GetDirectoryName(leasePath)!);
        if (!fixture) { WmiFanExperimentBoundary.Enable(directory, true); WmiFanExperimentBoundary.BeginRecovery(); }
        FileStream? lease = null;
        var retired = false;
        var releaseAccepted = false;
        var legacyAccepted = false;
        string? failure = null;
        string? supervisionFailure = null;
        CtgpStatus? supervisionCtgp = null;
        string? ctgpStatusPublicationFailure = null;
        var reason = "STARTUP_FAILURE";
        var baseline = 0L;
        var clock = Stopwatch.StartNew();
        var monitor = new WmiFanHeartbeatMonitor(ownerPid);
        var timing = new WmiFanGuardianTiming();
        void CheckLifecycle()
        {
            timing.Check(clock.Elapsed, WmiFanGuardianClockSample.Read());
            if (timing.Status.StopReason is { } stop)
            {
                reason = stop;
                throw new InvalidOperationException("Supervisión WMI interrumpida: " + stop);
            }
        }
        HpOmenBiosWmiClient? transport = null;
        byte[] fixturePolicy = [0, 1, 1, 0];
        var fixtureRequests=fixture?new List<HpBiosRequest>():null;
        bool Ac() => fixture || new WindowsPerformancePowerSourceReader().Read().Source == PerformancePowerSourceKind.Ac;
        var ctgp = new Hp8C40CtgpControl(r =>
        {
            if (fixture)
            {
                fixtureRequests!.Add(r);
                if (r.CommandType == 0x22) fixturePolicy = r.Payload.ToArray();
                var data = r.CommandType == 0x28 ? new byte[128] : fixturePolicy.ToArray();
                if (r.CommandType == 0x28) data[0] = 200;
                return new(0, data);
            }
            using var scope = new Hp8C40CtgpRequestScope(() =>
            {
                CheckLifecycle(); // Includes suspend during qualification or native-slot admission.
                owner.Refresh();
                monitor.Observe(Path.Combine(directory, "heartbeat.json"), clock.Elapsed);
                if (owner.HasExited || monitor.TimedOut(clock.Elapsed) || !Ac() || File.Exists(Path.Combine(directory, "stop.signal")))
                    throw new InvalidOperationException("cTGP: AC, propietario o sesión dejaron de estar disponibles.");
            });
            return (transport ??= new HpOmenBiosWmiClient()).SendWithResponse(r);
        }, Ac, () => { if (!fixture) Hp8C40CtgpQualification.Verify();else fixtureCtgpQualification?.Invoke(); }, () =>
            WmiFanExperiment.WriteJson(Path.Combine(directory, "ctgp-intent.json"), new
            { OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart, Original = new byte[] { 0, 1, 1, 0 }, Utc = DateTimeOffset.UtcNow }));
        Guid lastCtgpRequest = Guid.Empty;
        Guid lastFanDomainRequest=Guid.Empty;
        var nextCtgpObservation = TimeSpan.Zero;
        var lastCtgpConfirmation = TimeSpan.Zero;
        void PublishCtgp()
        {
            // A failed presentation update neither refreshes the last status nor
            // changes whether hardware recovery is required or confirmed.
            try { WmiFanExperiment.WriteJson(Path.Combine(directory, "ctgp-status.json"), ctgp.Status); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { ctgpStatusPublicationFailure = ex.Message; }
        }
        try
        {
            // Durable and exclusive, retained on uncertain native completion.
            lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(lease, new { OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart,
                SessionDirectory = directory, GuardianPid = Environment.ProcessId, DirectEcProhibited = true });
            lease.Flush(true);
            if (!fixture) baseline = LatestSystemRecord();
            using var current = Process.GetCurrentProcess();
            WmiFanExperiment.WriteJson(Path.Combine(directory, "ready.json"), new
            {
                OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart,
                GuardianPid = current.Id, GuardianStartUtcTicks = current.StartTime.ToUniversalTime().Ticks,
                DirectEcProhibited = true, LeasePath = leasePath
            });
            reason = "SUPERVISION_FAILURE";
            timing.Check(clock.Elapsed, WmiFanGuardianClockSample.Read());
            while (true)
            {
                timing.BeginWork(clock.Elapsed, WmiFanGuardianClockSample.Read());
                var now = clock.Elapsed;
                if (File.Exists(Path.Combine(directory, "stop.signal"))) { reason = "CLIENT_RELEASE"; break; }
                owner.Refresh();
                if (owner.HasExited) { reason = "OWNER_EXITED"; break; }
                if (timing.Status.StopReason is { } stop) { reason = stop; break; }
                monitor.Observe(Path.Combine(directory, "heartbeat.json"), now);
                if (monitor.TimedOut(now)) { reason = "HEARTBEAT_EXPIRED"; break; }
                if (!fixture && HasAcpiFault(baseline)) { reason = "ACPI_13_OR_15"; break; }
                CheckLifecycle();
                if(CtgpPublication.TryRead<WmiFanDomainCommand>(Path.Combine(directory,"fan-domain-command.json")) is{} fanCommand&&fanCommand.Id!=lastFanDomainRequest)
                {
                    if(fanCommand.Id==Guid.Empty||fanCommand.FanSession==Guid.Empty||fanCommand.OwnerPid!=ownerPid||fanCommand.OwnerStartUtcTicks!=ownerStart)
                        throw new IOException("La orden de liberación de ventiladores no corresponde al propietario.");
                    lastFanDomainRequest=fanCommand.Id;
                    using(var slot=WmiFanExperimentBoundary.EnterRecoverySlot(directory,TimeSpan.FromSeconds(10),
                        mutexName:fixture?WmiFanExperimentBoundary.FixtureNativeMutexName(directory):null))
                    {
                        var fanAccepted=false;var defaultAccepted=false;
                        var intentPath=Path.Combine(directory,"write-intent.json");
                        if(File.Exists(intentPath))
                        {
                            using var intent=JsonDocument.Parse(File.ReadAllText(intentPath));var i=intent.RootElement;
                            if(i.GetProperty("OwnerPid").GetInt32()!=ownerPid||i.GetProperty("OwnerStartUtcTicks").GetInt64()!=ownerStart||
                                i.GetProperty("FanSession").GetGuid()!=fanCommand.FanSession)
                                throw new IOException("La intención de ventiladores cambió antes de su liberación.");
                            var recovery=new WmiFanSession(r=>
                            {
                                if(fixture)fixtureRequests!.Add(r);
                                var rc=fixture?0:(transport??=new HpOmenBiosWmiClient()).Send(r);
                                if(rc==0&&r.CommandType==0x2E)fanAccepted=true;
                                if(rc==0&&r.CommandType==0x1A)defaultAccepted=true;
                                return rc;
                            },_=>throw new InvalidOperationException("El supervisor solo puede liberar ventiladores."));
                            recovery.Recover();
                            if(!fanAccepted||!defaultAccepted)throw new IOException("HP no aceptó la liberación de ventiladores.");
                            // The accepted fan release must not be replayed if a
                            // subsequent independent policy observation fails.
                            File.Delete(intentPath);
                        }
                        if(File.Exists(Path.Combine(directory,"native-inflight.json"))||File.Exists(Path.Combine(directory,"native-uncertain.signal")))
                            throw new IOException("Resultado nativo incierto; no se acredita la liberación.");
                        ctgp.Observe();lastCtgpConfirmation=clock.Elapsed;PublishCtgp();
                        using var child=Process.GetCurrentProcess();
                        WmiFanExperiment.WriteJson(Path.Combine(directory,"fan-domain-reply.json"),new WmiFanDomainReply(1,fanCommand.Id,
                            fanCommand.FanSession,ownerPid,ownerStart,child.Id,child.StartTime.ToUniversalTime().Ticks,
                            Hp8C40TargetProfile.Instance.Id,directory,true,fanAccepted,defaultAccepted,true,ctgp.Status,DateTimeOffset.UtcNow));
                    }
                }
                var commandPath = Path.Combine(directory, "ctgp-command.json");
                if (CtgpPublication.TryRead<CtgpCommand>(commandPath) is { } command)
                {
                    if (command.OwnerPid != ownerPid || command.OwnerStartUtcTicks != ownerStart || command.Id == Guid.Empty)
                        throw new InvalidDataException("Solicitud cTGP de otro propietario.");
                    if (command.Id != lastCtgpRequest)
                    {
                        lastCtgpRequest = command.Id;
                        try { ctgp.Apply(command.Enabled); CheckLifecycle(); lastCtgpConfirmation = clock.Elapsed; PublishCtgp(); }
                        catch(WmiNativeSlotBusyException ex) when(!ctgp.NeedsRestore)
                        {ctgp.DeferActivation(ex);PublishCtgp();}
                        catch (Exception ex)
                        {
                            ctgp.Fail(ex); PublishCtgp();
                            // A refused qualification has not acquired cTGP. Only
                            // owned or uncertain native work closes the shared resource.
                            if(ctgp.NeedsRestore||File.Exists(Path.Combine(directory,"native-inflight.json"))||
                                File.Exists(Path.Combine(directory,"native-uncertain.signal")))throw;
                        }
                        WmiFanExperiment.WriteJson(Path.Combine(directory, "ctgp-reply.json"), new CtgpReply(command.Id, ctgp.Status));
                    }
                }
                // A due time cannot be skipped when a slow iteration misses a
                // narrow modulo window. Never replay missed observations.
                if (clock.Elapsed >= nextCtgpObservation)
                {
                    var observationStarted = clock.Elapsed;
                    var observationDeferred = false;
                    try
                    {
                        if (ctgp.NeedsRestore || fixtureBeforeObservation is not null)
                        {
                            var observation = Task.Run(() =>
                            {
                                fixtureBeforeObservation?.Invoke();
                                ctgp.Observe();
                            });
                            if (await Task.WhenAny(observation, Task.Delay(CtgpObservationBudget)).ConfigureAwait(false) != observation)
                            {
                                // Fence new control promptly. The observer still owns
                                // its native call and must drain before restoration.
                                reason = "CTGP_OBSERVATION_TIMEOUT";
                                try { RequestStop(directory, reason); }
                                finally { await observation.ConfigureAwait(false); }
                                break;
                            }
                            await observation.ConfigureAwait(false);
                        }
                        else ctgp.Observe();
                        CheckLifecycle();
                        lastCtgpConfirmation = clock.Elapsed;
                        PublishCtgp();
                    }
                    catch (CtgpObservationDeferredException ex)
                    {
                        CheckLifecycle();
                        if (ctgp.Status is not { State: "Active", Active: true, Managed: true, Failure: null } ||
                            !ctgp.Status.FreshAt(DateTimeOffset.UtcNow) || clock.Elapsed - lastCtgpConfirmation >= CtgpStatus.ConfirmationLifetime)
                        {
                            var expired = new CtgpObservationUnavailableException(ex);
                            ctgp.Fail(expired); PublishCtgp();
                            reason = "CTGP_OBSERVATION_UNAVAILABLE";
                            throw expired;
                        }
                        // Retry only an unsent observation, never a setter or an
                        // invocation with unknown completion. No freshness renewal.
                        observationDeferred = true;
                        PublishCtgp();
                    }
                    catch (Exception ex) { ctgp.Fail(ex); PublishCtgp(); throw; }
                    // Slow successful work consumes the interval. Do not append a
                    // second full idle interval to an already old observation.
                    nextCtgpObservation = observationDeferred ? clock.Elapsed + TimeSpan.FromMilliseconds(250) :
                        observationStarted + TimeSpan.FromSeconds(2);
                }
                // Work may legitimately include mutex admission and a slow,
                // completed WMI call. It is not the following idle interval.
                // The existing heartbeat deadline and cTGP freshness still apply.
                timing.CompleteWork(clock.Elapsed, WmiFanGuardianClockSample.Read());
                if (timing.Status.StopReason is { } completedStop) { reason = completedStop; break; }
                if (timing.Status.WorkMs > 1000)
                    Console.WriteLine("WMI GUARDIAN SLOW WORK: " + JsonSerializer.Serialize(timing.Status));
                // Include diagnostic output in work, never in idle-gap accounting.
                timing.CompleteWork(clock.Elapsed, WmiFanGuardianClockSample.Read());
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { supervisionFailure = ex.Message; supervisionCtgp = ctgp.Status; }
        finally
        {
            // Close the parent's whitelist before recovery. The shared named
            // mutex and durable in-flight marker prevent overlapping a live or
            // unknown native call; a timeout is never firmware cancellation.
            if (lease is not null)
            {
                try
                {
                    RequestStop(directory, reason);
                    using var nativeSlot = WmiFanExperimentBoundary.EnterRecoverySlot(directory, TimeSpan.FromSeconds(10),
                        mutexName: fixture ? WmiFanExperimentBoundary.FixtureNativeMutexName(directory) : null);
                    Exception? ctgpFailure = null;
                    try { ctgp.Restore(); }
                    catch (Exception ex) { ctgp.Fail(ex); ctgpFailure = ex; }
                    if (File.Exists(Path.Combine(directory, "write-intent.json")))
                    {
                        var recovery = new WmiFanSession(r =>
                        {
                            int rc;
                            if (fixture) {fixtureRequests!.Add(r);rc = 0;}
                            else rc = (transport ??= new HpOmenBiosWmiClient()).Send(r);
                            if (rc == 0 && r.CommandType == 0x2E) releaseAccepted = true;
                            if (rc == 0 && r.CommandType == 0x1A) legacyAccepted = true;
                            return rc;
                        }, _ => throw new InvalidOperationException("Guardian cannot send normal targets."));
                        recovery.Recover(); // Attempts LegacyDefault even after rejected FF/FF.
                    }
                    if (ctgpFailure is not null) throw new IOException("cTGP sin restauración confirmada; lease conservado.", ctgpFailure);
                    // Even without a setter, a pending RPM query cannot be
                    // mistaken for a completed native transaction.
                    if (File.Exists(Path.Combine(directory, "native-inflight.json")) ||
                        File.Exists(Path.Combine(directory, "native-uncertain.signal")))
                        throw new InvalidOperationException("Native completion is unknown; lease retained.");
                    lease.Dispose(); lease = null;
                    File.Delete(leasePath);
                    retired = true;
                }
                catch (Exception ex) { failure = ex.Message; }
                finally { lease?.Dispose(); }
                PublishCtgp();
            }
            using var guardian = Process.GetCurrentProcess();
            WmiFanExperiment.WriteJson(Path.Combine(directory, "guardian-report.json"), new
            {
                SchemaVersion = 2, TargetProfileId = Hp8C40TargetProfile.Instance.Id,
                GuardianPid = guardian.Id, GuardianStartUtcTicks = guardian.StartTime.ToUniversalTime().Ticks,
                SessionDirectory = directory, RecoveryCompleted = retired && failure is null && !ctgp.NeedsRestore,
                OwnerPid = ownerPid, OwnerStartUtcTicks = ownerStart, ExitReason = reason,
                ReleaseRequestAccepted = releaseAccepted, LegacyDefaultRequestAccepted = legacyAccepted,
                GuardianLeaseRetired = retired, IndependentFirmwareOwnershipVerified = false,
                DirectEcProhibited = true, Failure = failure, SupervisionFailure = supervisionFailure, SupervisionCtgp = supervisionCtgp
                , CtgpRestored = !ctgp.NeedsRestore, Ctgp = ctgp.Status, CtgpStatusPublicationFailure = ctgpStatusPublicationFailure, Timing = timing.Status,
                FixtureRequests=fixtureRequests
            });
        }
        return retired && failure is null ? 0 : 1;
    }

    private static long LatestSystemRecord()
    {
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName) { ReverseDirection = true });
        using var latest = reader.ReadEvent();
        return latest?.RecordId ?? throw new InvalidOperationException("System event cursor unavailable.");
    }
    private static bool HasAcpiFault(long baseline)
    {
        if (LatestSystemRecord() < baseline) throw new InvalidOperationException("System event log reset.");
        using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName,
            $"*[System[Provider[@Name='ACPI'] and (EventID=13 or EventID=15) and EventRecordID > {baseline}]]"));
        using var item = reader.ReadEvent();
        return item is not null;
    }
}
