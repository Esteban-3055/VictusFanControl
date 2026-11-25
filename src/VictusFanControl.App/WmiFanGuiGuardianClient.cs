using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class WmiFanGuiGuardianClient : IWmiFanGuiGuardian
{
    private readonly bool _fixture;
    private readonly bool _productUpperRange;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Process? _child;
    private readonly int _ownerPid = Environment.ProcessId;
    private readonly long _ownerStart;
    private bool _released;
    private Guid _fanSession=Guid.NewGuid();
    private bool _fanSessionEntered,_fanDomainReleased;
    private FanWmiReleaseEvidence? _completedRelease;
    private readonly object _releaseCompletionSync=new();
    internal bool HasChild=>_child is not null;
    internal bool ChildExited=>_child?.HasExited==true;
    internal bool HasCompletedRelease=>_child is null&&_completedRelease?.GuardianLeaseRetired==true;
    internal FanWmiReleaseEvidence? CompletedRelease=>HasCompletedRelease?_completedRelease:null;
    internal bool RearmFixtureAfterCompletion {get;set;}
    internal bool Fixture=>_fixture;
    internal VictusFanControl.Recovery.ProductRecoveryRecord? RetainedFanLease(string? fixtureLease=null,string? fixtureCommon=null)
    {
        if(!_fixture&&(fixtureLease is not null||fixtureCommon is not null))throw new InvalidOperationException("Fixture inventory requires fixture mode.");
        if(_child is null)return null;
        EnsureAlive();
        if(_fanSessionEntered&&!_fanDomainReleased||
            File.Exists(Path.Combine(SessionDirectory,"write-intent.json"))||File.Exists(Path.Combine(SessionDirectory,"native-uncertain.signal")))
            throw new IOException("La sesión compartida no acredita la retirada de ventiladores.");
        var path=fixtureLease??LeasePath;
        var bytes=VictusFanControl.Recovery.ProductRecoveryInventory.ReadBytes(path);
        using var lease=JsonDocument.Parse(bytes);var r=lease.RootElement;
        if(r.GetProperty("OwnerPid").GetInt32()!=_ownerPid||r.GetProperty("OwnerStartUtcTicks").GetInt64()!=_ownerStart||
            r.GetProperty("GuardianPid").GetInt32()!=_child.Id||!r.GetProperty("DirectEcProhibited").GetBoolean()||
            !string.Equals(Path.GetFullPath(r.GetProperty("SessionDirectory").GetString()!),Path.GetFullPath(SessionDirectory),StringComparison.OrdinalIgnoreCase))
            throw new IOException("La sesión WMI retenida no coincide con su propietario y guardián.");
        // The handle was validated against READY/start ticks at startup and
        // remains open. An exited process or a changed lease cannot be reused.
        EnsureAlive();
        return new(path,"FanGui",VictusFanControl.Recovery.ProductRecoveryInventory.Hash(bytes));
    }
    private readonly SemaphoreSlim _ctgpCommands = new(1, 1);
    private readonly object _heartbeatPublication = new();
    private readonly object _ctgpPublication = new();
    private volatile CtgpStatus? _lastCtgpStatus;
    private string? _ctgpReadFailure;
    internal CtgpStatus? Ctgp
    {
        get
        {
            lock (_ctgpPublication)
            {
                CtgpStatus? status = null;
                var finalFailure = false;
                try
                {
                    status = ReadExitedCtgpFailure();
                    finalFailure = status is not null;
                    status ??= CtgpPublication.TryRead<CtgpStatus>(Path.Combine(SessionDirectory, "ctgp-status.json"));
                    _ctgpReadFailure = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
                { _ctgpReadFailure = ex.GetType().Name + ": " + ex.Message; }
                var now = DateTimeOffset.UtcNow;
                if (status?.Utc > now)
                {
                    _ctgpReadFailure = "La observación cTGP tiene una fecha futura.";
                    return null;
                }
                if (status is not null && (finalFailure || _lastCtgpStatus is null || status.Utc >= _lastCtgpStatus.Utc))
                    _lastCtgpStatus = status;
                // A retry, an older file or a failed read never renews the acquisition.
                var observed = _lastCtgpStatus;
                return observed is not null && (observed.Failure is not null || observed.FreshAt(now)) ? observed : null;
            }
        }
    }
    private CtgpStatus? ReadExitedCtgpFailure()
    {
        var child = _child;
        if (child is null || !child.HasExited) return null;
        using var report = CtgpPublication.TryRead<JsonDocument>(ReportPath);
        if (report is null) return null;
        var r = report.RootElement;
        if (r.GetProperty("SchemaVersion").GetInt32() != 2 ||
            r.GetProperty("OwnerPid").GetInt32() != _ownerPid ||
            r.GetProperty("OwnerStartUtcTicks").GetInt64() != _ownerStart ||
            r.GetProperty("TargetProfileId").GetString() != Hp8C40TargetProfile.Instance.Id ||
            r.GetProperty("GuardianPid").GetInt32() != child.Id ||
            r.GetProperty("GuardianStartUtcTicks").GetInt64() != child.StartTime.ToUniversalTime().Ticks ||
            !string.Equals(r.GetProperty("SessionDirectory").GetString(), Path.GetFullPath(SessionDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("El informe cTGP final no corresponde al guardián y sesión observados.");
        if (!r.TryGetProperty("SupervisionCtgp", out var atFailure) || atFailure.ValueKind == JsonValueKind.Null) return null;
        var status = atFailure.Deserialize<CtgpStatus>();
        // Restoration must not hide a policy fault that happened between two
        // GUI polls. This is diagnostic evidence, never proof of current activity.
        return status?.Failure is not null ? status : null;
    }
    internal object CtgpDiagnostic()
    {
        lock (_ctgpPublication)
            return new { observation = _lastCtgpStatus, ageMilliseconds = _lastCtgpStatus is null ? (double?)null :
                (DateTimeOffset.UtcNow - _lastCtgpStatus.Utc).TotalMilliseconds, readFailure = _ctgpReadFailure };
    }

    public async Task SetCtgpAsync(bool enabled, CancellationToken token)
    {
        await _ctgpCommands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureAlive(); Heartbeat();
            var command = new CtgpCommand(Guid.NewGuid(), _ownerPid, _ownerStart, enabled);
            WmiFanExperiment.WriteJson(Path.Combine(SessionDirectory, "ctgp-command.json"), command);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var replyPath = Path.Combine(SessionDirectory, "ctgp-reply.json");
            while (true)
            {
                EnsureAlive(); Heartbeat();
                if (File.Exists(replyPath))
                {
                    var reply = CtgpPublication.TryRead<CtgpReply>(replyPath);
                    if (reply?.Id == command.Id)
                    {
                        var status = reply.Status;
                        if(enabled&&status is{State:"Waiting",Managed:false,Active:false,Failure:null}&&status.FreshAt(DateTimeOffset.UtcNow))
                            throw new CtgpActivationDeferredException(status.WaitingReason??"cTGP espera acceso WMI.");
                        if (status is null || !status.ConfirmsSelection(enabled, DateTimeOffset.UtcNow))
                            throw new IOException(status?.Failure ?? "cTGP no confirmó la selección.");
                        lock (_ctgpPublication)
                        {
                            if (_lastCtgpStatus is null || status.Utc >= _lastCtgpStatus.Utc) _lastCtgpStatus = status;
                        }
                        return;
                    }
                }
                await Task.Delay(50, deadline.Token).ConfigureAwait(false);
            }
        }
        finally { _ctgpCommands.Release(); }
    }
    private string LeasePath => _fixture ? Path.Combine(SessionDirectory, "fixture-lease.json") : WmiFanGuiGuardianHost.LeasePath;
    public string SessionDirectory { get; private set; }
    public string ReportPath => Path.Combine(SessionDirectory, "guardian-report.json");
    internal string? LastReleasedReportPath { get; private set; }

    internal WmiFanGuiGuardianClient(bool fixture = false, string? fixtureDirectory = null, string? releasedBoundaryDirectory = null, bool productUpperRange = false, bool deferBoundary = false)
    {
        _fixture = fixture;
        _productUpperRange = productUpperRange;
        using var owner = Process.GetCurrentProcess();
        _ownerStart = owner.StartTime.ToUniversalTime().Ticks;
        if (!fixture && fixtureDirectory is not null) throw new ArgumentException("Fixture directory requires explicit fixture mode.");
        SessionDirectory = fixtureDirectory ?? (fixture
            ? Path.Combine(Path.GetTempPath(), "Victus-FanWmi-fixture-" + Guid.NewGuid().ToString("N"))
            : CreateSessionDirectory());
        Directory.CreateDirectory(SessionDirectory);
        if(!deferBoundary)ActivateBoundary(releasedBoundaryDirectory);
    }

    internal void ActivateBoundary(string? releasedBoundaryDirectory)
    {
        lock (_ctgpPublication) { _lastCtgpStatus = null; _ctgpReadFailure = null; }
        if (!_fixture)
        {
            if(releasedBoundaryDirectory is null) WmiFanExperimentBoundary.Enable(SessionDirectory, true, gui: true, productUpperRange: _productUpperRange);
            else
            {
                if(!string.Equals(Path.GetFullPath(releasedBoundaryDirectory),WmiFanExperimentBoundary.SessionDirectory,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("La frontera WMI liberada no coincide con el controlador anterior.");
                WmiFanExperimentBoundary.BeginRecovery();
                WmiFanExperimentBoundary.RearmGuiAfterSuccessfulRelease(releasedBoundaryDirectory,SessionDirectory);
            }
            WmiFanExperimentBoundary.EnsureGuiGuardianAlive = EnsureAlive;
        }
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (_released) throw new InvalidOperationException("WMI session is permanently released.");
        if (_child is not null) { EnsureAlive(); return; }
        if (File.Exists(LeasePath) || (!_fixture &&
            (File.Exists(WmiFanGuiGuardianHost.LegacyLeasePath) || File.Exists(WmiFanExperiment.LeasePath))))
            throw new InvalidOperationException("Pending fan lease blocks WMI GUI control; do not delete it.");
        Heartbeat();
        var start = new ProcessStartInfo(PerformanceGuardianClient.ResolveExecutable())
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in new[] { _fixture ? "--fan-wmi-fixture-session" : "--fan-wmi-session",
            "--session-dir", SessionDirectory, "--owner-pid", _ownerPid.ToString(), "--owner-start", _ownerStart.ToString() })
            start.ArgumentList.Add(arg);
        _fanSessionEntered=false;_fanDomainReleased=false;_completedRelease=null;
        _child = Process.Start(start) ?? throw new IOException("Could not start WMI fan guardian.");
        _child.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppLog.Write("WMI fan guardian: " + e.Data); };
        _child.OutputDataReceived += (_, e) => { if (e.Data is not null) AppLog.Write("WMI fan guardian: " + e.Data); };
        _child.BeginErrorReadLine(); _child.BeginOutputReadLine();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var readyPath = Path.Combine(SessionDirectory, "ready.json");
        while (!File.Exists(readyPath))
        {
            if (_child.HasExited) throw new IOException("WMI fan guardian failed before READY; no target dispatched.");
            await Task.Delay(50, deadline.Token).ConfigureAwait(false);
        }
        using var ready = JsonDocument.Parse(File.ReadAllText(readyPath));
        var r = ready.RootElement;
        if (r.GetProperty("OwnerPid").GetInt32() != _ownerPid || r.GetProperty("OwnerStartUtcTicks").GetInt64() != _ownerStart ||
            r.GetProperty("GuardianPid").GetInt32() != _child.Id ||
            r.GetProperty("GuardianStartUtcTicks").GetInt64() != _child.StartTime.ToUniversalTime().Ticks ||
            !r.GetProperty("DirectEcProhibited").GetBoolean() || r.GetProperty("LeasePath").GetString() != LeasePath)
            throw new IOException("WMI guardian READY identity mismatch.");
        EnsureAlive();
    }

    public void EnsureAlive()
    {
        if (_child is null || _child.HasExited || _released || !File.Exists(LeasePath) ||
            File.Exists(Path.Combine(SessionDirectory, "stop.signal")))
            throw new IOException("WMI fan guardian session is closed or unavailable.");
    }
    public void Heartbeat()
    {
        // cTGP reply polling and fan safety supervision can publish concurrently.
        // Serialize the timestamp capture too, so a queued writer cannot install
        // an older heartbeat after a newer one. Real I/O errors still propagate.
        lock (_heartbeatPublication)
            WmiFanExperiment.WriteJson(Path.Combine(SessionDirectory, "heartbeat.json"),
                new { Pid = _ownerPid, ElapsedMs = _clock.ElapsedMilliseconds });
    }
    public void PersistIntent(int level)
    {
        if (level < Hp8C40TargetProfile.MinimumValidatedFanLevel || level > (_productUpperRange ? Hp8C40ProductFanTargets.MaximumLogicalLevel : Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel))
            throw new ArgumentOutOfRangeException(nameof(level));
        EnsureAlive();
        _fanSessionEntered=true;_fanDomainReleased=false;
        var target = _productUpperRange ? Hp8C40ProductFanTargets.FromLogical(level) : (Cpu:level,Gpu:level);
        WmiFanExperiment.WriteJson(Path.Combine(SessionDirectory, "write-intent.json"), new
        { OwnerPid = _ownerPid, OwnerStartUtcTicks = _ownerStart,FanSession=_fanSession, Level = level, CpuLevel = target.Cpu, GpuLevel = target.Gpu, UpperRangePreview = _productUpperRange, Utc = DateTimeOffset.UtcNow });
        EnsureAlive();
    }
    public void BeginFanSession()
    {
        EnsureAlive();_fanSession=Guid.NewGuid();_fanSessionEntered=true;_fanDomainReleased=false;
        if(!_fixture)WmiFanExperimentBoundary.BeginFanSession();
    }
    internal async Task<FanWmiReleaseEvidence> ReleaseFanDomainAsync(CancellationToken token)
    {
        await _ctgpCommands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if(ChildExited||_child is null&&_completedRelease is not null)
                return await ConfirmCompletedReleaseAsync(token).ConfigureAwait(false);
            EnsureAlive();
            var directory=SessionDirectory;var hadIntent=File.Exists(Path.Combine(directory,"write-intent.json"));
            if(!_fixture)
            {
                WmiFanExperimentBoundary.BeginFanRelease();
                using var quiescence=CancellationTokenSource.CreateLinkedTokenSource(token);quiescence.CancelAfter(TimeSpan.FromSeconds(10));
                await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(quiescence.Token).ConfigureAwait(false);
                await Task.Run(()=>{using var slot=WmiFanExperimentBoundary.EnterRecoverySlot(directory,TimeSpan.FromSeconds(10),waitForConfirmedCleanup:true);}).ConfigureAwait(false);
            }
            var command=new WmiFanDomainCommand(Guid.NewGuid(),_fanSession,_ownerPid,_ownerStart);
            WmiFanExperiment.WriteJson(Path.Combine(directory,"fan-domain-command.json"),command);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while(true)
            {
                EnsureAlive();Heartbeat();
                var reply=CtgpPublication.TryRead<WmiFanDomainReply>(Path.Combine(directory,"fan-domain-reply.json"));
                if(reply?.Id==command.Id)
                {
                    var child=_child!;var now=DateTimeOffset.UtcNow;
                    if(reply.SchemaVersion!=1||reply.FanSession!=command.FanSession||reply.OwnerPid!=_ownerPid||reply.OwnerStartUtcTicks!=_ownerStart||
                        reply.GuardianPid!=child.Id||reply.GuardianStartUtcTicks!=child.StartTime.ToUniversalTime().Ticks||reply.TargetProfileId!=Hp8C40TargetProfile.Instance.Id||
                        !string.Equals(reply.SessionDirectory,directory,StringComparison.OrdinalIgnoreCase)||!reply.FanReleased||!reply.NativeComplete||
                        now<reply.Utc||now-reply.Utc>TimeSpan.FromSeconds(5)||hadIntent&&(!reply.ReleaseRequestAccepted||!reply.LegacyDefaultRequestAccepted)||
                        File.Exists(Path.Combine(directory,"write-intent.json"))||File.Exists(Path.Combine(directory,"native-uncertain.signal"))||
                        reply.Ctgp.Failure is not null||!reply.Ctgp.FreshAt(now))
                        throw new IOException("El recibo de ventiladores no acredita esta sesión compartida.");
                    if(!_fixture)WmiFanExperimentBoundary.CompleteFanRelease();
                    _fanDomainReleased=true;
                    return new(reply.ReleaseRequestAccepted,reply.LegacyDefaultRequestAccepted,false,false,
                        Path.Combine(directory,"fan-domain-reply.json"),"FAN_DOMAIN_RELEASED"){FanDomainReleased=true};
                }
                await Task.Delay(50,deadline.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            if(!_fixture)WmiFanExperimentBoundary.BeginRecovery();
            WmiFanGuiGuardianHost.RequestStop(SessionDirectory,"FAN_DOMAIN_RELEASE_UNCONFIRMED");
            throw;
        }
        finally{_ctgpCommands.Release();}
    }

    public async Task<FanWmiReleaseEvidence> ReleaseAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        Process releasedChild;Task completion;string releasedDirectory;
        lock(_releaseCompletionSync)
        {
        token.ThrowIfCancellationRequested();
        if(_child is null&&_completedRelease is not null)return ReadCompletedRelease();
        _released=true;
        releasedDirectory = SessionDirectory;
        if(_child is null)
            return new(false,false,!File.Exists(LeasePath),false,Path.Combine(releasedDirectory,"guardian-report.json"),"NO_SESSION");
        releasedChild=_child;
        // Register before another completion observer can dispose this handle.
        completion=releasedChild.WaitForExitAsync(deadline.Token);
        }
        var releasedReportPath = Path.Combine(releasedDirectory, "guardian-report.json");
        var releasedLeasePath = _fixture
            ? Path.Combine(releasedDirectory, "fixture-lease.json")
            : WmiFanGuiGuardianHost.LeasePath;

        if (_fixture) WmiFanGuiGuardianHost.RequestStop(releasedDirectory, "CLIENT_RELEASE");
        else await Task.Run(() => WmiFanExperimentBoundary.PrepareGuiReleaseStop(releasedDirectory, TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        await completion.ConfigureAwait(false);
        lock(_releaseCompletionSync)
        {
            if(_child is null&&_completedRelease is{} cached&&string.Equals(cached.ReportPath,releasedReportPath,StringComparison.OrdinalIgnoreCase))
                return ReadCompletedRelease();
            if(!ReferenceEquals(_child,releasedChild))throw new IOException("La sesión WMI cambió durante la confirmación del cierre.");
            return CompleteRelease(releasedDirectory, releasedReportPath, releasedLeasePath);
        }
    }

    private FanWmiReleaseEvidence ReadCompletedRelease()
    {
        var completed=_completedRelease??throw new IOException("No hay cierre WMI confirmado.");
        var old=Path.GetDirectoryName(completed.ReportPath)!;
        if(new[]{old,SessionDirectory}.Distinct(StringComparer.OrdinalIgnoreCase).Any(directory=>
            File.Exists(Path.Combine(directory,"native-inflight.json"))||File.Exists(Path.Combine(directory,"native-uncertain.signal"))))
            throw new IOException("La evidencia nativa cambió después del cierre; se conserva el bloqueo.");
        return completed;
    }

    public Task<FanWmiReleaseEvidence> ConfirmCompletedReleaseAsync(CancellationToken token)
    {
        lock(_releaseCompletionSync)
        {
        token.ThrowIfCancellationRequested();
        if(_child is null&&_completedRelease is not null)
            return Task.FromResult(ReadCompletedRelease());
        // Keep the actual Process handle: PID absence or a report alone cannot
        // establish completion. This path never signals stop or dispatches WMI.
        if (_child is null || !_child.HasExited)
            throw new IOException("WMI guardian completion is not confirmed; release remains blocked.");
        return Task.FromResult(CompleteRelease(SessionDirectory, ReportPath, LeasePath));
        }
    }

    private FanWmiReleaseEvidence CompleteRelease(string releasedDirectory, string releasedReportPath, string releasedLeasePath)
    {
        if (_child is null || !_child.HasExited ||
            File.Exists(Path.Combine(releasedDirectory, "native-inflight.json")) ||
            File.Exists(Path.Combine(releasedDirectory, "native-uncertain.signal")))
            throw new IOException("WMI native completion is unconfirmed; retain release evidence.");
        if (!File.Exists(releasedReportPath))
            throw new IOException("WMI guardian exited without release report; retain the lease.");

        FanWmiReleaseEvidence ReadReleaseEvidence(Process child)
        {
            try
            {
                using var report = JsonDocument.Parse(File.ReadAllText(releasedReportPath));
                var r = report.RootElement;
                if (r.GetProperty("SchemaVersion").GetInt32() != 2 ||
                    r.GetProperty("GuardianPid").GetInt32() != child.Id ||
                    r.GetProperty("GuardianStartUtcTicks").GetInt64() != child.StartTime.ToUniversalTime().Ticks ||
                    !string.Equals(r.GetProperty("SessionDirectory").GetString(), Path.GetFullPath(releasedDirectory), StringComparison.OrdinalIgnoreCase) ||
                    !r.GetProperty("RecoveryCompleted").GetBoolean() ||
                    r.GetProperty("OwnerPid").GetInt32() != _ownerPid ||
                    r.GetProperty("OwnerStartUtcTicks").GetInt64() != _ownerStart ||
                    r.GetProperty("TargetProfileId").GetString() != Hp8C40TargetProfile.Instance.Id ||
                    !r.GetProperty("DirectEcProhibited").GetBoolean() ||
                    r.GetProperty("IndependentFirmwareOwnershipVerified").GetBoolean() ||
                    !r.GetProperty("GuardianLeaseRetired").GetBoolean() ||
                    (File.Exists(Path.Combine(releasedDirectory, "write-intent.json")) &&
                        (!r.GetProperty("ReleaseRequestAccepted").GetBoolean() || !r.GetProperty("LegacyDefaultRequestAccepted").GetBoolean())) ||
                    r.GetProperty("Failure").ValueKind != JsonValueKind.Null ||
                    (File.Exists(Path.Combine(releasedDirectory, "ctgp-intent.json")) &&
                        (!r.TryGetProperty("CtgpRestored", out var ctgpRestored) || !ctgpRestored.GetBoolean())) ||
                    child.ExitCode != 0 ||
                    File.Exists(releasedLeasePath))
                    throw new IOException("WMI release report failed identity/completion checks; review retained lease.");

                return new FanWmiReleaseEvidence(
                    r.GetProperty("ReleaseRequestAccepted").GetBoolean(),
                    r.GetProperty("LegacyDefaultRequestAccepted").GetBoolean(),
                    r.GetProperty("GuardianLeaseRetired").GetBoolean(),
                    false,
                    releasedReportPath,
                    r.GetProperty("ExitReason").GetString()!)
                { SupervisionCtgp = r.GetProperty("SupervisionCtgp").ValueKind == JsonValueKind.Null ? null :
                    r.GetProperty("SupervisionCtgp").Deserialize<CtgpStatus>() };
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
            { throw new IOException("WMI release report is malformed; completion remains unconfirmed.", ex); }
        }

        var evidence = ReadReleaseEvidence(_child);
        LastReleasedReportPath = evidence.ReportPath;

        if (!_fixture)
        {
            var nextDirectory = CreateSessionDirectory();
            WmiFanExperimentBoundary.RearmGuiAfterSuccessfulRelease(
                releasedDirectory,
                nextDirectory);
            lock (_ctgpPublication)
            {
                SessionDirectory = nextDirectory;
                _lastCtgpStatus = null; _ctgpReadFailure = null;
            }
            _clock.Restart();
            _released = false;
        }
        else
        {
            lock (_ctgpPublication) { _lastCtgpStatus = null; _ctgpReadFailure = null; }
            if(RearmFixtureAfterCompletion)
            {
                SessionDirectory=Path.Combine(Path.GetTempPath(),"Victus-FanWmi-fixture-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(SessionDirectory);_clock.Restart();_released=false;
            }
        }

        _child.Dispose();
        _child = null;
        _completedRelease=evidence;

        return evidence;
    }

    private static string CreateSessionDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VictusFanControl",
            "FanWmi",
            "gui",
            Guid.NewGuid().ToString("N"));

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_fixture)
            {
                // Firmware telemetry uses a fresh directory after release,
                // even when no guardian has been started for that directory.
                // Fence and drain it too before letting the owner exit.
                WmiFanExperimentBoundary.BeginRecovery();
                WmiFanGuiGuardianHost.RequestStop(SessionDirectory, "OWNER_DISPOSE");
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && _child is null)
                {
                    var directory = SessionDirectory;
                    throw new WmiFanReadDrainPendingException(ex, () => ConfirmPassiveReadDrainAsync(directory));
                }
                await Task.Run(() =>
                {
                    using var slot = WmiFanExperimentBoundary.EnterRecoverySlot(SessionDirectory, TimeSpan.FromSeconds(10));
                }).ConfigureAwait(false);
            }
        }
        finally { _child?.Dispose(); }
    }

    private async Task ConfirmPassiveReadDrainAsync(string directory)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(deadline.Token).ConfigureAwait(false);
        await Task.Run(() =>
        {
            using var slot = WmiFanExperimentBoundary.EnterRecoverySlot(directory, TimeSpan.FromSeconds(10), waitForConfirmedCleanup: true);
            if (_child is not null || !WmiFanExperimentBoundary.Recovering ||
                !string.Equals(directory, WmiFanExperimentBoundary.SessionDirectory, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(directory, "stop.signal")) || File.Exists(LeasePath) ||
                File.Exists(Path.Combine(directory, "write-intent.json")) || File.Exists(Path.Combine(directory, "ctgp-intent.json")))
                throw new IOException("No se confirmó el cierre de la sesión de lectura WMI.");
        }).ConfigureAwait(false);
    }
}

internal sealed class WmiFanReadDrainPendingException(Exception cause, Func<Task> confirm)
    : IOException("La lectura WMI sigue pendiente al cerrar la sesión.", cause)
{
    internal Task ConfirmAsync() => confirm();
}
