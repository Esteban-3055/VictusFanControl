using VictusFanControl.Hardware.Intel;
using VictusFanControl.Performance;

namespace VictusFanControl.CpuProbe;

internal static class RaplProbeSelfTest
{
    private const ulong Baseline = 360UL | (1UL << 15) | (0x6AUL << 17) |
        (480UL << 32) | (1UL << 47) | (1UL << 48) | (0x4AUL << 49) | (1UL << 60);

    internal static async Task<int> RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "Vfc-Rapl-Fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Require(IntelRaplCodecSelfTest.Run(Console.Out) == 0, "existing RAPL decoder");
            var units = IntelRaplCodec.DecodeUnits(FakeHardware.Units);
            var applied = RaplWritePolicy.BuildReducedLimit(Baseline, units);
            Require((applied & ~RaplWritePolicy.OwnedMask) == (Baseline & ~RaplWritePolicy.OwnedMask), "all non-owned bits preserved");
            var decoded = IntelRaplCodec.DecodePackagePowerLimit(applied, units);
            Require(decoded.Pl1.PowerWatts == 36 && decoded.Pl2.PowerWatts == 48, "20 percent downward encoding");

            var explicit2040 = RaplWritePolicy.BuildRequestedLimit(Baseline, units, 20, 40);
            Require((explicit2040 & ~RaplWritePolicy.OwnedMask) == (Baseline & ~RaplWritePolicy.OwnedMask),
                "explicit limits preserve all non-owned bits");
            var explicitDecoded = IntelRaplCodec.DecodePackagePowerLimit(explicit2040, units);
            Require(explicitDecoded.Pl1.PowerWatts == 20 && explicitDecoded.Pl2.PowerWatts == 40,
                "explicit 20/40 encoding");

            const ulong observed8C40Baseline = 0x0042839800DF8168UL;
            const ulong expected8C402040 = 0x0042814000DF80A0UL;
            var observedUnits = IntelRaplCodec.DecodeUnits(0x00000000000A0E03UL);
            var observed2040 = RaplWritePolicy.BuildRequestedLimit(observed8C40Baseline, observedUnits, 20, 40);
            Require(observed2040 == expected8C402040,
                "observed HP 8C40 45/115 baseline encodes exact 20/40 raw value");

            var highThenLow = new List<CpuObservation>();
            for (var i = 0; i < 8; i++)
                highThenLow.Add(new(expected8C402040, 39.5 + (i % 2), 68, 99.8, true));
            highThenLow.Add(new(expected8C402040, 12, 58, 8, true));
            highThenLow.Add(new(expected8C402040, 11, 57, 6, true));
            for (var i = 0; i < 6; i++)
                highThenLow.Add(new(expected8C402040, 19.4 + ((i % 3) * 0.2), 58, 99.9, true));
            var enforcement = RaplEnforcementClassifier.Assess(
                Array.Empty<CpuObservation>(),
                highThenLow,
                IntelRaplCodec.DecodePackagePowerLimit(expected8C402040, observedUnits));
            Require(enforcement.Classification ==
                RaplEnforcementClassifier.OrderedHighLoadPhases,
                "ordered internal high-load PL2-to-PL1 enforcement classification");
            Require(enforcement.HighWindowPowerW is >= 39 and <= 41 &&
                enforcement.LowWindowPowerW is >= 19 and <= 21,
                "ordered enforcement window averages");
            RequireThrows(() => RaplWritePolicy.BuildRequestedLimit(Baseline, units, 9, 40),
                "explicit PL1 below safe floor rejected");
            RequireThrows(() => RaplWritePolicy.BuildRequestedLimit(Baseline, units, 30, 20),
                "explicit PL2 below PL1 rejected");
            RequireThrows(() => RaplWritePolicy.BuildRequestedLimit(Baseline, units, 45, 50),
                "explicit PL1 must be downward");
            RequireThrows(() => RaplWritePolicy.BuildRequestedLimit(Baseline, units, 20, 60),
                "explicit PL2 must be downward");

