namespace VictusFanControl.Hardware.Hp;

public static class Hp8C40BiosContractSelfTest
{
    public static int Run(TextWriter output)
    {
        var minimumLevel = (byte)Hp8C40TargetProfile.MinimumValidatedFanLevel;
        var maximumLevel = (byte)Hp8C40TargetProfile.MaximumValidatedFanLevel;

        var restore = Hp8C40BiosFanControl.BuildLegacyDefaultRequest();
        var getLevel = Hp8C40BiosFanControl.BuildGetFanLevelRequest();
        var setMinimum = Hp8C40BiosFanControl.BuildSetFanLevelRequest(minimumLevel, minimumLevel);
        var setInterior = Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 30);
        var setMaximum = Hp8C40BiosFanControl.BuildSetFanLevelRequest(maximumLevel, maximumLevel);
        var releaseLevel = Hp8C40BiosFanControl.BuildReleaseFanLevelRequest();

        var productionBoundsPass =
            minimumLevel == 10 &&
            maximumLevel == 50 &&
            Hp8C40BiosFanControl.MinimumValidatedLevel == 10 &&
            Hp8C40BiosFanControl.MaximumValidatedLevel == 50;

        var restorePass =
            restore.Command == 0x00020008 &&
            restore.CommandType == 0x1A &&
            restore.OutputSize == 0 &&
            restore.Payload.SequenceEqual(new byte[] { 0xFF, 0x00, 0x00, 0x00 });

        var getLevelPass =
            getLevel.Command == 0x00020008 &&
            getLevel.CommandType == 0x2D &&
            getLevel.OutputSize == 128 &&
            getLevel.Payload.SequenceEqual(new byte[] { 0x00, 0x00, 0x00, 0x00 });

        var setMinimumPass =
            setMinimum.Command == 0x00020008 &&
            setMinimum.CommandType == 0x2E &&
            setMinimum.OutputSize == 0 &&
            setMinimum.Payload.SequenceEqual(new byte[] { 10, 10, 0x00, 0x00 });

        var setInteriorPass =
            setInterior.Command == 0x00020008 &&
            setInterior.CommandType == 0x2E &&
            setInterior.OutputSize == 0 &&
            setInterior.Payload.SequenceEqual(new byte[] { 30, 30, 0x00, 0x00 });

        var setMaximumPass =
            setMaximum.Command == 0x00020008 &&
            setMaximum.CommandType == 0x2E &&
            setMaximum.OutputSize == 0 &&
            setMaximum.Payload.SequenceEqual(new byte[] { 50, 50, 0x00, 0x00 });

        var asymmetricRejected = false;
        try { _ = Hp8C40BiosFanControl.BuildSetFanLevelRequest(10, 11); }
        catch (ArgumentException) { asymmetricRejected = true; }

        var belowRangeRejected = false;
        try { _ = Hp8C40BiosFanControl.BuildSetFanLevelRequest(9, 9); }
        catch (ArgumentOutOfRangeException) { belowRangeRejected = true; }

        var aboveRangeRejected = false;
        try { _ = Hp8C40BiosFanControl.BuildSetFanLevelRequest(51, 51); }
        catch (ArgumentOutOfRangeException) { aboveRangeRejected = true; }

        output.WriteLine($"{(productionBoundsPass ? "PASS" : "FAIL")}  HP 8C40 production BIOS range pinned to equal-only 10..50");
        output.WriteLine($"{(restorePass ? "PASS" : "FAIL")}  HP 8C40 LegacyDefault WMI envelope");
        output.WriteLine($"{(getLevelPass ? "PASS" : "FAIL")}  HP 8C40 GetFanLevel WMI envelope");
        output.WriteLine($"{(setMinimumPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(10,10) production envelope");
        output.WriteLine($"{(setInteriorPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(30,30) production envelope");
        output.WriteLine($"{(setMaximumPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(50,50) production envelope");
        output.WriteLine($"{(asymmetricRejected ? "PASS" : "FAIL")}  HP 8C40 asymmetric production command rejected");
        output.WriteLine($"{(belowRangeRejected ? "PASS" : "FAIL")}  HP 8C40 level below 10 rejected");
        output.WriteLine($"{(aboveRangeRejected ? "PASS" : "FAIL")}  HP 8C40 level above 50 rejected");

        var releaseLevelPass =
            releaseLevel.Command == 0x00020008 &&
            releaseLevel.CommandType == 0x2E &&
            releaseLevel.OutputSize == 0 &&
            releaseLevel.Payload.SequenceEqual(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });

        output.WriteLine($"{(releaseLevelPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(FF,FF) release envelope");

        var restoreOrder = new List<string>();
        var firstFailurePropagated = false;
        try
        {
            Hp8C40BiosFanControl.ExecuteRestoreSequence(
                releaseAction: () =>
                {
                    restoreOrder.Add("release");
                    throw new IOException("synthetic release failure");
                },
                legacyDefaultAction: () => restoreOrder.Add("legacy-default"));
        }
        catch (HpBiosCallException ex)
        {
            firstFailurePropagated =
                ex.InnerException is IOException &&
                ex.Message.Contains("LegacyDefault was still attempted", StringComparison.Ordinal);
        }

        var restoreSequencePass =
            restoreOrder.SequenceEqual(new[] { "release", "legacy-default" }) &&
            firstFailurePropagated;
        output.WriteLine($"{(restoreSequencePass ? "PASS" : "FAIL")}  restore reports FF,FF failure only after LegacyDefault attempt");

        return productionBoundsPass &&
               restorePass &&
               getLevelPass &&
               setMinimumPass &&
               setInteriorPass &&
               setMaximumPass &&
               asymmetricRejected &&
               belowRangeRejected &&
               aboveRangeRejected &&
               releaseLevelPass &&
               restoreSequencePass
            ? 0
            : 8;
    }
}
