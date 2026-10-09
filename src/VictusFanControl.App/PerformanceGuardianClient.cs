using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using VictusFanControl.Performance;

namespace VictusFanControl.App;

internal sealed class PerformanceGuardianClient
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _modulePath;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private Guid _nonce;
    private string? _pipeName;
    private int _ownerPid;
    private long _ownerStart;
    private long _lastStatusUtcTicks;
    private string? _startupFailure;
    internal PerformanceGuardianResponse? LastStatus { get; private set; }
    internal PerformanceGuiSessionConfiguration? AppliedConfiguration { get; private set; }
    internal string? GuardianReportPath { get; private set; }
    internal bool HasProcess => _process is not null;
    internal bool HasFailedProcess
    {
        get { try { return _process is {HasExited:true,ExitCode:not 0}; } catch(InvalidOperationException) {return false;} }
    }
    internal bool LastStatusFresh
    {
        get
        {
            try { return LastStatus is not null && IsStatusFresh(DateTime.UtcNow.Ticks,Interlocked.Read(ref _lastStatusUtcTicks),_process is { HasExited:false }); }
            catch(InvalidOperationException) { return false; }
        }
    }
    internal static bool IsStatusFresh(long nowTicks,long observedTicks,bool ownerAlive) => ownerAlive&&observedTicks>0&&nowTicks>=observedTicks&&nowTicks-observedTicks<TimeSpan.FromSeconds(6).Ticks;
    internal bool LimitsActive => LastStatusFresh && LastStatus is { Ok:true,SessionEnabled:true,RuntimeFailure:null } s &&
        (!s.CpuEnabled || s.CpuState == "Active") && (!s.GpuEnabled || s.GpuState == "ActiveUnverified");

    internal PerformanceGuardianClient(string modulesDirectory, Func<ProcessStartInfo, Process?>? startProcess = null)
    {
        _modulePath = Path.Combine(modulesDirectory, "IntelMSR.bin");
        _startProcess = startProcess ?? Process.Start;
    }

    internal async Task<PerformanceGuardianResponse> EnableAsync(PerformanceGuiSessionConfiguration configuration)
    {
        configuration.Validate();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is not null) throw new InvalidOperationException("Libera la sesión anterior antes de aplicar otra configuración.");
            var recovery=PerformanceRecoveryPreview.Read();
            if(recovery.Pending)throw new InvalidOperationException(recovery.Detail);
            var executable = ResolveExecutable();
            using var owner = Process.GetCurrentProcess();
            _ownerPid = owner.Id; _ownerStart = owner.StartTime.ToUniversalTime().Ticks;
            _nonce = Guid.NewGuid(); _pipeName = "VFC.Performance.Gui." + _nonce.ToString("N");
            _startupFailure = null;
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VictusFanControl", "Performance", "gui", _nonce.ToString("N"));
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "configuration.json");
            GuardianReportPath = Path.Combine(directory, "guardian-report.json");
            File.WriteAllText(configPath, JsonSerializer.Serialize(configuration));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "--gui-session", "--configuration", configPath, "--module", _modulePath, "--pipe", _pipeName,
                "--nonce", _nonce.ToString("D"), "--owner-pid", _ownerPid.ToString(), "--owner-start", _ownerStart.ToString(), "--report", GuardianReportPath })
                start.ArgumentList.Add(arg);
            _process = _startProcess(start) ?? throw new IOException("No se pudo iniciar Performance Guardian.");
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { if(e.Data.StartsWith("Performance Guardian failed:",StringComparison.Ordinal))Volatile.Write(ref _startupFailure,e.Data); AppLog.Write("Performance Guardian: " + e.Data); } };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppLog.Write("Performance Guardian: " + e.Data); };
            _process.BeginErrorReadLine(); _process.BeginOutputReadLine();
            if(_process.HasExited)throw StoppedException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await ConnectAsync(timeout.Token).ConfigureAwait(false);
            LastStatus = await SendAsync(PerformanceGuardianProtocol.EnableSession, configuration, timeout.Token).ConfigureAwait(false);
            if (!LastStatus.Ok) throw new InvalidOperationException(LastStatus.Code + ": " + LastStatus.Message);
            AppliedConfiguration = configuration;
            return LastStatus;
        }
        catch
        {
            _pipe?.Dispose(); _pipe = null;
            // Retain live or failed owners; a nonzero exit is not proof of restored hardware.
            if (_process is { HasExited: true, ExitCode: 0 }) { _process.Dispose(); _process = null; }
            throw;
        }
        finally { _gate.Release(); }
    }

    internal async Task<PerformanceGuardianResponse> UpdateAsync(PerformanceGuiSessionConfiguration configuration)
    {
        configuration.Validate();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            if (_process is null || _process.HasExited || !LimitsActive || AppliedConfiguration is null ||
                configuration.CpuEnabled != AppliedConfiguration.CpuEnabled || configuration.GpuEnabled != AppliedConfiguration.GpuEnabled)
                throw new InvalidOperationException("Actualizar requiere la misma sesión activa y selección CPU/GPU.");
            if (_pipe is null || !_pipe.IsConnected) await ConnectAsync(timeout.Token).ConfigureAwait(false);
            LastStatus = null;
            LastStatus = await SendAsync(PerformanceGuardianProtocol.UpdateConfiguration, configuration, timeout.Token).ConfigureAwait(false);
            CaptureConfiguration(LastStatus);
            if (!LastStatus.Ok || AppliedConfiguration != configuration || !LimitsActive)
                throw new InvalidOperationException(LastStatus.Code + ": " + LastStatus.Message);
            return LastStatus;
        }
        catch
        {
            // A lost reply is not proof of rollback. Reconnect STATUS to recover committed presets.
            if (LastStatus is null) { _pipe?.Dispose(); _pipe = null; }
            throw;
        }
        finally { _gate.Release(); }
    }

    private void CaptureConfiguration(PerformanceGuardianResponse response)
    {
        if (response.Configuration is not { } configuration) return;
        try
        {
            configuration.Validate();
            if (AppliedConfiguration is { } previous &&
                (configuration.CpuEnabled != previous.CpuEnabled || configuration.GpuEnabled != previous.GpuEnabled))
                throw new IOException("Guardian changed active domains unexpectedly.");
        }
        catch { LastStatus = null; throw; }
        AppliedConfiguration = configuration;
    }

    internal async Task<PerformanceGuardianResponse?> StatusAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is null) return null;
            if (_process.HasExited) throw StoppedException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_pipe is null || !_pipe.IsConnected) await ConnectAsync(timeout.Token).ConfigureAwait(false);
            LastStatus = await SendAsync(PerformanceGuardianProtocol.Status, null, timeout.Token).ConfigureAwait(false);
            CaptureConfiguration(LastStatus);
            return LastStatus;
        }
        catch { LastStatus = null; _pipe?.Dispose(); _pipe = null; throw; }
        finally { _gate.Release(); }
    }

    internal async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is null) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (!_process.HasExited)
            {
                if (_pipe is null || !_pipe.IsConnected) await ConnectAsync(timeout.Token).ConfigureAwait(false);
                LastStatus = await SendAsync(PerformanceGuardianProtocol.Shutdown, null, timeout.Token).ConfigureAwait(false);
                if (!LastStatus.Ok || LastStatus.SessionEnabled || LastStatus.Phase != "Stopped")
                    throw new IOException("Guardian no confirmó la liberación: " + LastStatus.Message);
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            if (_process.ExitCode != 0) throw new IOException("Guardian terminó con error " + _process.ExitCode + "; revisa los journals.");
            _process.Dispose(); _process = null; _pipe?.Dispose(); _pipe = null; AppliedConfiguration = null; LastStatus = null;
        }
        finally { _gate.Release(); }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        _pipe?.Dispose();
        _pipe = new NamedPipeClientStream(".", _pipeName!, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(token);
        var connection = _pipe.ConnectAsync(waiting.Token);
        var exit = _process!.WaitForExitAsync(waiting.Token);
        try
        {
            await Task.WhenAny(connection, exit).ConfigureAwait(false);
            if (_process.HasExited)
            {
                waiting.Cancel();
                try { await connection.ConfigureAwait(false); } catch(Exception ex) when(ex is OperationCanceledException or IOException) { }
                throw StoppedException();
            }
            await connection.ConfigureAwait(false);
        }
        finally { waiting.Cancel(); }
        var hello = await SendAsync(PerformanceGuardianProtocol.Hello, null, token).ConfigureAwait(false);
        if (!hello.Ok) throw new IOException("Guardian rechazó la identidad: " + hello.Message);
    }

    private IOException StoppedException()
    {
        // Already exited: drain redirected error output before reporting the cause.
        _process!.WaitForExit();
        var recovery=PerformanceRecoveryPreview.Read();
        if(recovery.Pending)return new IOException(recovery.Detail+" Guardian terminó con error "+_process.ExitCode+".");
        var detail = Volatile.Read(ref _startupFailure);
        return new IOException("Performance Guardian terminó con error " + _process.ExitCode +
            "; conserva los registros pendientes y revisa la recuperación. " + (detail ?? "Revisa el informe de liberación."));
    }

    private async Task<PerformanceGuardianResponse> SendAsync(string type, PerformanceGuiSessionConfiguration? configuration, CancellationToken token)
    {
        var request = new PerformanceGuardianRequest(PerformanceGuardianProtocol.Version, Guid.NewGuid(), CpuPowerProductDefaults.TargetProfileId,
            _nonce, type, type == PerformanceGuardianProtocol.Hello ? _ownerPid : null,
            type == PerformanceGuardianProtocol.Hello ? _ownerStart : null, configuration?.CpuEnabled, configuration?.GpuEnabled,
            type == PerformanceGuardianProtocol.UpdateConfiguration ? configuration : null);
        await PerformanceGuardianCodec.WriteRequestAsync(_pipe!, request, token).ConfigureAwait(false);
        var response = await PerformanceGuardianCodec.ReadResponseAsync(_pipe!, token).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Guardian cerró la conexión.");
        ValidateResponse(request, response);
        Interlocked.Exchange(ref _lastStatusUtcTicks, DateTime.UtcNow.Ticks);
        return response;
    }

    internal static void ValidateResponse(PerformanceGuardianRequest request, PerformanceGuardianResponse response)
    {
        if (response.ProtocolVersion != PerformanceGuardianProtocol.Version || response.RequestId != request.RequestId ||
            response.TargetProfileId != request.TargetProfileId)
            throw new IOException("Guardian response identity/version mismatch.");
    }

    internal static string ResolveExecutable()
    {
        var published = Path.Combine(AppContext.BaseDirectory, "VictusFanControl.PerformanceGuardian.exe");
        if (File.Exists(published)) return published;
        published = Path.Combine(AppContext.BaseDirectory, "performance-guardian", "VictusFanControl.PerformanceGuardian.exe");
        if (File.Exists(published)) return published;
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VictusFanControl.sln")))
            {
                var built = Path.Combine(directory.FullName, "src", "VictusFanControl.PerformanceGuardian", "bin", "Release", "net8.0-windows", "VictusFanControl.PerformanceGuardian.exe");
                if (File.Exists(built)) return built;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Compila la solución Release para disponer de Performance Guardian.");
    }
}
