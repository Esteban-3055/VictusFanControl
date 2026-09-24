namespace VictusFanControl.Hardware.Hp;

public static class Hp88F8BiosContractSelfTest
{
    public static int Run(TextWriter output)
    {
        var request = Hp88F8BiosFanControl.BuildLegacyDefaultRequest();

        var pass =
            request.Command == 0x00020008 &&
            request.CommandType == 0x1A &&
            request.OutputSize == 0 &&
            request.Payload.SequenceEqual(new byte[] { 0xFF, 0x00, 0x00, 0x00 });

        output.WriteLine(
            $"{(pass ? "PASS" : "FAIL")}  HP 88F8 LegacyDefault WMI envelope");

        if (!pass)
        {
            output.WriteLine(
                $"  command=0x{request.Command:X8} type=0x{request.CommandType:X2} " +
                $"out={request.OutputSize} payload={Convert.ToHexString(request.Payload)}");
        }

        return pass ? 0 : 8;
    }
}
