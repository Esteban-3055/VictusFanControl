namespace VictusFanControl.Control.Adaptive;

internal static class CpuThermalDemandFilterSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
            var origin = DateTimeOffset.UnixEpoch;
            AdaptiveFanPolicyInput Input(double seconds, double raw = 50, double mean = 50) =>
                new(origin.AddSeconds(seconds), mean, 0, 0, 35, 0, 0) { CpuRawControlTemperatureC = raw };
            var filter = new CpuThermalDemandFilter();
            AdaptiveFanPolicyInput Apply(AdaptiveFanPolicyInput input)
            {
                Check(filter.TryApply(input, TimeSpan.FromSeconds(3), out var result, out var reason), reason);
                return result;
            }
            Apply(Input(0));
            Check(Apply(Input(1, 91, 80)).CpuRawControlTemperatureC == 50, "Single package/core spike reached demand.");
            Check(Apply(Input(2)).CpuEffectiveTemperatureC == 50, "Cooling did not pass immediately.");
            for (var s = 3; s < 20; s++)
                Check(Apply(Input(s, s % 2 == 1 ? 94 : 70, s % 2 == 1 ? 85 : 60)).CpuRawControlTemperatureC < 85,
                    "Separated spikes accumulated into sustained heat.");
            filter.Reset(); Apply(Input(0));
            Check(Apply(Input(2.5, 91, 80)).CpuRawControlTemperatureC == 50, "Sparse first hot observation qualified early.");
            Check(Apply(Input(3, 91, 80)).CpuRawControlTemperatureC == 50, "Irregular polling fabricated 2s of heat.");
            Check(Apply(Input(4.5, 91, 80)).CpuRawControlTemperatureC == 91, "Continuous heat was delayed past confirmation.");
            Check(Apply(Input(5, 50)).CpuEffectiveTemperatureC == 50, "Filter delayed a real temperature drop.");
            filter.Reset();
            Check(Apply(Input(0, 91, 80)).CpuRawControlTemperatureC == 91, "First hot sample fabricated a cold start.");
            filter.Reset(); Apply(Input(0));
            Check(Apply(Input(1, 99, 85)).CpuRawControlTemperatureC == 99 && filter.Observation!.ExtremeBypass,
                "99C was hidden by the spike window.");
            Check(!filter.TryApply(Input(1, 100), TimeSpan.FromSeconds(3), out _, out _), "Duplicate raw epoch accepted.");
            Check(!filter.TryApply(Input(2) with { CpuEffectiveTemperatureC = double.NaN }, TimeSpan.FromSeconds(3), out _, out _),
                "Invalid raw telemetry was turned into valid filtered data.");
            Apply(Input(3));
            Check(!filter.TryApply(Input(7, 91), TimeSpan.FromSeconds(3), out _, out _), "Gap accumulated confirmation.");
            Check(Apply(Input(8, 91)).CpuRawControlTemperatureC == 91, "Unknown history assumed a cold recovery.");
            filter.Reset(); Apply(Input(0)); Apply(Input(1, 91, 80));
            Check(Apply(Input(2, 91, 80) with { CpuThermalRetained = true }).CpuRawControlTemperatureC == 50,
                "Retained CPU acquisition confirmed heat.");
            Check(Apply(Input(3, 91, 80)).CpuRawControlTemperatureC == 50 &&
                Apply(Input(4, 91, 80)).CpuRawControlTemperatureC == 50 &&
                Apply(Input(5, 91, 80)).CpuRawControlTemperatureC == 91,
                "Fresh recovery counted retained epochs toward confirmation.");
            filter.Reset(); Apply(Input(0));
            Check(Apply(Input(1, 99, 80) with { CpuThermalRetained = true }).CpuRawControlTemperatureC == 99,
                "Partially retained telemetry hid an extreme fresh package/core.");

            var fan = Product.ProductProfiles.DefaultProfile(Product.ProductPowerProfile.Ac).Fan;
            fan = fan with { UnifiedDemand = new() { Curve = [new(0, 10), new(90, 10), new(100, 50)] } };
            var tuning = fan.Tuning with { RiseTimeConstantSeconds = .5, IncreaseConfirmationSeconds = 0,
                ThermalDecreaseHoldSeconds = 30, ShortLoadDecreaseConfirmationSeconds = 2 };
            AdaptiveFanInertiaPolicy Policy() => new(fan.BuildPolicy(), tuning, cpuSpikeFilterEnabled: true);
            var p = Policy();
            Check(p.Evaluate(Input(0)).EqualFanLevel == 10, "Cold baseline changed.");
            var isolated = p.Evaluate(Input(1, 91, 60));
            Check(!isolated.ThermalOverride && isolated.UnifiedDemand!.Percent < 100 && isolated.EqualFanLevel == 10 &&
                isolated.CpuThermalDemand!.SpikeSuppressed, "Moderate spike caused a thermal floor or fast rise.");
            Check(p.Evaluate(Input(2)).EqualFanLevel == 10, "Discarded spike remained in actuation.");
            p.Reset(); p.Evaluate(Input(0)); p.Evaluate(Input(1, 91, 60));
            Check(p.ObserveDuringActuation(Input(2, 91, 60), out _), "Fresh ACK acquisition rejected.");
            var sustained = p.Evaluate(Input(3, 91, 60));
            Check(sustained.ThermalOverride && sustained.ActuationDemandLevel == 50 && sustained.EqualFanLevel == 14,
                "ACK history failed sustained heat or lost protected four-level rise.");
            p.Reset(); p.Evaluate(Input(0));
            var extreme = p.Evaluate(Input(1, 100, 60));
            Check(extreme.ThermalOverride && extreme.UnifiedDemand!.Percent == 100 && extreme.EqualFanLevel == 14,
                "Extreme CPU temperature was delayed.");
            for (var s = 2; s <= 28; s++) p.Evaluate(Input(s));
            p.Evaluate(Input(29, 86));
            for (var s = 30; s <= 32; s++) p.Evaluate(Input(s));
            Check(p.Evaluate(Input(33)).EqualFanLevel == 13, "Isolated moderate heat renewed the 30s cooling hold.");
            p.Reset(); p.Evaluate(Input(0));
            var gpu = p.Evaluate(Input(1) with { GpuTemperatureC = 81 });
            Check(gpu.ThermalOverride && gpu.UnifiedDemand!.Percent == 100, "CPU filtering weakened GPU response.");
            p.Reset(); p.Evaluate(Input(0));
            var power = p.Evaluate(Input(1, 91) with { CpuPackagePowerW = 60 });
            Check(power.UnifiedDemand!.CpuPower == 40, "CPU spike filter changed power contribution.");
            Check(power.CpuThermalDemand!.RawControlC == 91 && power.CpuThermalDemand.ControlC == 50,
                "Diagnostic lost original versus filtered CPU temperatures.");
            output.WriteLine("PASS CPU spike filter: 2s continuous heat, irregular polling, isolated/repeated spikes, raw validation, gaps, hot startup, immediate 99C, ACK history, cooling hold, GPU and power isolation.");
            return 0;
        }
        catch (Exception ex) { output.WriteLine("FAIL CPU spike filter: " + ex); return 1; }
    }
}
