using System.Diagnostics;
using System.Text.Json;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Runtime;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal sealed class WmiFanGuiGuardianClient : IWmiFanGuiGuardian
{
    private readonly bool _fixture;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Process? _child;
    private readonly int _ownerPid = Environment.ProcessId;
    private readonly long _ownerStart;
    private bool _released;
    private string LeasePath => _fixture ? Path.Combine(SessionDirectory, "fixture-lease.json") : WmiFanGuiGuardianHost.LeasePath;
    public string SessionDirectory { get; private set; }
    public string ReportPath => Path.Combine(SessionDirectory, "guardian-report.json");

    internal WmiFanGuiGuardianClient(bool fixture = false, string? fixtureDirectory = null, string? releasedBoundaryDirectory = null)
    {
        _fixture = fixture;
        using var owner = Process.GetCurrentProcess();
        _ownerStart = owner.StartTime.ToUniversalTime().Ticks;
        if (!fixture && fixtureDirectory is not null) throw new ArgumentException("Fixture directory requires explicit fixture mode.");
        SessionDirectory = fixtureDirectory ?? (fixture
            ? Path.Combine(Path.GetTempPath(), "Victus-FanWmi-fixture-" + Guid.NewGuid().ToString("N"))
            : CreateSessionDirectory());
        Directory.CreateDirectory(SessionDirectory);
        if (!fixture)
        {
            if(releasedBoundaryDirectory is null) WmiFanExperimentBoundary.Enable(SessionDirectory, true, gui: true);
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
    public void Heartbeat() => WmiFanExperiment.WriteJson(Path.Combine(SessionDirectory, "heartbeat.json"),
        new { Pid = _ownerPid, ElapsedMs = _clock.ElapsedMilliseconds });
    public void PersistIntent(int level)
    {
        if (level < Hp8C40TargetProfile.MinimumValidatedFanLevel || level > Hp8C40TargetProfile.MaximumPhysicallyQualifiedFanLevel)
            throw new ArgumentOutOfRangeException(nameof(level));
        EnsureAlive();
        WmiFanExperiment.WriteJson(Path.Combine(SessionDirectory, "write-intent.json"), new
        { OwnerPid = _ownerPid, OwnerStartUtcTicks = _ownerStart, Level = level, Utc = DateTimeOffset.UtcNow });
        EnsureAlive();
    }

    public async Task<FanWmiReleaseEvidence> ReleaseAsync(CancellationToken token)
    {
        _released = true;
        var releasedDirectory = SessionDirectory;
        var releasedReportPath = Path.Combine(releasedDirectory, "guardian-report.json");
        var releasedLeasePath = _fixture
            ? Path.Combine(releasedDirectory, "fixture-lease.json")
            : WmiFanGuiGuardianHost.LeasePath;

        if (_child is null)
            return new(false, false, !File.Exists(releasedLeasePath), false, releasedReportPath, "NO_SESSION");

        WmiFanGuiGuardianHost.RequestStop(releasedDirectory, "CLIENT_RELEASE");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await _child.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        if (!File.Exists(releasedReportPath))
            throw new IOException("WMI guardian exited without release report; retain the lease.");

        using var report = JsonDocument.Parse(File.ReadAllText(releasedReportPath));
        var r = report.RootElement;
        if (r.GetProperty("OwnerPid").GetInt32() != _ownerPid ||
            r.GetProperty("OwnerStartUtcTicks").GetInt64() != _ownerStart ||
            r.GetProperty("TargetProfileId").GetString() != Hp8C40TargetProfile.Instance.Id ||
            !r.GetProperty("DirectEcProhibited").GetBoolean() ||
            r.GetProperty("IndependentFirmwareOwnershipVerified").GetBoolean() ||
            r.GetProperty("Failure").ValueKind != JsonValueKind.Null ||
            _child.ExitCode != 0 ||
            File.Exists(releasedLeasePath))
            throw new IOException("WMI release report failed identity/completion checks; review retained lease.");

        var evidence = new FanWmiReleaseEvidence(
            r.GetProperty("ReleaseRequestAccepted").GetBoolean(),
            r.GetProperty("LegacyDefaultRequestAccepted").GetBoolean(),
            r.GetProperty("GuardianLeaseRetired").GetBoolean(),
            false,
            releasedReportPath,
            r.GetProperty("ExitReason").GetString()!);

        _child.Dispose();
        _child = null;

        if (!_fixture)
        {
            var nextDirectory = CreateSessionDirectory();
            WmiFanExperimentBoundary.RearmGuiAfterSuccessfulRelease(
                releasedDirectory,
                nextDirectory);
            SessionDirectory = nextDirectory;
            _clock.Restart();
            _released = false;
        }

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
                await HpWmiFanTelemetryReader.WaitForProductionQuiescenceAsync(deadline.Token).ConfigureAwait(false);
                await Task.Run(() =>
                {
                    using var slot = WmiFanExperimentBoundary.EnterRecoverySlot(SessionDirectory, TimeSpan.FromSeconds(10));
                }).ConfigureAwait(false);
            }
        }
        finally { _child?.Dispose(); }
    }
}
