using VictusFanControl.Hardware.Hp;

namespace VictusFanControl.Runtime;

/// <summary>One-way, process-local boundary for the read-only isolation CLI.</summary>
internal static class WmiOnlyInvestigationPolicy
{
    private static int _enabled;
    private static int _residualEc;
    private static int _deniedEc;
    private static int _deniedWmi;

    internal static bool Enabled => Volatile.Read(ref _enabled) != 0;
    internal static int DeniedEcAccesses => Volatile.Read(ref _deniedEc);
    internal static int DeniedWmiRequests => Volatile.Read(ref _deniedWmi);

    internal static bool ResidualEcAllowed => Volatile.Read(ref _residualEc) != 0;
    internal static bool IsResidualRegister(byte register) => register is 0x34 or 0x35 or 0xEC or 0xF4;

    internal static void Enable(bool residualEc = false)
    {
        if (Enabled)
        {
            if (ResidualEcAllowed != residualEc)
                throw new InvalidOperationException("An installed isolation policy cannot change mode.");
            return;
        }
        if (residualEc) Volatile.Write(ref _residualEc, 1);
        Interlocked.Exchange(ref _enabled, 1);
        EcWmiInvestigationTrace.Record(0, "isolation.enabled",
            residualEc
                ? "Scenario C: HP RPM WMI + EC 0x34/0x35/0xEC/0xF4 reads; HP writes prohibited; firmware retains control"
                : "HP 8C40 fan RPM via WMI only; direct EC and HP writes prohibited; firmware retains control");
    }

    internal static void EnsureDirectEcAllowed()
    {
        if (!Enabled || ResidualEcAllowed) return;
        Interlocked.Increment(ref _deniedEc);
        EcWmiInvestigationTrace.Record(0, "isolation.ec-access.denied", "before module load or port I/O");
        throw new InvalidOperationException("Direct EC access is prohibited during the WMI-only investigation.");
    }

    internal static void EnsureRegisterReadAllowed(byte register)
    {
        EnsureDirectEcAllowed();
        if (!Enabled || IsResidualRegister(register)) return;
        DenyEc($"EC register 0x{register:X2} is outside scenario C.");
    }

    internal static void EnsureProtocolWriteAllowed(byte port, byte value)
    {
        EnsureDirectEcAllowed();
        if (!Enabled || (port == 0x66 && value == 0x80) ||
            (port == 0x62 && IsResidualRegister(value))) return;
        DenyEc("Only the RD_EC command and qualified register address may be sent during C.");
    }

    private static void DenyEc(string reason)
    {
        Interlocked.Increment(ref _deniedEc);
        EcWmiInvestigationTrace.Record(0, "isolation.ec-access.denied", reason);
        throw new InvalidOperationException(reason);
    }

    internal static void EnsureWmiRequestAllowed(HpBiosRequest request)
    {
        if (!Enabled) return;
        if (request.Command == Hp8C40BiosFanControl.DefaultCommand &&
            request.CommandType == Hp8C40BiosFanControl.GetFanLevelCommandType &&
            request.OutputSize == 128 && request.Payload is { Length: 4 } &&
            request.Payload.All(value => value == 0)) return;

        Interlocked.Increment(ref _deniedWmi);
        EcWmiInvestigationTrace.Record(0, "isolation.wmi-request.denied",
            $"command=0x{request.Command:X};type=0x{request.CommandType:X};output={request.OutputSize}");
        throw new InvalidOperationException("WMI-only investigation permits only the qualified HP 20008h/2Dh RPM read.");
    }

    internal static void EnsureTargetAllowed(HardwareTargetProfile? target)
    {
        if (Enabled && target != Hp8C40TargetProfile.Instance)
            throw new InvalidOperationException("WMI-only investigation requires the exact qualified HP 8C40/F.18 target.");
    }
}
