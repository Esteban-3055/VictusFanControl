namespace VictusFanControl.Hardware.Hp;

public static class Hp88F8BiosContractSelfTest
{
    public static int Run(TextWriter output)
    {
        var restore = Hp88F8BiosFanControl.BuildLegacyDefaultRequest();
        var getLevel = Hp88F8BiosFanControl.BuildGetFanLevelRequest();
        var setLevel = Hp88F8BiosFanControl.BuildSetFanLevelRequest(30, 30);
        var releaseLevel = Hp88F8BiosFanControl.BuildReleaseFanLevelRequest();

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

        output.WriteLine(
            $"{(restorePass ? "PASS" : "FAIL")}  HP 88F8 LegacyDefault WMI envelope");
        output.WriteLine(
            $"{(getLevelPass ? "PASS" : "FAIL")}  HP 88F8 GetFanLevel WMI envelope");
        var releaseLevelPass =
            releaseLevel.Command == 0x00020008 &&
            releaseLevel.CommandType == 0x2E &&
            releaseLevel.OutputSize == 0 &&
            releaseLevel.Payload.SequenceEqual(new byte[] { 0xFF, 0xFF, 0x00, 0x00 });

        output.WriteLine(
            $"{(setLevelPass ? "PASS" : "FAIL")}  HP 88F8 SetFanLevel(30,30) WMI envelope");
        output.WriteLine(
            $"{(releaseLevelPass ? "PASS" : "FAIL")}  HP 88F8 SetFanLevel(FF,FF) release envelope");

        var restoreOrder = new List<string>();
        var firstFailurePropagated = false;
        try
        {
            Hp88F8BiosFanControl.ExecuteRestoreSequence(
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
               releaseLevelPass &&
               restoreSequencePass
            ? 0
            : 8;
    }
}
