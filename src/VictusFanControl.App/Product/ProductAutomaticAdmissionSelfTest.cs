using VictusFanControl.Product;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.App;

internal static class ProductAutomaticAdmissionSelfTest
{
    internal static void Run(Action<bool, string> require)
    {
        var at = new DateTimeOffset(2026, 10, 11, 3, 0, 0, TimeSpan.Zero);
        TelemetrySnapshot Sample(double? gpuPower = 83.016) => new(at, "CPU", 55, 20, 30,
            "GPU", 45, gpuPower, 80, 4900, 4900)
        {
            CpuExpectedPhysicalCoreCount = 1, CpuCoreTemperatures = [new(0, 0, "Performance", 55)],
            ProductTelemetryTolerance = true, FanMaximumAgeMilliseconds = 10000
        };
        ProductRuntimeState State(TelemetrySnapshot sample) => new()
        {
            Target = "HP-8C40-9D0R1LA-F18", AutomaticAuthorized = true,
            Runtime = "Healthy", Source = "Ac", CanApplyPerformance = true, Snapshot = sample
        };
        SafetyGateResult Safety(TelemetrySnapshot sample) => new(true, true, true, true, true, true,
            false, true, true, true, sample.Timestamp, sample.Timestamp, 1, Array.Empty<string>());
        void Code(TelemetrySnapshot? sample, string code, DateTimeOffset? now = null) =>
            require(ProductAutomaticAdmission.TelemetryIssues(sample, now ?? at).Any(i => i.Code == code),
                "Admission lost the concrete cause: " + code);

        foreach (var mode in Enum.GetValues<ProductAutomaticReviewMode>())
        foreach (var power in new[] { 75d, 81.251, 83.016, 115, 300 })
        {
            var sample = Sample(power);
            require(ProductAutomaticAdmission.StartupIssues(State(sample), new(), at).Count == 0,
                "GPU consumption still blocks startup Automatic.");
            require(ProductAutomaticAdmission.RuntimeIssues(sample, at, Safety(sample)).Count == 0,
                "GPU consumption still blocks runtime admission.");
            var guard = new ProductAutomaticReview(() => 0, mode); guard.Start();
            guard.Observe(sample, Safety(sample)); guard.EnsureDispatchAllowed(sample);
        }
        foreach (var invalid in new double?[] { null, double.NaN, -1, 300.001 })
        {
            var sample = Sample(invalid); Code(sample, invalid.HasValue ? "gpu-power-invalid" : "gpu-power-missing");
            var guard = new ProductAutomaticReview(() => 0, ProductAutomaticReviewMode.Habitual); guard.Start();
            var rejected = false; try { guard.Observe(sample, Safety(sample)); } catch (InvalidOperationException) { rejected = true; }
            require(rejected, "Removing the consumption cutoff admitted invalid GPU telemetry.");
        }
        foreach (var sample in new[] { Sample() with { GpuTemperatureC = 95.001 } })
        {
            require(ProductAutomaticAdmission.StartupIssues(State(sample), new(), at).Count > 0,
                "Consumption change disabled the GPU thermal protection at entry.");
            foreach (var mode in Enum.GetValues<ProductAutomaticReviewMode>())
            {
                var guard = new ProductAutomaticReview(() => 0, mode); guard.Start();
                var rejected = false; try { guard.Observe(sample, Safety(sample)); } catch (InvalidOperationException) { rejected = true; }
                require(rejected, "Consumption change disabled the GPU thermal protection in operation.");
            }
        }
        foreach(var temperature in new[]{82d,83,87,94.999,95})
        {
            var sample=Sample() with{GpuTemperatureC=temperature};
            require(ProductAutomaticAdmission.StartupIssues(State(sample),new(),at).Count==0,"Habitual GPU entry rejected <=95 C.");
            var policy=new ProductProtectionSettings();
            var effective=policy.ApplyThermalPolicy(sample,Safety(sample) with{ThermalEmergency=true,PreconditionsReady=false});
            require(effective.CustomControlPermitted&&!effective.ThermalEmergency,"Historical raw GPU gate leaked into habitual admission.");
            var guard=new ProductAutomaticReview(()=>0,ProductAutomaticReviewMode.Habitual);guard.Configure(policy);guard.Start();
            guard.Observe(sample,effective);guard.EnsureDispatchAllowed(sample);
        }
        foreach(var temperature in new[]{95.001,96d,105})
            require(new ProductProtectionSettings().IsGpuThermalEmergency(temperature),"GPU >95 did not hand off.");
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity,-1,105.001})
            Code(Sample() with{GpuTemperatureC=invalid},invalid.HasValue?"gpu-temperature-invalid":"gpu-temperature-missing");
        foreach(var mode in new[]{ProductAutomaticReviewMode.Short,ProductAutomaticReviewMode.Extended})
        {
            var sample=Sample() with{GpuTemperatureC=83};var guard=new ProductAutomaticReview(()=>0,mode);guard.Configure(new());guard.Start();
            var denied=false;try{guard.Observe(sample,Safety(sample));}catch(InvalidOperationException){denied=true;}
            require(denied,"Habitual GPU threshold weakened historical bounded review.");
        }
        require(!ProductProfilesStore.Serialize(new ProductProfiles{Protections=new(){HistoricalGpuThermalPolicy=true}}).Contains("historicalGpuThermalPolicy",StringComparison.OrdinalIgnoreCase),"Runtime-only historical thermal flag persisted.");
        Console.WriteLine("PASS habitual GPU: 82/83/87/94.999/exact95 admitted; >95 handoff; historical review and invalid telemetry preserved.");
        foreach(var watts in new[]{60d,60.001,61,75,115,500})
        foreach(var legacyFlag in new[]{false,true})
        {
            var sample=Sample(115) with{CpuPackagePowerW=watts};
            var preferences=new ProductProtectionSettings{PowerEnvelopeHandoff=legacyFlag};
            require(ProductAutomaticAdmission.StartupIssues(State(sample),preferences,at).Count==0,
                "Habitual startup still has a CPU consumption cutoff.");
            require(ProductAutomaticAdmission.RuntimeIssues(sample,at,Safety(sample),preferences).Count==0,
                "Habitual runtime still has a CPU consumption cutoff.");
            var guard=new ProductAutomaticReview(()=>0,ProductAutomaticReviewMode.Habitual);guard.Configure(preferences);guard.Start();
            guard.Observe(sample,Safety(sample));guard.EnsureDispatchAllowed(sample);
        }
        foreach(var mode in new[]{ProductAutomaticReviewMode.Short,ProductAutomaticReviewMode.Extended})
        {
            var sample=Sample() with{CpuPackagePowerW=61};
            var guard=new ProductAutomaticReview(()=>0,mode);guard.Start();
            var rejected=false;try{guard.Observe(sample,Safety(sample));}catch(InvalidOperationException){rejected=true;}
            require(rejected,"Habitual change bypassed the explicit bounded physical review envelope.");
        }
        foreach(var invalid in new double?[]{null,double.NaN,double.PositiveInfinity,-1,500.001})
        {
            var sample=Sample() with{CpuPackagePowerW=invalid};
            Code(sample,invalid.HasValue?"cpu-power-invalid":"cpu-power-missing");
            var guard=new ProductAutomaticReview(()=>0,ProductAutomaticReviewMode.Habitual);guard.Start();
            var rejected=false;try{guard.Observe(sample,Safety(sample));}catch(InvalidOperationException){rejected=true;}
            require(rejected,"Consumption change admitted invalid CPU telemetry.");
        }
        var oldJson=ProductProfilesStore.Serialize(new ProductProfiles()).Replace("\"powerEnvelopeHandoff\": false","\"powerEnvelopeHandoff\": true");
        var imported=ProductProfilesStore.Parse(oldJson);
        require(!imported.Protections.PowerEnvelopeHandoff&&imported.Protections.CpuThermalHandoff&&imported.Protections.GpuThermalHandoff&&
            imported.Ac.CpuPl1Watts==35&&imported.Ac.CpuPl2Watts==60&&imported.Ac.GpuMaximumMHz==1850,
            "Legacy cutoff migration changed thermal preferences or configured CPU/GPU limits.");
        require(!ProductProfilesStore.Copy(new ProductProfiles{Protections=new(){PowerEnvelopeHandoff=true}}).Protections.PowerEnvelopeHandoff,
            "Saving an old enabled consumption preference reintroduced the cutoff.");
        require(!imported.Protections.PowerEnvelopeHandoff &&
            ProductAutomaticAdmission.StartupIssues(State(Sample(115)), imported.Protections, at).Count == 0,
            "Legacy enabled power preference restored the removed consumption cutoff.");

        Code(null, "no-snapshot");
        Code(Sample() with { CpuTemperatureC = null }, "cpu-temperature-missing");
        Code(Sample() with { GpuTemperatureC = null }, "gpu-temperature-missing");
        Code(Sample() with { CpuPackagePowerW = null }, "cpu-power-missing");
        Code(Sample() with { CpuFanRpm = null }, "cpu-rpm-missing");
        Code(Sample() with { GpuFanRpm = null }, "gpu-rpm-missing");
        Code(Sample() with { CpuCoreTemperatures = [] }, "cpu-cores-incomplete");
        Code(Sample() with { CpuCoreTemperatures = [new(0, 0, "Performance", double.NaN)] }, "cpu-cores-invalid");
        Code(Sample() with { CpuExpectedPhysicalCoreCount = 2, CpuCoreTemperatures = [new(0, 0, "Performance", 55), new(0, 1, "Performance", 55)] }, "cpu-cores-invalid");
        Code(Sample() with { RetainedTelemetry = "CpuTemperatureC" }, "retained-fields");
        Code(Sample() with { CpuThermalSampledAtUtc = at.AddSeconds(-5) }, "cpu-thermal-stale");
        Code(Sample() with { GpuThermalSampledAtUtc = at.AddMilliseconds(1) }, "gpu-thermal-future");
        Code(Sample() with { CpuPowerSampledAtUtc = at.AddSeconds(-5) }, "cpu-power-stale");
        Code(Sample() with { GpuPowerSampledAtUtc = at.AddSeconds(-5) }, "gpu-power-stale");
        require(ProductAutomaticAdmission.TelemetryIssues(Sample() with { CpuThermalSampledAtUtc = at.AddMilliseconds(-4999) }, at).Count == 0,
            "The critical sensor freshness boundary changed.");
        require(ProductAutomaticAdmission.TelemetryIssues(Sample() with { CpuLoadPercent = null, GpuLoadPercent = null }, at).Count == 0,
            "Missing optional load began blocking product entry.");
        var fan = Sample() with
        {
            FanTelemetrySource = "HP-WMI-ACPI-2D", FanRpmResolution = 100,
            FanSampledAtUtc = at.AddMilliseconds(-2000), FanAgeCapturedAtUtc = at,
            FanSampleAgeMilliseconds = 2000
        };
        require(ProductAutomaticAdmission.TelemetryIssues(fan, at.AddMilliseconds(999)).Count == 0,
            "Fresh WMI RPM were rejected before the entry deadline.");
        Code(fan, "fan-stale", at.AddMilliseconds(1000));
        Code(fan with { FanSampleAgeMilliseconds = null }, "fan-metadata");
        Code(fan with { FanAgeCapturedAtUtc = at.AddMilliseconds(1) }, "fan-clock");
        require(ProductAutomaticAdmission.RuntimeIssues(Sample(), at, Safety(Sample()) with { RuntimeHealthy = false }, runtime: "Degraded")
            .Any(i => i.Code == "runtime" && i.Detail.Contains("Degraded", StringComparison.Ordinal)),
            "Runtime rejection lost the actual controller state.");
        require(ProductAutomaticAdmission.RuntimeIssues(Sample(), at, Safety(Sample()) with { FanWritePathPresent = false })
            .Any(i => i.Code == "fan-backend"), "Missing control backend kept a generic rejection.");
        var gate = new ProductStartupAutomatic(0);
        require(!gate.Observe(State(Sample() with { CpuTemperatureC = null }), at, 0) &&
            gate.Issues.Any(i => i.Code == "cpu-temperature-missing") && gate.Status.Contains("sin lectura", StringComparison.Ordinal),
            "Startup wait lost its specific missing sensor.");
        for (var i = 0; i < 3; i++)
        {
            var sample = Sample(115) with { Timestamp = at.AddSeconds(i) };
            require(gate.Observe(State(sample), sample.Timestamp, i * 1000) == (i == 2) && gate.Issues.Count == 0,
                "Recovered startup did not count three distinct fresh high-GPU-power acquisitions.");
        }
        Console.WriteLine("PASS: habitual CPU 60/60.001/61/75/115/500 W and GPU consumption admit startup/runtime/dispatch; legacy cutoff cleared; invalid telemetry, bounded reviews, thermal and freshness protections retained.");
    }
}