            var changed = (applied & ~RaplWritePolicy.Pl1Mask) | 200UL;
            var plan = RaplWritePolicy.PlanRestore(Baseline, applied, changed);
            Require((plan.Value & RaplWritePolicy.Pl1Mask) == 200 &&
                (plan.Value & RaplWritePolicy.Pl2Mask) == (Baseline & RaplWritePolicy.Pl2Mask), "external PL1 preserved; owned PL2 restored");
            Require(plan.Status == "EXTERNAL_CHANGE_PRESERVED", "external writer reported");
            Require(RaplWritePolicy.PlanRestore(Baseline, applied, applied | (1UL << 63)).Value == (applied | (1UL << 63)), "restore never clears lock");
            Require(RaplWritePolicy.PlanRestore(Baseline, applied, applied ^ (1UL << 16)).Value == (applied ^ (1UL << 16)), "changed clamp blocks restoration");

            async Task Check(string name, FakeHardware fake, bool writes, string expected,
                string restore, int count, Func<bool>? owner = null, CancellationToken token = default,
                RequestedPowerLimits? explicitLimits = null)
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var journal = Path.Combine(directory, "active.json");
                using var evidence = new ProbeEvidence(directory, journal);
                fake.Journal = journal;
                var result = await RaplProbeEngine.RunAsync(fake, evidence, writes,
                    new ProbeTiming(2, 3, 1, 1), owner ?? (() => true), token, explicitLimits);
                Require(result.Result == expected, name + " result: " + result.Result);
                Require(result.RestoreResult == restore, name + " restore: " + result.RestoreResult);
                Require(fake.WriteCount == count, name + " write count");
                Require(File.Exists(journal) == (count > 0 && restore != "BASELINE_VERIFIED"), name + " unresolved marker");
                Require(File.Exists(Path.Combine(directory, "summary.json")), name + " evidence exists");
                Console.WriteLine("PASS " + name);
            }

            await Check("readonly", new(), false, "READ_ONLY_STABLE", "NOT_NEEDED", 0);
            await Check("accepted-restored", new(), true, "WRITE_ACCEPTED_AND_PERSISTED", "BASELINE_VERIFIED", 2);
            var explicitFake = new FakeHardware();
            await Check("explicit-20-40", explicitFake, true, "WRITE_ACCEPTED_AND_PERSISTED",
                "BASELINE_VERIFIED", 2, explicitLimits: new RequestedPowerLimits(20, 40));
            Require(explicitFake.FirstApplied.HasValue, "explicit write captured");
            var explicitApplied = IntelRaplCodec.DecodePackagePowerLimit(explicitFake.FirstApplied!.Value, units);
            Require(explicitApplied.Pl1.PowerWatts == 20 && explicitApplied.Pl2.PowerWatts == 40,
                "engine applies explicit 20/40");
            await Check("locked", new() { Raw = Baseline | (1UL << 63) }, true, "WRITE_REFUSED_OR_READ_FAILED", "NOT_NEEDED", 0);
            await Check("disabled", new() { Raw = Baseline & ~(1UL << 15) }, true, "WRITE_REFUSED_OR_READ_FAILED", "NOT_NEEDED", 0);
            await Check("dynamic-baseline", new() { BeforeSample = h => { if (h.SampleCount == 3) h.Raw ^= 1; } }, true, "WRITE_REFUSED_OR_READ_FAILED", "NOT_NEEDED", 0);
            await Check("hot-baseline", new() { Temperature = 85 }, true, "WRITE_REFUSED_OR_READ_FAILED", "NOT_NEEDED", 0);
            await Check("readback-rejected", new() { IgnoreApply = true }, true, "WRITE_TEST_FAILED", "BASELINE_VERIFIED", 1);
            await Check("write-error-after-mutation", new() { ThrowAfterApply = true }, true, "WRITE_TEST_FAILED", "BASELINE_VERIFIED", 2);
            var lostParent = new FakeHardware();
            await Check("parent-exit", lostParent, true, "ABORTED", "BASELINE_VERIFIED", 2, () => lostParent.WriteCount == 0);
            await Check("ac-loss", new() { BeforeSample = h => { if (h.WriteCount == 1) h.Ac = false; } }, true, "ABORTED", "BASELINE_VERIFIED", 2);
            await Check("thermal-abort", new() { BeforeSample = h => { if (h.WriteCount == 1) h.Temperature = 91; } }, true, "WRITE_TEST_FAILED", "BASELINE_VERIFIED", 2);
            await Check("external-limit", new() { BeforeSample = h => { if (h.WriteCount == 1) h.Raw = (h.Raw & ~RaplWritePolicy.Pl1Mask) | 200UL; } }, true,
                "WRITE_TEST_FAILED", "EXTERNAL_CHANGE_PRESERVED", 2);
            await Check("restore-lock-appeared", new() { BeforeSample = h => { if (h.WriteCount == 1) h.Raw |= 1UL << 63; } }, true,
                "WRITE_TEST_FAILED", "RESTORE_BLOCKED_LOCK", 1);
            await Check("restore-clamp-changed", new() { BeforeSample = h => { if (h.WriteCount == 1) h.Raw ^= 1UL << 16; } }, true,
                "WRITE_TEST_FAILED", "RESTORE_DEFERRED_NONPOWER_FIELDS_CHANGED", 1);
            await Check("restore-io-error", new() { ThrowOnRestore = true }, true,
                "WRITE_ACCEPTED_AND_PERSISTED", "RESTORE_UNCONFIRMED", 2);
            using var cts = new CancellationTokenSource();
            await Check("cancel-after-write", new() { AfterApply = () => cts.Cancel() }, true,
                "ABORTED", "BASELINE_VERIFIED", 2, token: cts.Token);
            await GuardianProcessFixture.RunAsync(root);
            Require(CpuPowerLimiterSelfTest.Run(Console.Out) == 0,
                "production CPU power limiter foundation");
            Console.WriteLine("Intel RAPL P1/P2 fixture suite: PASS (no hardware I/O).");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("RAPL fixture FAIL: " + ex); return 1; }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    private static void RequireThrows(Action action, string label)
    {
        try
        {
            action();
            throw new InvalidOperationException(label);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("WRITE_REFUSED_", StringComparison.Ordinal))
        {
        }
    }

    private sealed class FakeHardware : IRaplProbeHardware
    {
        internal const ulong Units = 3UL | (14UL << 8) | (10UL << 16);
        internal ulong Raw = Baseline;
        internal ulong? FirstApplied;
        internal int WriteCount, SampleCount;
        internal bool Ac = true, IgnoreApply, ThrowAfterApply, ThrowOnRestore;
        internal double Temperature = 50;
        internal string? Journal;
        internal Action<FakeHardware>? BeforeSample;
        internal Action? AfterApply;
        public ulong UnitsRaw => Units;
        public ulong PowerInfoRaw => 360UL | (80UL << 16) | (920UL << 32);
        public ulong ReadLimit() => Raw;
        public void WriteLimit(ulong value)
        {
            Require(File.Exists(Journal), "durable journal exists before EVERY physical write");
            Require((value & ~RaplWritePolicy.OwnedMask) == (Raw & ~RaplWritePolicy.OwnedMask), "write preserves non-owned fields");
            WriteCount++;
            if (WriteCount == 1) FirstApplied = value;
            if (WriteCount == 2 && ThrowOnRestore) throw new IOException("restore ioctl failed");
            if (WriteCount != 1 || !IgnoreApply) Raw = value;
            if (WriteCount == 1)
            {
                AfterApply?.Invoke();
                if (ThrowAfterApply) throw new IOException("apply error after mutation");
            }
        }
        public CpuObservation Sample()
        {
            SampleCount++;
            BeforeSample?.Invoke(this);
            return new(Raw, WriteCount == 1 ? 40 : 60, Temperature, 80, Ac);
        }
        public void Dispose() { }
    }
}
