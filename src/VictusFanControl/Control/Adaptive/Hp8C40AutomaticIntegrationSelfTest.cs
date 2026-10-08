using VictusFanControl.Hardware.Hp;
using VictusFanControl.Hardware.Windows;
using VictusFanControl.Runtime;
using VictusFanControl.Safety;
using VictusFanControl.Telemetry;

namespace VictusFanControl.Control.Adaptive;

internal static class Hp8C40AutomaticIntegrationSelfTest
{
    private static readonly HardwareIdentity Hardware = new(
        Hp8C40TargetProfile.BoardManufacturer, Hp8C40TargetProfile.BoardProduct,
        Hp8C40TargetProfile.BoardVersion, Hp8C40TargetProfile.SystemManufacturer,
        Hp8C40TargetProfile.SystemProductName, $"{Hp8C40TargetProfile.SystemSkuPrefix}#AKH",
        Hp8C40TargetProfile.ValidatedBiosVersion);

    internal static async Task<int> RunAsync(TextWriter output)
    {
        var failures = 0;
        void Check(bool pass, string name)
        {
            output.WriteLine($"{(pass ? "PASS" : "FAIL")}  prepared Automatic: {name}");
            if (!pass) failures++;
        }

        // The real feature gate stays closed even with the new implementation installed.
        var closedBackend = new Backend();
        await using (var coordinator = new FanControlCoordinator(closedBackend))
        {
            var controller = new AdaptiveFanProductionController(coordinator, Hp8C40AdaptiveCandidateV1.Create(),
                true, Hp8C40PostM9UserControlGate.AutomaticExecutionAuthorized, automaticHardware: Hardware);
            var mode = await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var snapshot = Sample(DateTimeOffset.UtcNow);
            var blocked = await controller.ProcessAutomaticAsync(snapshot, Raw(snapshot), CancellationToken.None);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Manual, CancellationToken.None);
            await controller.ApplyManualAsync(10, Raw(snapshot), CancellationToken.None);
            Check(mode.Action == AdaptiveFanProductionActionKind.Blocked && !blocked.ExecutionAuthorized &&
                closedBackend.Levels.SequenceEqual(new[] { 10 }) && !controller.AutomaticFreshAcquisitionRequired,
                "closed gate issues no Automatic command; Manual still permits 10");
        }

        var origin = DateTimeOffset.UtcNow;
        long clock = 0;
        DateTimeOffset Now() => origin.AddMilliseconds(clock);
        AdaptiveFanProductionController Create(FanControlCoordinator coordinator) =>
            new(coordinator, Hp8C40AdaptiveCandidateV1.Create(), true, true,
                automaticHardware: Hardware, automaticMilliseconds: () => clock, utcNow: Now);

