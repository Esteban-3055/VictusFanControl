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
    internal PerformanceGuardianResponse? LastStatus { get; private set; }
    internal PerformanceGuiSessionConfiguration? AppliedConfiguration { get; private set; }
    internal string? GuardianReportPath { get; private set; }
    internal bool HasProcess => _process is not null;
    internal bool LimitsActive => _process is { HasExited: false } && LastStatus is { Ok: true, SessionEnabled: true, RuntimeFailure: null } s &&
        DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastStatusUtcTicks) < TimeSpan.FromSeconds(6).Ticks &&
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
            var executable = ResolveExecutable();
            using var owner = Process.GetCurrentProcess();
            _ownerPid = owner.Id; _ownerStart = owner.StartTime.ToUniversalTime().Ticks;
            _nonce = Guid.NewGuid(); _pipeName = "VFC.Performance.Gui." + _nonce.ToString("N");
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
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppLog.Write("Performance Guardian: " + e.Data); };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppLog.Write("Performance Guardian: " + e.Data); };
            _process.BeginErrorReadLine(); _process.BeginOutputReadLine();
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
            // Retain a live process so release can reconnect. Never kill a hardware owner.
            if (_process is { HasExited: true }) { _process.Dispose(); _process = null; }
            throw;
        }
        finally { _gate.Release(); }
    }

    internal async Task<PerformanceGuardianResponse?> StatusAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_process is null) return null;
            if (_process.HasExited) throw new IOException("Performance Guardian terminó; revisa el informe de liberación.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_pipe is null || !_pipe.IsConnected) await ConnectAsync(timeout.Token).ConfigureAwait(false);
            LastStatus = await SendAsync(PerformanceGuardianProtocol.Status, null, timeout.Token).ConfigureAwait(false);
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
        await _pipe.ConnectAsync(token).ConfigureAwait(false);
        var hello = await SendAsync(PerformanceGuardianProtocol.Hello, null, token).ConfigureAwait(false);
        if (!hello.Ok) throw new IOException("Guardian rechazó la identidad: " + hello.Message);
    }

    private async Task<PerformanceGuardianResponse> SendAsync(string type, PerformanceGuiSessionConfiguration? configuration, CancellationToken token)
    {
        var request = new PerformanceGuardianRequest(PerformanceGuardianProtocol.Version, Guid.NewGuid(), CpuPowerProductDefaults.TargetProfileId,
            _nonce, type, type == PerformanceGuardianProtocol.Hello ? _ownerPid : null,
            type == PerformanceGuardianProtocol.Hello ? _ownerStart : null, configuration?.CpuEnabled, configuration?.GpuEnabled);
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

    private static string ResolveExecutable()
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
