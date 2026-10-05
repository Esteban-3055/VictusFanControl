using VictusFanControl.Hardware.PawnIo;

namespace VictusFanControl.Performance;

internal readonly record struct CpuRaplPowerInfo(
    double ThermalSpecWatts,
    double MinimumWatts,
    double MaximumWatts,
    ulong Raw);

/// <summary>
/// Narrow production-shaped RAPL backend for MSR_PKG_POWER_LIMIT (0x610).
///
/// Ownership is restricted to the two 15-bit PL1/PL2 power fields. Enable,
/// Clamp, Tau, Lock and every reserved/non-power bit are preserved from the
/// current register image. The write surface exposes no caller-selected MSR.
///
/// hardwareWritesAuthorized=false is a hard software gate: Write throws before
/// a PawnIO write session is created or ioctl_write_msr can be invoked.
/// </summary>
internal sealed class PawnIoCpuPowerLimitBackend :
    ICpuPowerLimitBackend,
    IDisposable
{
    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgPowerInfo = 0x614;

    private readonly IntelMsrReader _reader;
    private readonly PawnIoModuleSession? _writer;
    private readonly bool _hardwareWritesAuthorized;
    private bool _disposed;

    internal PawnIoCpuPowerLimitBackend(
        string modulePath,
        bool hardwareWritesAuthorized)
    {
        _reader =
            new IntelMsrReader(
                modulePath);

        try
        {
            UnitsRaw =
                _reader.ReadMsr(
                    MsrRaplPowerUnit);

            PowerUnitWatts =
                CpuRaplPowerLimitCodec.DecodePowerUnitWatts(
                    UnitsRaw);

            PowerInfoRaw =
                _reader.ReadMsr(
                    MsrPkgPowerInfo);

            PowerInfo =
                CpuRaplPowerLimitCodec.DecodePowerInfo(
                    PowerInfoRaw,
                    PowerUnitWatts);

            _hardwareWritesAuthorized =
                hardwareWritesAuthorized;

            if (hardwareWritesAuthorized)
            {
                _writer =
                    new PawnIoModuleSession(
                        modulePath);
            }
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public bool IsSupported =>
        !_disposed;

    internal ulong UnitsRaw { get; }

    internal ulong PowerInfoRaw { get; }

    internal double PowerUnitWatts { get; }

    internal CpuRaplPowerInfo PowerInfo { get; }

    internal Version PawnIoVersion =>
        _reader.PawnIoVersion;

    internal int PhysicalCoreCount =>
        _reader.PhysicalCoreCount;

    internal bool HardwareWritesAuthorized =>
        _hardwareWritesAuthorized;

    public CpuPowerLimitSnapshot Read()
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.DecodeSnapshot(
            _reader.ReadMsr(
                MsrPkgPowerLimit),
            PowerUnitWatts);
    }

    public CpuPowerLimitApplyPlan BuildApplyPlan(
        CpuPowerLimitSnapshot baseline,
        CpuPowerLimitRequest request)
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.BuildPlan(
            baseline,
            baseline,
            request,
            PowerUnitWatts,
            PowerInfo);
    }

    public CpuPowerLimitApplyPlan BuildReacquirePlan(
        CpuPowerLimitSnapshot originalBaseline,
        CpuPowerLimitRequest request,
        CpuPowerLimitSnapshot current)
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.BuildPlan(
            originalBaseline,
            current,
            request,
            PowerUnitWatts,
            PowerInfo);
    }

    public CpuPowerLimitApplyPlan BuildOwnedTransitionPlan(
        CpuPowerLimitSnapshot originalBaseline,
        CpuPowerLimitRequest request,
        CpuPowerLimitSnapshot current)
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.BuildPlan(
            originalBaseline,
            current,
            request,
            PowerUnitWatts,
            PowerInfo);
    }

    public CpuPowerLimitRestorePlan PlanRestore(
        CpuPowerLimitSnapshot restoreTarget,
        ulong appliedRaw,
        CpuPowerLimitSnapshot current)
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.PlanRestore(
            restoreTarget,
            appliedRaw,
            current);
    }

    public bool OwnedFieldsMatch(
        ulong expectedRaw,
        CpuPowerLimitSnapshot current)
    {
        ThrowIfDisposed();

        return CpuRaplPowerLimitCodec.OwnedFieldsMatch(
            expectedRaw,
            current.Raw);
    }

    public void Write(
        ulong raw)
    {
        ThrowIfDisposed();

        if (!_hardwareWritesAuthorized ||
            _writer is null)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_HARDWARE_WRITE_GATE_CLOSED");
        }

        // Exactly one native write attempt. No retry and no hidden reapply.
        _writer.Execute(
            "ioctl_write_msr",
            new ulong[]
            {
                MsrPkgPowerLimit,
                raw
            },
            0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _writer?.Dispose();
        _reader.Dispose();
        _disposed =
            true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            _disposed,
            this);
    }
}

