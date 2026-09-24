namespace VictusFanControl.Hardware.Hp;

public static class Hp88F8BiosContractSelfTest
{
    public static int Run(TextWriter output)
    {
        var restore = Hp88F8BiosFanControl.BuildLegacyDefaultRequest();
        var level = Hp88F8BiosFanControl.BuildSetFanLevelRequest(30, 30);

        var restorePass =
            restore.Command == 0x00020008 &&
            restore.CommandType == 0x1A &&
            restore.OutputSize == 0 &&
            restore.Payload.SequenceEqual(new byte[] { 0xFF, 0x00, 0x00, 0x00 });

        var levelPass =
            level.Command == 0x00020008 &&
            level.CommandType == 0x2E &&
            level.OutputSize == 0 &&
            level.Payload.SequenceEqual(new byte[] { 30, 30, 0x00, 0x00 });

        output.WriteLine(
            $"{(restorePass ? "PASS" : "FAIL")}  HP 88F8 LegacyDefault WMI envelope");
        output.WriteLine(
            $"{(levelPass ? "PASS" : "FAIL")}  HP 88F8 SetFanLevel(30,30) WMI envelope");

        return restorePass && levelPass ? 0 : 8;
    }
}
