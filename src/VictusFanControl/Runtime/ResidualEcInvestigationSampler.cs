using System.Diagnostics;
using System.Globalization;
using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.PawnIo;

namespace VictusFanControl.Runtime;

/// <summary>Scenario C only: production coherent setpoint + guards, never tachometers.</summary>
internal sealed class ResidualEcInvestigationSampler : IDisposable
{
    internal readonly record struct Sample(byte CpuSetpoint, byte GpuSetpoint, byte MaxFan, byte FanSwitch);
    private readonly Func<Sample> _read;
    private readonly TextWriter _output;
    private readonly IDisposable? _hardware;
    private readonly TimeSpan _interval;
    private TimeSpan _next;
    internal string? Fault { get; private set; }
    internal int Samples { get; private set; }

    internal ResidualEcInvestigationSampler(Func<Sample> read, TextWriter output,
        TimeSpan interval, IDisposable? hardware = null)
    {
        if (interval < TimeSpan.FromSeconds(2) || interval > TimeSpan.FromSeconds(60))
            throw new ArgumentOutOfRangeException(nameof(interval));
        _read = read; _output = output; _interval = interval; _hardware = hardware;
        _output.WriteLine("timestamp_utc,elapsed_ms,duration_ms,cpu_setpoint_hex,gpu_setpoint_hex,max_fan_hex,fan_switch_hex,status,error");
        _output.Flush();
    }

    internal static ResidualEcInvestigationSampler Create(string modulesDirectory,
        string outputPath, int intervalMs, HardwareTargetProfile? target)
    {
        WmiOnlyInvestigationPolicy.EnsureTargetAllowed(target);
        if (!WmiOnlyInvestigationPolicy.ResidualEcAllowed || target != Hp8C40TargetProfile.Instance)
            throw new InvalidOperationException("Scenario C requires its restricted policy and qualified 8C40 target.");
        var ec = new AcpiEcReader(Path.Combine(modulesDirectory, "LpcACPIEC.bin"));
        StreamWriter? output = null;
        try
        {
            output = new StreamWriter(outputPath, false, new System.Text.UTF8Encoding(false));
            return new ResidualEcInvestigationSampler(() =>
            {
                var layout = Hp8C40TargetProfile.Instance.FanEcLayout;
                var setpoint = ec.ReadStableFanSetpoint(layout);
                var guard = ec.ReadFanControlGuard(layout);
                return new Sample(setpoint.CpuSetpoint, setpoint.GpuSetpoint, guard.MaxFan, guard.FanSwitch);
            }, output, TimeSpan.FromMilliseconds(intervalMs), ec);
        }
        catch { output?.Dispose(); ec.Dispose(); throw; }
    }

    internal bool ReadIfDue(TimeSpan elapsed)
    {
        if (Fault is not null) return false;
        if (elapsed < _next) return true;
        // One batch only, even after a long delay; no accumulated catch-up work.
        var stamp = DateTimeOffset.UtcNow.ToString("o");
        var watch = Stopwatch.StartNew();
        EcWmiInvestigationTrace.Record(0, "isolation.ec-sample.begin", $"sample={Samples + 1}");
        try
        {
            var sample = _read();
            _next = elapsed + watch.Elapsed + _interval;
            Samples++;
            _output.WriteLine(FormattableString.Invariant($"{stamp},{elapsed.TotalMilliseconds:F3},{watch.Elapsed.TotalMilliseconds:F3},0x{sample.CpuSetpoint:X2},0x{sample.GpuSetpoint:X2},0x{sample.MaxFan:X2},0x{sample.FanSwitch:X2},complete,"));
            _output.Flush();
            EcWmiInvestigationTrace.Record(0, "isolation.ec-sample.end",
                $"sample={Samples};cpu=0x{sample.CpuSetpoint:X2};gpu=0x{sample.GpuSetpoint:X2};max=0x{sample.MaxFan:X2};switch=0x{sample.FanSwitch:X2}");
            return true;
        }
        catch (Exception ex)
        {
            Fault = ex.GetType().Name + ": " + ex.Message;
            EcWmiInvestigationTrace.Record(0, "isolation.ec-sample.failure", Fault);
            _output.WriteLine(string.Join(',', stamp,
                elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                "", "", "", "", "failed", '"' + Fault.Replace("\"", "\"\"") + '"'));
            _output.Flush();
            return false; // Existing per-transaction retries already ran. No reinitialization loop.
        }
    }

    public void Dispose() { try { _output.Dispose(); } finally { _hardware?.Dispose(); } }
}
