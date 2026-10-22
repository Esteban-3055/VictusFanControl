using System.Diagnostics;
using System.Text;
using System.Runtime.InteropServices;
using VictusFanControl.Hardware.Nvidia;

namespace VictusFanControl.App;

/// <summary>Only GPU reads cross this boundary. Native failure cannot corrupt the GUI process.</summary>
internal sealed class NvmlTelemetryProcessClient : INvmlTelemetryReader
{
    internal const int SampleTimeoutMilliseconds = 1500;
    internal const int RestartBackoffMilliseconds = 5000;
    private readonly object _gate = new();
    private readonly string? _preferredName;
    private readonly bool _exact;
    private readonly Func<ProcessStartInfo> _startInfo;
    private readonly int _sampleTimeout;
    private readonly Func<long> _clock;
    private readonly Action<Process> _terminate;
    private static readonly object RetiredGate=new();
    private static readonly List<Process> UnconfirmedReaders=new();
    private Process? _process;
    private ErrorTail _errors = new();
    private Task _errorDrain = Task.CompletedTask;
    private string _session = "";
    private long _sequence, _retryAt;
    private bool _disposed;
    internal int? ChildProcessId => _process?.Id;
    internal int NativeDiagnosticCharacterCount => _errors.Text.Length;
    public string DeviceName { get; private set; } = "NVIDIA GPU";

    internal NvmlTelemetryProcessClient(string? preferredName, bool exact,
        Func<ProcessStartInfo>? startInfo = null, int sampleTimeout = SampleTimeoutMilliseconds, Func<long>? clock = null, Action<Process>? terminateFixture = null)
    {
        _preferredName = preferredName; _exact = exact;
        _startInfo = startInfo ?? (() => CreateStartInfo());
        _sampleTimeout = sampleTimeout; _clock = clock ?? (() => Environment.TickCount64);
        _terminate=terminateFixture??(child=>child.Kill(entireProcessTree:true));
        lock (_gate) Start();
    }