internal static class CpuRaplPowerLimitCodec
{
    internal const ulong Pl1Mask =
        0x7FFFUL;

    internal const ulong Pl2Mask =
        0x7FFFUL << 32;

    internal const ulong OwnedMask =
        Pl1Mask |
        Pl2Mask;

    internal static double DecodePowerUnitWatts(
        ulong unitsRaw)
    {
        var exponent =
            (int)(unitsRaw & 0x0F);

        var unit =
            Math.Pow(
                0.5,
                exponent);

        if (!double.IsFinite(unit) ||
            unit <= 0)
        {
            throw new InvalidDataException(
                "Intel RAPL power unit is invalid.");
        }

        return unit;
    }

    internal static CpuPowerLimitSnapshot DecodeSnapshot(
        ulong raw,
        double powerUnitWatts)
    {
        ValidateUnit(
            powerUnitWatts);

        var pl1Raw =
            raw &
            0x7FFFUL;

        var pl2Raw =
            (raw >> 32) &
            0x7FFFUL;

        return new CpuPowerLimitSnapshot(
            Raw:
                raw,
            Pl1Watts:
                pl1Raw *
                powerUnitWatts,
            Pl2Watts:
                pl2Raw *
                powerUnitWatts,
            Locked:
                (raw &
                 (1UL << 63)) !=
                0);
    }

    internal static CpuRaplPowerInfo DecodePowerInfo(
        ulong raw,
        double powerUnitWatts)
    {
        ValidateUnit(
            powerUnitWatts);

        var thermal =
            (raw &
             0x7FFFUL) *
            powerUnitWatts;

        var minimum =
            ((raw >> 16) &
             0x7FFFUL) *
            powerUnitWatts;

        var maximum =
            ((raw >> 32) &
             0x7FFFUL) *
            powerUnitWatts;

        return new CpuRaplPowerInfo(
            thermal,
            minimum,
            maximum,
            raw);
    }

    internal static CpuPowerLimitApplyPlan BuildPlan(
        CpuPowerLimitSnapshot originalBaseline,
        CpuPowerLimitSnapshot current,
        CpuPowerLimitRequest request,
        double powerUnitWatts,
        CpuRaplPowerInfo powerInfo)
    {
        ValidateUnit(
            powerUnitWatts);

        if (originalBaseline.Locked ||
            current.Locked)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_LOCKED");
        }

        if (!double.IsFinite(
                request.Pl1Watts) ||
            !double.IsFinite(
                request.Pl2Watts) ||
            request.Pl1Watts <
                CpuPowerProductDefaults.MinimumPl1Watts ||
            request.Pl2Watts <
                request.Pl1Watts)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_INVALID_REQUEST");
        }

        var pl1Raw =
            EncodeDown(
                request.Pl1Watts,
                powerUnitWatts);

        var pl2Raw =
            EncodeDown(
                request.Pl2Watts,
                powerUnitWatts);

        if (pl1Raw == 0 ||
            pl2Raw == 0 ||
            pl1Raw >
                0x7FFFUL ||
            pl2Raw >
                0x7FFFUL ||
            pl2Raw <
                pl1Raw)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_REQUEST_NOT_REPRESENTABLE");
        }

        var appliedPl1 =
            pl1Raw *
            powerUnitWatts;

        var appliedPl2 =
            pl2Raw *
            powerUnitWatts;

        if (appliedPl1 <
                CpuPowerProductDefaults.MinimumPl1Watts ||
            appliedPl2 <
                appliedPl1 ||
            appliedPl1 >=
                originalBaseline.Pl1Watts ||
            appliedPl2 >=
                originalBaseline.Pl2Watts)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_REQUEST_NOT_DOWNWARD_FROM_BASELINE");
        }

        if (powerInfo.MinimumWatts >
                0 &&
            appliedPl1 +
                1e-9 <
                powerInfo.MinimumWatts)
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_PL1_BELOW_REPORTED_MINIMUM");
        }

        if (powerInfo.MaximumWatts >
                0 &&
            (appliedPl1 >
                 powerInfo.MaximumWatts +
                 1e-9 ||
             appliedPl2 >
                 powerInfo.MaximumWatts +
                 1e-9))
        {
            throw new InvalidOperationException(
                "CPU_POWER_BACKEND_REQUEST_ABOVE_REPORTED_MAXIMUM");
        }

        var requestedRaw =
            (current.Raw &
             ~OwnedMask) |
            pl1Raw |
            (pl2Raw << 32);

        return new CpuPowerLimitApplyPlan(
            RequestedRaw:
                requestedRaw,
            AppliedPl1Watts:
                appliedPl1,
            AppliedPl2Watts:
                appliedPl2);
    }

    internal static CpuPowerLimitRestorePlan PlanRestore(
        CpuPowerLimitSnapshot restoreTarget,
        ulong appliedRaw,
        CpuPowerLimitSnapshot current)
    {
        if (current.Raw ==
            restoreTarget.Raw)
        {
            return new CpuPowerLimitRestorePlan(
                current.Raw,
                "ALREADY_TARGET");
        }

        if (current.Locked)
        {
            return new CpuPowerLimitRestorePlan(
                current.Raw,
                "RESTORE_BLOCKED_LOCK");
        }

        if (!OwnedFieldsMatch(
                appliedRaw,
                current.Raw))
        {
            return new CpuPowerLimitRestorePlan(
                current.Raw,
                "EXTERNAL_POWER_FIELDS_PRESERVED");
        }

        var value =
            (current.Raw &
             ~OwnedMask) |
            (restoreTarget.Raw &
             OwnedMask);

        return new CpuPowerLimitRestorePlan(
            value,
            value ==
                current.Raw
                ? "ALREADY_TARGET_POWER_FIELDS"
                : "RESTORE_OWNED_FIELDS_PRESERVE_CURRENT_METADATA");
    }

    internal static bool OwnedFieldsMatch(
        ulong expectedRaw,
        ulong currentRaw) =>
        (expectedRaw &
         OwnedMask) ==
        (currentRaw &
         OwnedMask);

    private static ulong EncodeDown(
        double watts,
        double powerUnitWatts) =>
        (ulong)Math.Floor(
            watts /
            powerUnitWatts +
            1e-12);

    private static void ValidateUnit(
        double powerUnitWatts)
    {
        if (!double.IsFinite(
                powerUnitWatts) ||
            powerUnitWatts <=
                0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(powerUnitWatts));
        }
    }
}

