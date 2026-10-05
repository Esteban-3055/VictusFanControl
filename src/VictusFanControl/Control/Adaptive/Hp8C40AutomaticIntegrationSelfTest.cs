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
        return failures;
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