    internal static ProcessStartInfo CreateStartInfo(string? fixture = null)
    {
        // A WinExe apphost may expose null console streams even with redirected handles.
        // Use the installed console host for private IPC; never start another GUI/UAC prompt.
        var path = Environment.ProcessPath ?? throw new IOException("Application executable unavailable.");
        if (!Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            path = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        if (!File.Exists(path)) throw new IOException("The installed .NET console host is unavailable.");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(typeof(NvmlTelemetryProcessClient).Assembly.Location);
        using var parent = Process.GetCurrentProcess();
        start.ArgumentList.Add(fixture is null ? "--nvml-telemetry-worker" : "--nvml-telemetry-fixture");
        start.ArgumentList.Add(parent.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (fixture is not null) start.ArgumentList.Add(fixture);
        return start;
    }

    private void Start()
    {
        try
        {
            lock(RetiredGate)
            {
                for(var i=UnconfirmedReaders.Count-1;i>=0;i--)
                    if(UnconfirmedReaders[i].HasExited){UnconfirmedReaders[i].Dispose();UnconfirmedReaders.RemoveAt(i);}
                if(UnconfirmedReaders.Count!=0)throw new NvmlTelemetryUnavailableException("Previous read-only NVML helper termination remains unconfirmed; no replacement spawned.");
            }
            _session = Guid.NewGuid().ToString("N"); _sequence = 0;
            _process = Process.Start(_startInfo()) ?? throw new IOException("NVML helper did not start.");
            _process.StandardInput.AutoFlush = true;
            _errors = new(); _errorDrain = DrainErrorsAsync(_process.StandardError, _errors);
            var ready = ExchangeAsync("initialize", 5000).GetAwaiter().GetResult();
            if (ready.Sample is not null || ready.SampledAtUtc is not null || string.IsNullOrWhiteSpace(ready.DeviceName) ||
                _exact && !string.Equals(_preferredName, ready.DeviceName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NVML helper GPU identity mismatch.");
            DeviceName = ready.DeviceName;
        }
        catch (Exception ex) { throw Fail(ex); }
    }

    public NvmlClient.GpuSample ReadSample()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process is null)
            {
                if (_clock() < _retryAt) throw new NvmlTelemetryUnavailableException("NVML helper is waiting before retry; GPU sample unavailable.");
                Start();
            }
            try
            {
                var started = DateTimeOffset.UtcNow;
                var reply = ExchangeAsync("sample", _sampleTimeout).GetAwaiter().GetResult();
                if (reply.DeviceName != DeviceName || reply.SampledAtUtc is not { } sampled || sampled < started ||
                    sampled > DateTimeOffset.UtcNow || reply.Sample is not { } sample ||
                    !double.IsFinite(sample.TemperatureC) || sample.TemperatureC is < 10 or > 125 ||
                    !double.IsFinite(sample.PowerW) || sample.PowerW is < 0 or >= 300 ||
                    !double.IsFinite(sample.LoadPercent) || sample.LoadPercent is < 0 or > 100)
                    throw new InvalidDataException("Invalid or stale NVML helper sample.");
                return sample;
            }
            catch (Exception ex) { throw Fail(ex); }
        }
    }

    private async Task<NvmlTelemetryProtocol.Reply> ExchangeAsync(string operation, int timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var request = new NvmlTelemetryProtocol.Request(NvmlTelemetryProtocol.Version, _session, ++_sequence,
            operation, _preferredName, _exact);
        await NvmlTelemetryProtocol.WriteAsync(_process!.StandardInput, request, cancellation.Token).ConfigureAwait(false);
        var line = await NvmlTelemetryProtocol.ReadLineAsync(_process.StandardOutput, cancellation.Token).ConfigureAwait(false);
        var reply = NvmlTelemetryProtocol.Parse<NvmlTelemetryProtocol.Reply>(line);
        if (reply.Version != request.Version || reply.Session != request.Session || reply.Sequence != request.Sequence || reply.Failed)
            throw new InvalidDataException("NVML helper rejected or mismatched the acquisition.");
        return reply;
    }

    private NvmlTelemetryUnavailableException Fail(Exception error)
    {
        var pid = _process?.Id;
        int? exitCode = null;
        try { if (_process?.HasExited == true) exitCode = _process.ExitCode; } catch (InvalidOperationException) { }
        var stoppedExit = Stop(); exitCode ??= stoppedExit;
        try { _errorDrain.Wait(100); } catch (AggregateException) { }
        _retryAt = _clock() + RestartBackoffMilliseconds;
        AppLog.WriteStructured("NVML TELEMETRY HELPER FAILURE", new { processId = pid, exitCode,
            failure = error.GetType().Name, session = _session, sequence = _sequence,
            retryAfterMs = RestartBackoffMilliseconds, gpuSampleValid = false, nativeDiagnostic = _errors.Text });
        return new NvmlTelemetryUnavailableException("Isolated NVML acquisition failed (" + error.GetType().Name + "); GPU sample unavailable.");
    }

    private sealed class ErrorTail
    {
        private readonly object _sync = new();
        private readonly StringBuilder _text = new();
        internal string Text { get { lock (_sync) return _text.ToString(); } }
        internal void Append(char[] buffer, int count)
        {
            lock (_sync) { _text.Append(buffer, 0, count); if (_text.Length > 8192) _text.Remove(0, _text.Length - 8192); }
        }
    }
    private static async Task DrainErrorsAsync(TextReader reader, ErrorTail errors)
    {
        // Retain only a bounded native stack tail; drain the remainder so stderr cannot block acquisition.
        try
        {
            var buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0) errors.Append(buffer, count);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private int? Stop(bool graceful = false)
    {
        var child = _process; _process = null;
        if (child is null) return null;
        try
        {
            if (graceful && !child.HasExited) { child.StandardInput.Close(); child.WaitForExit(500); }
            if (!child.HasExited) { _terminate(child); child.WaitForExit(500); }
            return child.HasExited ? child.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        finally
        {
            // This process is strictly read-only. Even so, never lose its
            // identity and spawn an unbounded series of blocked readers.
            if(child.HasExited)child.Dispose();
            else lock(RetiredGate)UnconfirmedReaders.Add(child);
        }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Stop(graceful: true); }
    }
}
