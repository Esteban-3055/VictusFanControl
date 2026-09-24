using System.Diagnostics;

namespace VictusFanControl.Hardware.PawnIo;

/// <summary>
/// Read-only EC register client for the standard ACPI EC command/data ports.
/// A register read still requires protocol writes of the READ command and
/// register address to ports 0x66/0x62; it never writes an EC register value.
/// </summary>
internal sealed class AcpiEcReader : IDisposable
{
    private const byte DataPort = 0x62;
    private const byte CommandStatusPort = 0x66;

    private const byte StatusOutputBufferFull = 0x01;
    private const byte StatusInputBufferFull = 0x02;
    private const byte CommandReadEc = 0x80;

    private const int ReadAttempts = 5;
    private const int RetryDelayMs = 2;
    private static readonly TimeSpan EcIoTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MutexTimeout = TimeSpan.FromMilliseconds(500);

    private readonly PawnIoModuleSession _session;
    private readonly Mutex _ecMutex = new(false, @"Global\Access_EC");

    public AcpiEcReader(string modulePath)
    {
        _session = new PawnIoModuleSession(modulePath);
    }

    public Version PawnIoVersion => _session.DriverVersion;

    public byte ReadRegister(byte register)
    {
        var lockTaken = AcquireMutex();
        try
        {
            return RetryLocked(
                () => ReadRegisterLocked(register),
                $"EC register 0x{register:X2}");
        }
        finally
        {
            if (lockTaken)
            {
                _ecMutex.ReleaseMutex();
            }
        }
    }

    public ushort ReadWordLittleEndian(byte lowRegister)
    {
        if (lowRegister == byte.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(lowRegister));
        }

        var lockTaken = AcquireMutex();
        try
        {
            return RetryLocked(
                () => ReadWordLittleEndianLocked(lowRegister),
                $"EC word 0x{lowRegister:X2}/0x{(byte)(lowRegister + 1):X2}");
        }
        finally
        {
            if (lockTaken)
            {
                _ecMutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Reads both 88F8 tachometers while holding one EC mutex lease.
    /// If any of the four register transactions fails, the complete pair is
    /// retried so CPU/GPU RPM belong to one coherent successful snapshot.
    /// </summary>
    public FanTachometerSample ReadFanTachometers()
    {
        var lockTaken = AcquireMutex();
        try
        {
            return RetryLocked(
                () => new FanTachometerSample(
                    ReadWordLittleEndianLocked(0xB0),
                    ReadWordLittleEndianLocked(0xB2)),
                "EC fan tachometer snapshot 0xB0-0xB3");
        }
        finally
        {
            if (lockTaken)
            {
                _ecMutex.ReleaseMutex();
            }
        }
    }

    public Hp88F8ControlStateSample ReadHp88F8ControlState()
    {
        var lockTaken = AcquireMutex();
        try
        {
            return RetryLocked(
                () => new Hp88F8ControlStateSample(
                    CpuSetpoint: ReadRegisterLocked(0x34),
                    GpuSetpoint: ReadRegisterLocked(0x35),
                    CpuRate: ReadRegisterLocked(0x2E),
                    GpuRate: ReadRegisterLocked(0x2F),
                    Countdown: ReadRegisterLocked(0x63),
                    CpuRpm: ReadWordLittleEndianLocked(0xB0),
                    GpuRpm: ReadWordLittleEndianLocked(0xB2)),
                "EC 88F8 control-state snapshot");
        }
        finally
        {
            if (lockTaken)
            {
                _ecMutex.ReleaseMutex();
            }
        }
    }

    public void Dispose()
    {
        _session.Dispose();
        _ecMutex.Dispose();
    }

    private T RetryLocked<T>(Func<T> action, string operation)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                return action();
            }
            catch (TimeoutException ex)
            {
                lastError = ex;
            }
            catch (InvalidDataException ex)
            {
                lastError = ex;
            }

            if (attempt < ReadAttempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }

        throw new IOException(
            $"{operation} failed after {ReadAttempts} attempts: {lastError?.Message}",
            lastError);
    }

    private bool AcquireMutex()
    {
        try
        {
            if (!_ecMutex.WaitOne(MutexTimeout))
            {
                throw new TimeoutException("Timed out waiting for Global\\Access_EC.");
            }

            return true;
        }
        catch (AbandonedMutexException)
        {
            // WaitOne grants ownership when reporting an abandoned mutex.
            return true;
        }
    }

    private ushort ReadWordLittleEndianLocked(byte lowRegister)
    {
        var low = ReadRegisterLocked(lowRegister);
        var high = ReadRegisterLocked((byte)(lowRegister + 1));
        return (ushort)(low | (high << 8));
    }

    private byte ReadRegisterLocked(byte register)
    {
        // Standard ACPI RD_EC handshake:
        // idle -> READ command -> IBF clear -> address -> IBF clear -> OBF set -> data.
        WaitForIdle();
        WritePort(CommandStatusPort, CommandReadEc);

        WaitForInputBufferEmpty();
        WritePort(DataPort, register);

        WaitForInputBufferEmpty();
        WaitForOutputBufferFull();

        return ReadPort(DataPort);
    }

    private void WaitForIdle()
    {
        WaitUntil(
            status => (status & (StatusOutputBufferFull | StatusInputBufferFull)) == 0,
            "EC did not become idle before the read transaction.");
    }

    private void WaitForInputBufferEmpty()
    {
        WaitUntil(
            status => (status & StatusInputBufferFull) == 0,
            "EC input buffer did not become empty.");
    }

    private void WaitForOutputBufferFull()
    {
        WaitUntil(
            status => (status & StatusOutputBufferFull) != 0,
            "EC output buffer did not become full.");
    }

    private void WaitUntil(Func<byte, bool> predicate, string timeoutMessage)
    {
        var start = Stopwatch.GetTimestamp();
        byte lastStatus = 0;

        while (Stopwatch.GetElapsedTime(start) < EcIoTimeout)
        {
            lastStatus = ReadPort(CommandStatusPort);
            if (predicate(lastStatus))
            {
                return;
            }

            Thread.SpinWait(32);
        }

        throw new TimeoutException($"{timeoutMessage} Last status=0x{lastStatus:X2}.");
    }

    private byte ReadPort(byte port)
    {
        var values = _session.Execute("ioctl_pio_read", new ulong[] { port }, 1);
        if (values.Length != 1)
        {
            throw new InvalidDataException("LpcACPIEC returned an unexpected result length.");
        }

        if (values[0] > byte.MaxValue)
        {
            throw new InvalidDataException($"LpcACPIEC returned an invalid port value: 0x{values[0]:X}.");
        }

        return (byte)values[0];
    }

    private void WritePort(byte port, byte value)
    {
        _session.Execute("ioctl_pio_write", new ulong[] { port, value }, 0);
    }

    internal readonly record struct FanTachometerSample(ushort CpuRpm, ushort GpuRpm);

    internal readonly record struct Hp88F8ControlStateSample(
        byte CpuSetpoint,
        byte GpuSetpoint,
        byte CpuRate,
        byte GpuRate,
        byte Countdown,
        ushort CpuRpm,
        ushort GpuRpm);
}
