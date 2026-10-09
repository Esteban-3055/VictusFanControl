namespace VictusFanControl.Control.Adaptive;

/// <summary>Observed workload history, not a measurement of chassis temperature.</summary>
internal sealed class AdaptiveLoadHistory
{
    private DateTimeOffset? _last;
    private bool _previousLoaded;
    private double _idleSeconds;
    public double ObservedLoadSeconds { get; private set; }
    public bool SustainedLoadCooling { get; private set; }

    public void Observe(AdaptiveFanPolicyInput input, AdaptiveFanTuning? tuning)
    {
        if (tuning is not { AdaptiveDescentEnabled: true }) { Reset(); return; }
        var elapsed = _last.HasValue ? (input.Timestamp - _last.Value).TotalSeconds : 0;
        var loaded = input.CpuLoadPercent >= tuning.LoadThresholdPercent ||
            input.GpuLoadPercent >= tuning.LoadThresholdPercent ||
            input.CpuPackagePowerW >= tuning.CpuLoadPowerThresholdW ||
            input.GpuPowerW >= tuning.GpuLoadPowerThresholdW;
        // Count only intervals bounded by loaded samples; pauses are tolerated
        // but not counted toward the twenty-minute qualification.
        if (loaded)
        {
            if (_previousLoaded) ObservedLoadSeconds += elapsed;
            _idleSeconds = 0;
            if (ObservedLoadSeconds >= tuning.SustainedLoadSeconds)
                SustainedLoadCooling = true;
        }
        else
        {
            _idleSeconds += elapsed;
            if (!SustainedLoadCooling && _idleSeconds > tuning.LoadPauseToleranceSeconds)
                ObservedLoadSeconds = 0;
            if (SustainedLoadCooling && _idleSeconds >= tuning.SustainedLoadCooldownSeconds)
            {
                SustainedLoadCooling = false;
                ObservedLoadSeconds = 0;
            }
        }
        _last = input.Timestamp;
        _previousLoaded = loaded;
    }

    // Missing/discontinuous telemetry cannot certify cooling or loaded time.
    // Keep an already qualified slow descent until fresh idle evidence exists.
    public void BreakContinuity()
    {
        _last = null; _previousLoaded = false; _idleSeconds = 0;
        if (!SustainedLoadCooling) ObservedLoadSeconds = 0;
    }
    public void Reset()
    {
        SustainedLoadCooling = false; ObservedLoadSeconds = 0;
        BreakContinuity();
    }
}
