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

    private const int ReadAttempts = 3;
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
            Exception? lastError = null;
            for (var attempt = 1; attempt <= ReadAttempts; attempt++)
            {
                try
                {
                    return ReadRegisterLocked(register);
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                    if (attempt < ReadAttempts)
                    {
                        Thread.Sleep(1);
                    }
                }
            }

            throw new TimeoutException(
                $"EC register 0x{register:X2} failed after {ReadAttempts} attempts: {lastError?.Message}",
                lastError);
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

        var highRegister = (byte)(lowRegister + 1);
        var lockTaken = AcquireMutex();
        try
        {
            Exception? lastError = null;
            for (var attempt = 1; attempt <= ReadAttempts; attempt++)
            {
                try
                {
                    // Retry the complete word so the low/high bytes belong to the
                    // same successful transaction pair.
                    var low = ReadRegisterLocked(lowRegister);
                    var high = ReadRegisterLocked(highRegister);
                    return (ushort)(low | (high << 8));
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                    if (attempt < ReadAttempts)
                    {
                        Thread.Sleep(1);
                    }
                }
            }

            throw new TimeoutException(
                $"EC word 0x{lowRegister:X2}/0x{highRegister:X2} failed after {ReadAttempts} attempts: {lastError?.Message}",
                lastError);
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

    private byte ReadRegisterLocked(byte register)
    {
        // Match the standard ACPI RD_EC handshake used by the validated HP tools:
        // idle -> READ command -> IBF clear -> address -> IBF clear -> OBF set -> data.
        WaitForIdle();
        WritePort(CommandStatusPort, CommandReadEc);

        WaitForInputBufferEmpty();
        WritePort(DataPort, register);

        // Important: the EC must consume the address before we wait for result data.
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
}