internal static class PawnIoCpuPowerLimitBackendSelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            const ulong unitsRaw =
                3UL;

            const ulong baselineRaw =
                0x0042839800DF8168UL;

            var unit =
                CpuRaplPowerLimitCodec.DecodePowerUnitWatts(
                    unitsRaw);

            var baseline =
                CpuRaplPowerLimitCodec.DecodeSnapshot(
                    baselineRaw,
                    unit);

            Require(
                Math.Abs(
                    baseline.Pl1Watts -
                    45) <
                1e-9 &&
                Math.Abs(
                    baseline.Pl2Watts -
                    115) <
                1e-9,
                "known HP 8C40 baseline decodes to 45/115 W");

            var info =
                new CpuRaplPowerInfo(
                    ThermalSpecWatts:
                        45,
                    MinimumWatts:
                        8,
                    MaximumWatts:
                        115,
                    Raw:
                        0);

            var ac =
                CpuRaplPowerLimitCodec.BuildPlan(
                    baseline,
                    baseline,
                    new CpuPowerLimitRequest(
                        35,
                        60),
                    unit,
                    info);

            Require(
                ac.RequestedRaw ==
                    0x004281E000DF8118UL &&
                ac.AppliedPl1Watts ==
                    35 &&
                ac.AppliedPl2Watts ==
                    60,
                "AC 35/60 exact raw encoding");

            var acSnapshot =
                CpuRaplPowerLimitCodec.DecodeSnapshot(
                    ac.RequestedRaw,
                    unit);

            var battery =
                CpuRaplPowerLimitCodec.BuildPlan(
                    baseline,
                    acSnapshot,
                    new CpuPowerLimitRequest(
                        8,
                        15),
                    unit,
                    info);

            Require(
                battery.RequestedRaw ==
                    0x0042807800DF8040UL &&
                battery.AppliedPl1Watts ==
                    8 &&
                battery.AppliedPl2Watts ==
                    15,
                "Battery 8/15 exact raw encoding");

            RequireThrows<InvalidOperationException>(
                () =>
                    CpuRaplPowerLimitCodec.BuildPlan(
                        baseline,
                        baseline,
                        new CpuPowerLimitRequest(
                            8,
                            15),
                        unit,
                        info with
                        {
                            MinimumWatts =
                                10
                        }),
                "nonzero MSR 0x614 minimum blocks an 8 W PL1");

            var metadataCurrent =
                CpuRaplPowerLimitCodec.DecodeSnapshot(
                    ac.RequestedRaw ^
                    (1UL << 16),
                    unit);

            var restore =
                CpuRaplPowerLimitCodec.PlanRestore(
                    baseline,
                    ac.RequestedRaw,
                    metadataCurrent);

            Require(
                CpuRaplPowerLimitCodec.OwnedFieldsMatch(
                    baseline.Raw,
                    restore.Value) &&
                (restore.Value &
                 ~CpuRaplPowerLimitCodec.OwnedMask) ==
                (metadataCurrent.Raw &
                 ~CpuRaplPowerLimitCodec.OwnedMask),
                "restore changes only owned PL fields and preserves current metadata");

            output.WriteLine(
                "PawnIO CPU RAPL production backend self-test: PASS (35/60 and 8/15 encoding, reported-minimum gate, owned-field restore; no PawnIO load/hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "PawnIO CPU RAPL production backend self-test: FAIL - " +
                ex);

            return 1;
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                label);
        }
    }

    private static void RequireThrows<T>(
        Action action,
        string label)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException(
            label);
    }
}