        var backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var first = Sample(Now(), 71);
            var initial = await controller.ProcessAutomaticAsync(first, Raw(first), CancellationToken.None);
            clock = 100;
            var high = Sample(Now(), 98);
            var raw = Raw(high);
            var effective = controller.EvaluateAutomaticSafety(high, raw, observe: true);
            // Many display/dispatch checks must not turn one CPU spike into five.
            var previewsPermitted = true;
            for (var i = 0; i < 10; i++)
                previewsPermitted &= controller.EvaluateAutomaticSafety(high, Display(high, Now()), observe: false).CustomControlPermitted;
            Check(previewsPermitted, "ten previews preserve the single counted high epoch");
            var raised = await controller.ProcessAutomaticAsync(high, effective, CancellationToken.None);
            Check(initial.EqualFanLevel == 31 && raised.EqualFanLevel == 35 && raised.ThermalOverride &&
                controller.AutomaticAcquisitionBudgetMilliseconds == 2000 &&
                backend.Levels.SequenceEqual(new[] { 31, 35 }), "raw 98 C raises 31 -> 35 without EMA delay");
            var sequenceBefore = raw.EvaluationSequence;
            var sequenceAfter = Raw(high).EvaluationSequence;
            Check(sequenceAfter == sequenceBefore + 1, "display and dispatch consume no control sequence numbers");
            clock = 2100;
            var cooled = Sample(Now());
            var stopped = await controller.ProcessAutomaticAsync(cooled, Raw(cooled), CancellationToken.None);
            clock = 2200;
            var later = Sample(Now());
            var latched = await controller.ProcessAutomaticAsync(later, Raw(later), CancellationToken.None);
            Check(stopped.Action == AdaptiveFanProductionActionKind.RestoreFirmware &&
                latched.Action == AdaptiveFanProductionActionKind.HoldFirmware &&
                backend.Levels.Count == 2 && backend.Restores == 1 && !controller.AutomaticFreshAcquisitionRequired,
                "late cold telemetry cannot reopen expired admission");
            await controller.SetModeAsync(AdaptiveFanProductionMode.Firmware, CancellationToken.None);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var restarted = await controller.ProcessAutomaticAsync(later, Raw(later), CancellationToken.None);
            Check(restarted.EqualFanLevel == 30, "explicit Firmware -> Automatic starts a new cold session at floor 30");
        }

        // Average is a demand source; emergency admission still observes raw cores.
        clock = 0;
        var averageBackend = new Backend();
        await using (var coordinator = new FanControlCoordinator(averageBackend))
        {
            var configuration = new FanConfiguration();
            var controller = new AdaptiveFanProductionController(coordinator, Hp8C40AdaptiveCandidateV1.Create(), true, true,
                automaticHardware: Hardware, automaticMilliseconds: () => clock, utcNow: Now, automaticConfiguration: configuration);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var isolatedHot = Sample(Now()) with
            {
                GpuTemperatureC = 35,
                CpuCoreTemperatures = Sample(Now()).CpuCoreTemperatures.Select((c,i)=>c with { TemperatureC=i==0 ? 90 : 45 }).ToArray()
            };
            var preview = new AdaptiveFanPolicyShadowEvaluator(Hardware, configuration.BuildPolicy(), true, configuration);
            var shadow = preview.Evaluate(SystemState.Healthy, isolatedHot, Now());
            var normal = await controller.ProcessAutomaticAsync(isolatedHot, Raw(isolatedHot), CancellationToken.None);
            Check(normal.EqualFanLevel==30 && !normal.ThermalOverride && shadow.RecommendedEqualLevel==30,
                "isolated 90 C core does not drive Average demand; preview/controller agree at the prepared 30 floor");
            clock = 100;
            var emergency = isolatedHot with { Timestamp=Now(),
                CpuCoreTemperatures=isolatedHot.CpuCoreTemperatures.Select((c,i)=>c with { TemperatureC=i==0 ? 99 : 45 }).ToArray() };
            var stopped = await controller.ProcessAutomaticAsync(emergency, Raw(emergency), CancellationToken.None);
            var shadowStopped = preview.Evaluate(SystemState.Healthy, emergency, Now());
            Check(stopped.Action==AdaptiveFanProductionActionKind.RestoreFirmware && averageBackend.Restores==1 &&
                averageBackend.Levels.SequenceEqual(new[]{30}) && shadowStopped.EffectiveThermalEmergency && !shadowStopped.PolicyAccepted,
                "raw core at 99 C restores Firmware immediately despite Average below 50 C");
        }

        // Product review opts into raw-heat response. Historical prepared
        // qualification settings remain reproducible above.
        clock=0;var productBackend=new Backend();
        await using(var coordinator=new FanControlCoordinator(productBackend))
        {
            var configuration=new FanConfiguration();
            var controller=new AdaptiveFanProductionController(coordinator,configuration.BuildPolicy(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,
                automaticConfiguration:configuration,useRawCpuThermalResponse:true);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,CancellationToken.None);
            var cold=Sample(Now(),63) with{GpuTemperatureC=35,CpuCoreTemperatures=Sample(Now()).CpuCoreTemperatures.Select(c=>c with{TemperatureC=45}).ToArray()};
            var initial=await controller.ProcessAutomaticAsync(cold,Raw(cold),CancellationToken.None);
            clock=100;var spike=cold with{Timestamp=Now(),CpuTemperatureC=96};
            var raw=Raw(spike);var effective=controller.EvaluateAutomaticSafety(spike,raw,observe:true);
            var raised=await controller.ProcessAutomaticAsync(spike,raw,CancellationToken.None);
            Check(raw.ThermalEmergency&&effective.CustomControlPermitted&&initial.EqualFanLevel==30&&
                raised.ThermalOverride&&raised.EqualFanLevel==34&&raised.ActuationDemandLevel>=44,
                "product review CPU package 96 C raises fans immediately despite cool core-average demand");
            clock=1200;var recovered=cold with{Timestamp=Now(),CpuTemperatureC=62};
            var held=await controller.ProcessAutomaticAsync(recovered,Raw(recovered),CancellationToken.None);
            Check(held.Action==AdaptiveFanProductionActionKind.HoldCustom&&held.EqualFanLevel==34&&
                coordinator.Authority==FanAuthority.Custom&&productBackend.Restores==0&&controller.AutomaticAcquisitionBudgetMilliseconds is null,
                "timely recovered product spike retains Automatic and slow descent");
            clock=1300;var warm=cold with{Timestamp=Now(),CpuTemperatureC=93};
            var cooling=await controller.ProcessAutomaticAsync(warm,Raw(warm),CancellationToken.None);
            Check(cooling.ThermalOverride&&cooling.ActuationDemandLevel>=44&&cooling.EqualFanLevel==38&&
                coordinator.Authority==FanAuthority.Custom&&productBackend.Restores==0&&controller.AutomaticAcquisitionBudgetMilliseconds is null,
                "established raw CPU 93 C keeps protected cooling without a confirmation deadline in the legacy response");
            clock=1400;var critical=cold with{Timestamp=Now(),CpuTemperatureC=99};
            var stopped=await controller.ProcessAutomaticAsync(critical,Raw(critical),CancellationToken.None);
            Check(stopped.Action==AdaptiveFanProductionActionKind.RestoreFirmware&&productBackend.Restores==1,
                "product raw response preserves immediate CPU 99 C handoff");
        }

        clock=0;var unifiedBackend=new Backend();
        await using(var coordinator=new FanControlCoordinator(unifiedBackend))
        {
            var configuration=VictusFanControl.Product.ProductProfiles.DefaultProfile(VictusFanControl.Product.ProductPowerProfile.Ac).Fan;
            var controller=new AdaptiveFanProductionController(coordinator,configuration.BuildPolicy(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,
                automaticConfiguration:configuration,automaticMinimumLevel:10,useRawCpuThermalResponse:true);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,CancellationToken.None);
            var cold=Sample(Now(),63) with{GpuTemperatureC=35,CpuCoreTemperatures=Sample(Now()).CpuCoreTemperatures.Select(c=>c with{TemperatureC=45}).ToArray()};
            var initial=await controller.ProcessAutomaticAsync(cold,Raw(cold),CancellationToken.None);
            clock=700;var warm=cold with{Timestamp=Now(),CpuTemperatureC=93};
            var cooling=await controller.ProcessAutomaticAsync(warm,Raw(warm),CancellationToken.None);
            Check(initial.EqualFanLevel.HasValue&&cooling.ThermalOverride&&cooling.ActuationDemandLevel==50&&
                cooling.EqualFanLevel==Math.Min(50,initial.EqualFanLevel.Value+4)&&coordinator.Authority==FanAuthority.Custom&&
                unifiedBackend.Restores==0&&controller.AutomaticAcquisitionBudgetMilliseconds is null,
                "unified product CPU 93 C retains maximum thermal target and protected four-level rise without a confirmation deadline");
        }

        foreach (var source in new[]{CpuDemandTemperatureSource.PerformanceCoreAverage, CpuDemandTemperatureSource.HottestPerformanceCoresAverage})
        {
            clock=0;
            var pBackend=new Backend();
            await using var pCoordinator=new FanControlCoordinator(pBackend);
            var settings=new FanConfiguration {Tuning=new AdaptiveFanTuning {CpuTemperatureSource=source,HottestPerformanceCoreCount=2}};
            var pController=new AdaptiveFanProductionController(pCoordinator,settings.BuildPolicy(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,automaticConfiguration:settings);
            await pController.SetModeAsync(AdaptiveFanProductionMode.Automatic,CancellationToken.None);
            var snapshot=Sample(Now()) with {GpuTemperatureC=35,
                CpuCoreTemperatures=Sample(Now()).CpuCoreTemperatures.Select((c,i)=>c with {TemperatureC=i==0?90:45}).ToArray()};
            var pShadow=new AdaptiveFanPolicyShadowEvaluator(Hardware,settings.BuildPolicy(),true,settings);
            var shadow=pShadow.Evaluate(SystemState.Healthy,snapshot,Now());
            var actual=await pController.ProcessAutomaticAsync(snapshot,Raw(snapshot),CancellationToken.None);
            Check(actual.EqualFanLevel==30 &&
                shadow.RecommendedEqualLevel==actual.EqualFanLevel,
                $"{source}: configured N=2 reaches preview/controller consistently without dropping below the prepared 30 floor");
            clock=100;
            var emergency=snapshot with {Timestamp=Now(),CpuCoreTemperatures=snapshot.CpuCoreTemperatures
                .Select((c,i)=>i==13?c with {TemperatureC=99}:c).ToArray()};
            var stopped=await pController.ProcessAutomaticAsync(emergency,Raw(emergency),CancellationToken.None);
            Check(stopped.Action==AdaptiveFanProductionActionKind.RestoreFirmware && pBackend.Restores==1 &&
                pShadow.Evaluate(SystemState.Healthy,emergency,Now()).EffectiveThermalEmergency,
                $"{source}: excluded E-Core at 99 C still restores Firmware immediately");
        }

        // Expiry after read-only EnterCustom preparation must prevent Apply dispatch.
        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var cold = Sample(Now(), 71);
            controller.EvaluateAutomaticSafety(cold, Raw(cold), observe: true);
            clock = 100;
            backend.AfterEnter = () => clock += 2000;
            var high = Sample(Now(), 98);
            var denied = false;
            try { await controller.ProcessAutomaticAsync(high, Raw(high), CancellationToken.None); }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && backend.Levels.Count == 0 && coordinator.Authority == FanAuthority.Firmware,
                "deadline after queued/preparation work blocks the first backend command");
        }

        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var cold = Sample(Now(), 71);
            await controller.ProcessAutomaticAsync(cold, Raw(cold), CancellationToken.None);
            await coordinator.RestoreFirmwareAsync("synthetic authority loss", CancellationToken.None);
            clock = 100;
            var next = Sample(Now(), 72);
            var lost = await controller.ProcessAutomaticAsync(next, Raw(next), CancellationToken.None);
            Check(lost.Action == AdaptiveFanProductionActionKind.HoldFirmware && backend.Levels.Count == 1 &&
                !controller.AutomaticFreshAcquisitionRequired,
                "unexpected authority loss cannot silently reacquire Automatic control");
        }

        // Backend/native preparation can outlive the coordinator's initial check.
        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var cold = Sample(Now(), 71);
            await controller.ProcessAutomaticAsync(cold, Raw(cold), CancellationToken.None);
            clock = 100;
            backend.BeforeNative = async () =>
            {
                await Task.Yield();
                clock += 2000;
            };
            var high = Sample(Now(), 98);
            var denied = false;
            try { await controller.ProcessAutomaticAsync(high, Raw(high), CancellationToken.None); }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && backend.Levels.SequenceEqual(new[] { 31 }) && backend.Restores == 1 &&
                coordinator.Authority == FanAuthority.Firmware,
                "async native preparation rechecks deadline; release bypass remains available");
            backend.BeforeNative = null;
            await controller.SetModeAsync(AdaptiveFanProductionMode.Manual, CancellationToken.None);
            var manual = Sample(Now());
            await controller.ApplyManualAsync(10, Raw(manual), CancellationToken.None);
            Check(backend.Levels.SequenceEqual(new[] { 31, 10 }), "expired Automatic scope cannot leak into Manual");
        }

        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var cold = Sample(Now());
            await controller.ProcessAutomaticAsync(cold, Raw(cold), CancellationToken.None);
            AdaptiveFanProductionResult? result = null;
            for (var i = 1; i <= 5; i++)
            {
                clock = i * 100;
                var high = Sample(Now(), 95);
                result = await controller.ProcessAutomaticAsync(high, Raw(high), CancellationToken.None);
            }
            Check(result?.Action == AdaptiveFanProductionActionKind.RestoreFirmware && backend.Levels.Count == 5,
                "fifth unique high epoch restores firmware before the two-second limit");
        }

        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var warmup = Sample(Now()) with { CpuPackagePowerW = null };
            await controller.ProcessAutomaticAsync(warmup, Raw(warmup), CancellationToken.None);
            var display = controller.EvaluateAutomaticSafety(warmup, Display(warmup, Now()), false);
            clock = 100;
            var ready = Sample(Now());
            var admitted = await controller.ProcessAutomaticAsync(ready, Raw(ready), CancellationToken.None);
            Check(!display.CustomControlPermitted && admitted.EqualFanLevel == 30 && backend.Levels.Count == 1,
                "repeated cold warmup checks never write and allow the first complete healthy acquisition");
        }

        // A display on a not-yet-observed epoch cannot advance or poison acquisition.
        clock = 0;
        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var controller = Create(coordinator);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic, CancellationToken.None);
            var cold = Sample(Now());
            var preview = controller.EvaluateAutomaticSafety(cold, Display(cold, Now()), false);
            var started = await controller.ProcessAutomaticAsync(cold, Raw(cold), CancellationToken.None);
            clock = 100;
            var next = Sample(Now());
            var nextPreview = controller.EvaluateAutomaticSafety(next, Display(next, Now()), false);
            var held = await controller.ProcessAutomaticAsync(next, Raw(next), CancellationToken.None);
            Check(!preview.CustomControlPermitted && !nextPreview.CustomControlPermitted && started.EqualFanLevel == 30 &&
                held.Action == AdaptiveFanProductionActionKind.HoldCustom && backend.Levels.SequenceEqual(new[] { 30 }),
                "display before acquisition neither counts nor closes a healthy session; hold does not retransmit");
            var queuedOldDisplay = controller.EvaluateAutomaticSafety(cold, Display(cold, Now()), false);
            clock = 200;
            var current = Sample(Now());
            var continued = await controller.ProcessAutomaticAsync(current, Raw(current), CancellationToken.None);
            Check(!queuedOldDisplay.CustomControlPermitted && continued.Action == AdaptiveFanProductionActionKind.HoldCustom,
                "an old queued UI frame cannot close a newer healthy acquisition");
            clock = 300;
            var mismatched = Sample(Now());
            var mismatch = await controller.ProcessAutomaticAsync(mismatched, Raw(next), CancellationToken.None);
            Check(mismatch.Action == AdaptiveFanProductionActionKind.RestoreFirmware && backend.Levels.Count == 1,
                "mismatched original SafetyGate cannot be repaired into write permission");
        }
        // Stored tuning may request a lower floor, but the exact HP 8C40
        // Automatic runtime and its shadow must both reason inside 30..50.
        clock = 0;
        var configuredBackend = new Backend();
        await using (var configuredCoordinator = new FanControlCoordinator(configuredBackend))
        {
            var configured = new FanConfiguration
            {
                Tuning = new FanConfiguration().Tuning with { MinimumLevel = 28 }
            };
            var configuredController = new AdaptiveFanProductionController(
                configuredCoordinator,
                Hp8C40AdaptiveCandidateV1.Create(),
                true,
                true,
                automaticHardware: Hardware,
                automaticMilliseconds: () => clock,
                utcNow: Now,
                automaticConfiguration: configured);
            await configuredController.SetModeAsync(
                AdaptiveFanProductionMode.Automatic,
                CancellationToken.None);
            var low = Sample(Now());
            var actual = await configuredController.ProcessAutomaticAsync(
                low,
                Raw(low),
                CancellationToken.None);
            var configuredShadow = new AdaptiveFanPolicyShadowEvaluator(
                Hardware,
                configured.BuildPolicy(),
                preparedAutomatic: true,
                configuration: configured);
            var shadow = configuredShadow.Evaluate(
                SystemState.Healthy,
                low,
                Now());
            Check(actual.EqualFanLevel == Hp8C40AutomaticPolicy.MinimumLevel &&
                shadow.RecommendedEqualLevel == Hp8C40AutomaticPolicy.MinimumLevel &&
                configuredBackend.Levels.SequenceEqual(new[] { Hp8C40AutomaticPolicy.MinimumLevel }),
                "configured 28 floor is projected to the prepared 30 floor before Automatic smoothing and shadow planning");
        }

        backend = new Backend();
        await using (var coordinator = new FanControlCoordinator(backend))
        {
            var settings = new FanConfiguration();
            var controller = new AdaptiveFanProductionController(coordinator, Hp8C40AdaptiveCandidateV1.Create(),
                true, false, automaticHardware: Hardware, automaticConfiguration: settings);
            var replacement = settings with { Tuning = settings.Tuning with { MinimumLevel = 28 } };
            var persisted = 0;
            await controller.ConfigureAutomaticAsync(replacement, CancellationToken.None, _ => persisted++);
            Check(persisted == 1 && controller.AutomaticConfiguration!.Tuning.MinimumLevel == 28 &&
                controller.Mode == AdaptiveFanProductionMode.Firmware && !controller.AutomaticExecutionAuthorized &&
                backend.Levels.Count == 0 && backend.Restores == 0,
                "settings save in Firmware has no fan command and cannot authorize Automatic");
            var failed = false;
            try { await controller.ConfigureAutomaticAsync(settings, CancellationToken.None, _ => throw new IOException("fake save failure")); }
            catch (IOException) { failed = true; }
            Check(failed && controller.AutomaticConfiguration!.Tuning.MinimumLevel == 28,
                "failed persistence leaves live configuration intact");
            await controller.SetModeAsync(AdaptiveFanProductionMode.Manual, CancellationToken.None);
            failed = false;
            try { await controller.ConfigureAutomaticAsync(settings, CancellationToken.None, _ => persisted++); }
            catch (InvalidOperationException) { failed = true; }
            Check(failed && persisted == 1 && backend.Levels.Count == 0,
                "Manual mode blocks settings before persistence even without a fan write");
        }
        backend = new Backend();
        await using(var coordinator=new FanControlCoordinator(backend))
        {
            var profiles=new VictusFanControl.Product.ProductProfiles();
            var controller=new AdaptiveFanProductionController(coordinator,Hp8C40AdaptiveCandidateV1.Create(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,
                automaticConfiguration:profiles.Ac.Fan,automaticMinimumLevel:10);
            for(var session=0;session<4;session++)
            {
                var ac=session%2==0;
                await controller.ConfigureAutomaticAsync(ac?profiles.Ac.Fan:profiles.Battery.Fan,default);
                await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,default);
                clock+=1000;
                var sample=Sample(Now(),40) with{GpuTemperatureC=35,GpuPowerW=5};
                var actual=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
                var target=ac?12:10;
                Check(actual.EqualFanLevel==target&&actual.UnifiedDemand is not null&&actual.Action==AdaptiveFanProductionActionKind.EnterCustomAndApply,
                    "explicit quiet 10..50 review applies the source curve after each clean Firmware release");
                clock+=1000;sample=sample with{Timestamp=Now()};
                await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
                Check(backend.Levels.Count==session+1,"quiet review holds without retransmission");
                await controller.SetModeAsync(AdaptiveFanProductionMode.Firmware,default);
                Check(coordinator.Authority==FanAuthority.Firmware&&backend.Restores==session+1,"quiet review releases before reconfiguration");
            }
        }
        backend=new Backend();clock=0;
        await using(var coordinator=new FanControlCoordinator(backend))
        {
            var original=new VictusFanControl.Product.ProductProfiles().Ac.Fan;
            var controller=new AdaptiveFanProductionController(coordinator,original.BuildPolicy(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,automaticConfiguration:original,automaticMinimumLevel:10);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,default);
            clock+=1000;var sample=Sample(Now(),40) with{GpuTemperatureC=35,GpuPowerW=5};
            await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            var levels=backend.Levels.Count;
            var replacement=original.UnifiedDemand! with{Curve=[new(0,40),new(100,50)]};
            await controller.ApplyUnifiedDemandAsync(replacement,()=>{},default);
            Check(controller.Mode==AdaptiveFanProductionMode.Automatic&&coordinator.Authority==FanAuthority.Custom&&
                backend.Levels.Count==levels&&backend.Restores==0&&controller.LastAutomaticResult is null,
                "live Apply swaps only configuration without writes, restore, session restart or stale decision marker");
            clock+=1000;sample=sample with{Timestamp=Now()};
            var next=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(next.EqualFanLevel==12&&next.SmoothedDemandLevel is >12 and <40&&next.RawDemandLevel is >=40 and <41,
                "live Apply preserves EMA and current actuation rather than jumping to the new raw target");
            var tuned=original.Tuning with{RiseTimeConstantSeconds=5,IncreaseConfirmationSeconds=2};
            var beforeTuning=backend.Levels.Count;
            await controller.ApplyTuningAsync(tuned,()=>{},default);
            Check(controller.AutomaticConfiguration!.Tuning==tuned&&controller.LastAutomaticResult is null&&backend.Levels.Count==beforeTuning&&backend.Restores==0,
                "live tuning changes response without writes, release or stale markers");
            clock+=1000;sample=sample with{Timestamp=Now()};
            var afterTuning=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(afterTuning.SmoothedDemandLevel>next.SmoothedDemandLevel&&afterTuning.SmoothedDemandLevel<40&&afterTuning.EqualFanLevel==12,
                "live tuning preserves EMA and restarts confirmation rather than jumping to raw demand");
            var beforeRejectedTuning=FanConfigurationStore.Serialize(controller.AutomaticConfiguration!);
            var refused=false;try{await controller.ApplyTuningAsync(tuned with{RiseTimeConstantSeconds=0},()=>{},default);}catch(InvalidDataException){refused=true;}
            Check(refused&&FanConfigurationStore.Serialize(controller.AutomaticConfiguration!)==beforeRejectedTuning,"invalid tuning leaves the active engine intact");
            refused=false;try{await controller.ApplyTuningAsync(tuned,()=>throw new InvalidOperationException("cancelled tuning"),default);}catch(InvalidOperationException){refused=true;}
            Check(refused&&backend.Levels.Count==beforeTuning,"cancelled tuning admission does not write hardware");
            var intact=FanConfigurationStore.Serialize(controller.AutomaticConfiguration!);
            var rejected=false;try{await controller.ApplyUnifiedDemandAsync(original.UnifiedDemand!,()=>throw new InvalidOperationException("cancelled click"),default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&FanConfigurationStore.Serialize(controller.AutomaticConfiguration!)==intact&&backend.Levels.Count==levels,
                "cancelled admission leaves the applied curve and hardware untouched");
            clock+=1000;sample=Sample(Now(),85) with{GpuTemperatureC=35,GpuPowerW=5};
            var thermal=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(thermal.EqualFanLevel==16&&thermal.ThermalOverride,"live Apply preserves protected immediate four-level thermal rise");
            clock+=1000;sample=Sample(Now(),96) with{GpuTemperatureC=35,GpuPowerW=5};
            await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            clock+=1000;await controller.ApplyUnifiedDemandAsync(original.UnifiedDemand!,()=>{},default);
            await controller.ApplyTuningAsync(tuned with{RiseTimeConstantSeconds=6},()=>{},default);
            clock+=1001;rejected=false;
            try{await controller.ApplyUnifiedDemandAsync(replacement,()=>{},default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&controller.AutomaticConfiguration!.UnifiedDemand!.Curve.SequenceEqual(original.UnifiedDemand!.Curve),
                "live Apply does not renew an outstanding thermal confirmation deadline");
            await controller.SetModeAsync(AdaptiveFanProductionMode.Firmware,default);
            rejected=false;try{await controller.ApplyUnifiedDemandAsync(original.UnifiedDemand!,()=>{},default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&backend.Restores==1,"live Apply cannot acquire authority from Firmware");
        }
        backend=new Backend();clock=0;
        await using(var coordinator=new FanControlCoordinator(backend))
        {
            var profiles=new VictusFanControl.Product.ProductProfiles();
            var controller=new AdaptiveFanProductionController(coordinator,profiles.Ac.Fan.BuildPolicy(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,automaticConfiguration:profiles.Ac.Fan,automaticMinimumLevel:10);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,default);
            clock=1000;var sample=Sample(Now(),40) with{GpuTemperatureC=35,GpuPowerW=5,CpuLoadPercent=60};
            await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            clock+=1000;sample=sample with{Timestamp=Now()};
            controller.EvaluateAutomaticSafety(sample,Raw(sample),observe:true);
            var cancelled=false;
            try{await controller.ObserveAutomaticSourceWaitAsync(sample,()=>throw new OperationCanceledException("Firmware click"),default);}
            catch(OperationCanceledException){cancelled=true;}
            Check(cancelled&&backend.Levels.SequenceEqual(new[]{12})&&backend.Restores==0,
                "cancelled source wait mutated policy or dispatched hardware");
            for(var i=0;i<3;i++)
            {
                clock+=1000;sample=sample with{Timestamp=Now()};
                var observed=controller.EvaluateAutomaticSafety(sample,Raw(sample),observe:true);
                Check(observed.CustomControlPermitted,"source wait lost safety admission");
                await controller.ObserveAutomaticSourceWaitAsync(sample,()=>{},default);
            }
            Check(backend.Levels.SequenceEqual(new[]{12})&&backend.Restores==0,"source wait dispatched writes or restored Firmware");
            await controller.ApplyUnifiedDemandAsync(profiles.Battery.Fan.UnifiedDemand!,()=>{},default,profiles.Battery.Fan);
            clock+=1000;sample=sample with{Timestamp=Now()};
            var resumed=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(resumed.ObservedLoadSeconds>=3&&resumed.SustainedLoadCooling==false,
                "production diagnostic lost load history across source wait and curve handoff");
            Check(resumed.EqualFanLevel==12&&resumed.SmoothedDemandLevel is >10 and <12&&
                controller.AutomaticConfiguration!.Profile.Id==profiles.Battery.Fan.Profile.Id&&backend.Levels.Count==1,
                "Battery handoff lost continuity, metadata or actuation inertia");
            var rejected=false;
            try{await controller.ApplyUnifiedDemandAsync(profiles.Ac.Fan.UnifiedDemand!,()=>{},default,
                profiles.Ac.Fan with{Tuning=profiles.Ac.Fan.Tuning with{RiseTimeConstantSeconds=9}});}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&controller.AutomaticConfiguration!.Profile.Id==profiles.Battery.Fan.Profile.Id,
                "different inertia tuning silently replaced the running engine");
            await controller.ApplyUnifiedDemandAsync(profiles.Ac.Fan.UnifiedDemand!,()=>{},default,profiles.Ac.Fan);
            Check(controller.Mode==AdaptiveFanProductionMode.Automatic&&backend.Restores==0&&backend.Levels.Count==1,
                "AC return reacquired authority or wrote during configuration");
            var adjusted=profiles.Ac.Fan.Tuning with{RiseTimeConstantSeconds=7};
            await controller.ApplyTuningAsync(adjusted,()=>{},default);
            clock+=1000;sample=sample with{Timestamp=Now()};
            var preserved=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(preserved.ObservedLoadSeconds>resumed.ObservedLoadSeconds,"response tuning preserves accumulated loaded intervals");
            await controller.ApplyTuningAsync(adjusted with{LoadThresholdPercent=99},()=>{},default);
            clock+=1000;sample=sample with{Timestamp=Now()};
            var resetLoad=await controller.ProcessAutomaticAsync(sample,Raw(sample),default);
            Check(resetLoad.ObservedLoadSeconds==0&&resetLoad.SustainedLoadCooling==false,"new load definition starts a new workload history");
            await controller.SetModeAsync(AdaptiveFanProductionMode.Firmware,default);
            rejected=false;try{await controller.ObserveAutomaticSourceWaitAsync(sample,()=>{},default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected,"source wait continued after Firmware cancellation");
        }
        // The opt-in research hook cannot bypass the existing native dispatch boundary.
        foreach(bool weaken in new[]{false,true})
        {
            clock=0;var research=new ResearchFixture{Weaken=weaken};var researchBackend=new Backend();
            if(!weaken)researchBackend.BeforeNative=()=>{research.Refuse=true;return Task.CompletedTask;};
            await using var coordinator=new FanControlCoordinator(researchBackend);
            var controller=new AdaptiveFanProductionController(coordinator,Hp8C40AdaptiveCandidateV1.Create(),true,true,
                automaticHardware:Hardware,automaticMilliseconds:()=>clock,utcNow:Now,automaticConfiguration:new FanConfiguration(),
                automaticMinimumLevel:10,useRawCpuThermalResponse:true,experimentalPolicy:research);
            await controller.SetModeAsync(AdaptiveFanProductionMode.Automatic,default);
            var sample=Sample(Now());bool rejected=false;
            try{await controller.ProcessAutomaticAsync(sample,Raw(sample),default);}catch(InvalidOperationException){rejected=true;}
            Check(rejected&&researchBackend.Levels.Count==0&&coordinator.Authority!=FanAuthority.Custom,
                weaken?"research cannot lower protected raw demand":"research source loss between evaluation and native Set issues no write and restores firmware");
        }
        return failures;
    }

    private sealed class ResearchFixture:IExperimentalFanPolicy
    {
        internal bool Refuse,Weaken;
        public AdaptiveFanInertiaDecision Evaluate(AdaptiveFanPolicyInput input,AdaptiveFanInertiaDecision baseline)=>
            Weaken?baseline with{RawDemandLevel=baseline.RawDemandLevel-1}:baseline;
        public void EnsureDispatchAllowed(DateTimeOffset timestamp,DateTimeOffset now)
        {if(Refuse)throw new InvalidOperationException("synthetic source lost before native dispatch");}
        public void ObserveDuringActuation(AdaptiveFanPolicyInput input) { }
        public void Reset() { }
    }

    private static SafetyGateResult Raw(TelemetrySnapshot snapshot) =>
        SafetyGate.Evaluate(Hardware, SystemState.Healthy, snapshot, snapshot.Timestamp, true);
    private static SafetyGateResult Display(TelemetrySnapshot snapshot, DateTimeOffset now) =>
        SafetyGate.EvaluateForDisplay(Hardware, SystemState.Healthy, snapshot, now, true);
    private static TelemetrySnapshot Sample(DateTimeOffset timestamp, double cpu = 45) =>
        new(timestamp, "Intel Core i7-13700H", cpu, 5, 17,
            Hp8C40TargetProfile.ExpectedGpuName, 47, 14, 0, 3000, 3000)
        {
            CpuExpectedPhysicalCoreCount = Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount,
            CpuCoreTemperatures = Enumerable.Range(0, Hp8C40TargetProfile.Instance.ExpectedPhysicalCoreCount)
                .Select(i => new CpuCoreTemperatureSample(i, i, i < 6 ? "Performance" : "Efficiency", cpu)).ToArray()
        };

    private sealed class Backend : IFanControlBackend
    {
        public string Name => "synthetic-prepared-automatic";
        public bool CanWrite => true;
        public FanBackendCapabilities Capabilities => new(Hp8C40TargetProfile.BoardProduct, 10, 50, false);
        public List<int> Levels { get; } = [];
        public int Restores { get; private set; }
        public Action? AfterEnter { get; set; }
        public Func<Task>? BeforeNative { get; set; }
        private bool _active;
        public ValueTask ProbeControlDependencyAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<FanBackendStatus> GetStatusAsync(CancellationToken ct) =>
            ValueTask.FromResult(new FanBackendStatus(Name, true, _active, true, true, "synthetic"));
        public ValueTask EnterCustomModeAsync(CancellationToken ct)
        {
            _active = true;
            AfterEnter?.Invoke();
            return ValueTask.CompletedTask;
        }
        public async ValueTask ApplyAsync(FanCommand command, CancellationToken ct)
        {
            if (BeforeNative is not null) await BeforeNative();
            await Task.Run(() => FanDispatchAdmissionScope.EnsureNativeRequestAllowed(
                Hp8C40BiosFanControl.BuildSetFanLevelRequest((byte)command.CpuLevel, (byte)command.GpuLevel)));
            Levels.Add(command.CpuLevel);
        }
        public ValueTask RestoreFirmwareAutoAsync(CancellationToken ct)
        {
            FanDispatchAdmissionScope.EnsureNativeRequestAllowed(Hp8C40BiosFanControl.BuildReleaseFanLevelRequest());
            FanDispatchAdmissionScope.EnsureNativeRequestAllowed(Hp8C40BiosFanControl.BuildLegacyDefaultRequest());
            Restores++;
            _active = false;
            return ValueTask.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
