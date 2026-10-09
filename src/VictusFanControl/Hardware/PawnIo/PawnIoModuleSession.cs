namespace VictusFanControl.Hardware.PawnIo;

internal sealed class PawnIoModuleSession : IDisposable
{
    private readonly PawnIoDevice _device;

    public PawnIoModuleSession(string modulePath)
    {
        if (!File.Exists(modulePath))
        {
            throw new FileNotFoundException("Required signed PawnIO module was not found.", modulePath);
        }

        _device = PawnIoDevice.Open();
        DriverVersion = _device.GetDriverVersion();

        var module = File.ReadAllBytes(modulePath);
        _device.LoadModule(module);
    }

    public Version DriverVersion { get; }

    public ulong[] Execute(string functionName, ReadOnlySpan<ulong> input, int outputCount) =>
        _device.Execute(functionName, input, outputCount);

    public void Dispose() => _device.Dispose();
}
