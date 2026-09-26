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
    private const int WaitPollLimit = 30;
    private const int WaitSpinPolls = 5;
    private const int WaitYieldPolls = 10;
    private const int MaximumStableWordDeltaRpm = 128;
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

    /// <summary>
    /// Reads only the HP 88F8 fixed-level ownership registers while holding one
    /// Global\Access_EC mutex lease. Watchdog lease admission/recovery uses this
    /// narrow snapshot because it needs ownership only, not rate/tach/manual
    /// diagnostics. Keeping the service-side critical section to 0x34/0x35
    /// avoids unnecessary contention with the GUI telemetry/control readers.
    /// </summary>
    public Hp88F8SetpointSample ReadHp88F8Setpoint()
    {
        var lockTaken = AcquireMutex();
        try
        {
            return RetryLocked(
                () => new Hp88F8SetpointSample(
                    CpuSetpoint: ReadRegisterLocked(0x34),
                    GpuSetpoint: ReadRegisterLocked(0x35)),
                "EC 88F8 setpoint snapshot 0x34/0x35");
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
                    CpuRateTarget: ReadRegisterLocked(0x2C),
                    GpuRateTarget: ReadRegisterLocked(0x2D),
                    CpuRate: ReadRegisterLocked(0x2E),
                    GpuRate: ReadRegisterLocked(0x2F),
                    CpuSetpoint: ReadRegisterLocked(0x34),
                    GpuSetpoint: ReadRegisterLocked(0x35),
                    Manual: ReadRegisterLocked(0x62),
                    Countdown: ReadRegisterLocked(0x63),
                    Mode: ReadRegisterLocked(0x95),
                    MaxFan: ReadRegisterLocked(0xEC),
                    FanSwitch: ReadRegisterLocked(0xF4),
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
        // A 16-bit EC value is exposed as two independent byte transactions.
        // Read it twice and reject a large disagreement so an update between
        // low/high byte reads cannot silently become a plausible but torn RPM.
        var first = ReadWordLittleEndianRawLocked(lowRegister);
        var second = ReadWordLittleEndianRawLocked(lowRegister);

        if (Math.Abs((int)second - first) > MaximumStableWordDeltaRpm)
        {
            throw new InvalidDataException(
                $"EC word 0x{lowRegister:X2} was unstable: {first} -> {second}.");
        }

        return second;
    }

    private ushort ReadWordLittleEndianRawLocked(byte lowRegister)
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
        byte lastStatus = 0;

        for (var poll = 0; poll < WaitPollLimit; poll++)
        {
            lastStatus = ReadPort(CommandStatusPort);
            if (predicate(lastStatus))
            {
                return;
            }

            // Keep the common fast path fast, but do not hammer the ACPI EC
            // continuously when Windows/BIOS is already using it.
            if (poll < WaitSpinPolls)
            {
                Thread.SpinWait(32);
            }
            else if (poll < WaitSpinPolls + WaitYieldPolls)
            {
                Thread.Sleep(0);
            }
            else
            {
                Thread.Sleep(1);
            }
        }

        throw new TimeoutException(
            $"{timeoutMessage} Last status=0x{lastStatus:X2} after {WaitPollLimit} polls.");
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

    internal readonly record struct Hp88F8SetpointSample(
        byte CpuSetpoint,
        byte GpuSetpoint);

    internal readonly record struct Hp88F8ControlStateSample(
        byte CpuRateTarget,
        byte GpuRateTarget,
        byte CpuRate,
        byte GpuRate,
        byte CpuSetpoint,
        byte GpuSetpoint,
        byte Manual,
        byte Countdown,
        byte Mode,
        byte MaxFan,
        byte FanSwitch,
        ushort CpuRpm,
        ushort GpuRpm);
}
