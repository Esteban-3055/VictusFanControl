namespace VictusFanControl.Hardware.Hp;

public static class Hp8C40BiosContractSelfTest
{
    public static int Run(TextWriter output)
    {
        var restore = Hp8C40BiosFanControl.BuildLegacyDefaultRequest();
        var getLevel = Hp8C40BiosFanControl.BuildGetFanLevelRequest();
        var setLevel = Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 30);
        var setUpperLevel = Hp8C40BiosFanControl.BuildSetFanLevelRequest(32, 32);
        var releaseLevel = Hp8C40BiosFanControl.BuildReleaseFanLevelRequest();

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

        var setLevelPass =
            setLevel.Command == 0x00020008 &&
            setLevel.CommandType == 0x2E &&
            setLevel.OutputSize == 0 &&
            setLevel.Payload.SequenceEqual(new byte[] { 30, 30, 0x00, 0x00 });

        var setUpperLevelPass =
            setUpperLevel.Command == 0x00020008 &&
            setUpperLevel.CommandType == 0x2E &&
            setUpperLevel.OutputSize == 0 &&
            setUpperLevel.Payload.SequenceEqual(new byte[] { 32, 32, 0x00, 0x00 });

        var asymmetricRejected = false;
        try
        {
            _ = Hp8C40BiosFanControl.BuildSetFanLevelRequest(30, 31);
        }
        catch (ArgumentException)
        {
            asymmetricRejected = true;
        }

        var aboveRangeRejected = false;
        try
        {
            _ = Hp8C40BiosFanControl.BuildSetFanLevelRequest(33, 33);
        }
        catch (ArgumentOutOfRangeException)
        {
            aboveRangeRejected = true;
        }

        output.WriteLine(
            $"{(restorePass ? "PASS" : "FAIL")}  HP 8C40 LegacyDefault WMI envelope");
        output.WriteLine(
            $"{(getLevelPass ? "PASS" : "FAIL")}  HP 8C40 GetFanLevel WMI envelope");
        var releaseLevelPass =
            releaseLevel.Command == 0x00020008 &&
            releaseLevel.CommandType == 0x2E &&
            releaseLevel.OutputSize == 0 &&
            releaseLevel.Payload.SequenceEqual(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });

        output.WriteLine(
            $"{(setLevelPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(30,30) WMI envelope");
        output.WriteLine(
            $"{(setUpperLevelPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(32,32) WMI envelope");
        output.WriteLine(
            $"{(asymmetricRejected ? "PASS" : "FAIL")}  HP 8C40 asymmetric production command rejected");
        output.WriteLine(
            $"{(aboveRangeRejected ? "PASS" : "FAIL")}  HP 8C40 level above 32 rejected");
        output.WriteLine(
            $"{(releaseLevelPass ? "PASS" : "FAIL")}  HP 8C40 SetFanLevel(FF,FF) release envelope");

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
        output.WriteLine(
            $"{(restoreSequencePass ? "PASS" : "FAIL")}  restore reports FF,FF failure only after LegacyDefault attempt");

        return restorePass &&
               getLevelPass &&
               setLevelPass &&
               setUpperLevelPass &&
               asymmetricRejected &&
               aboveRangeRejected &&
               releaseLevelPass &&
               restoreSequencePass
            ? 0
            : 8;
    }
}
