namespace VictusFanControl.Performance;

internal enum PerformanceGuardianAuthorityPhase
{
    WaitingForHello,
    Idle,
    SessionEnabled,
    ParentLost,
    Stopped
}

internal readonly record struct PerformanceGuardianAuthorityResult(
    bool Accepted,
    string Code,
    string Message,
    PerformanceGuardianAuthorityPhase Phase,
    bool SessionEnabled,
    bool CpuEnabled,
    bool GpuEnabled);

/// <summary>
/// Pure startup/session authority gate for the detached Performance Guardian.
///
/// The launch tuple (target, owner PID/start-time, nonce) is fixed by the
/// launcher. HELLO authenticates that tuple. Merely starting the guardian or
/// observing AC/Battery never grants Apply authority.
///
/// ENABLE_SESSION is the only command that grants live startup/apply authority.
/// Recovery authority remains separate and is not represented by this gate.
/// </summary>
internal sealed class PerformanceGuardianAuthority
{
    private readonly string _targetProfileId;
    private readonly int _ownerPid;
    private readonly long _ownerStartUtcTicks;
    private readonly Guid _sessionNonce;

    internal PerformanceGuardianAuthority(
        string targetProfileId,
        int ownerPid,
        long ownerStartUtcTicks,
        Guid sessionNonce)
    {
        if (string.IsNullOrWhiteSpace(
                targetProfileId))
        {
            throw new ArgumentException(
                "Performance Guardian target profile id is required.",
                nameof(targetProfileId));
        }

        if (ownerPid <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerPid));
        }

        if (ownerStartUtcTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ownerStartUtcTicks));
        }

        if (sessionNonce ==
            Guid.Empty)
        {
            throw new ArgumentException(
                "Performance Guardian session nonce cannot be empty.",
                nameof(sessionNonce));
        }

        _targetProfileId =
            targetProfileId;

        _ownerPid =
            ownerPid;

        _ownerStartUtcTicks =
            ownerStartUtcTicks;

        _sessionNonce =
            sessionNonce;
    }

    internal PerformanceGuardianAuthorityPhase Phase { get; private set; } =
        PerformanceGuardianAuthorityPhase.WaitingForHello;

    internal bool CpuEnabled { get; private set; }

    internal bool GpuEnabled { get; private set; }

    internal bool SessionEnabled =>
        Phase ==
        PerformanceGuardianAuthorityPhase.SessionEnabled;

    internal PerformanceGuardianAuthorityResult Handle(
        PerformanceGuardianRequest request)
    {
        if (request.ProtocolVersion !=
            PerformanceGuardianProtocol.Version)
        {
            return Reject(
                "PROTOCOL_VERSION",
                "Performance Guardian protocol version mismatch.");
        }

        if (request.RequestId ==
            Guid.Empty)
        {
            return Reject(
                "REQUEST_ID",
                "Performance Guardian request id cannot be empty.");
        }

        if (!string.Equals(
                request.TargetProfileId,
                _targetProfileId,
                StringComparison.Ordinal))
        {
            return Reject(
                "TARGET_MISMATCH",
                "Performance Guardian target profile mismatch.");
        }

        if (request.SessionNonce !=
            _sessionNonce)
        {
            return Reject(
                "AUTH_NONCE",
                "Performance Guardian session nonce mismatch.");
        }

        if (Phase is
            PerformanceGuardianAuthorityPhase.ParentLost or
            PerformanceGuardianAuthorityPhase.Stopped)
        {
            return Reject(
                "GUARDIAN_STOPPING",
                "Performance Guardian authority is no longer active.");
        }

        return request.Type switch
        {
            PerformanceGuardianProtocol.Hello =>
                HandleHello(request),

            PerformanceGuardianProtocol.EnableSession =>
                HandleEnable(request),

            PerformanceGuardianProtocol.DisableSession =>
                HandleDisable(),

            PerformanceGuardianProtocol.Status =>
                Accept(
                    "STATUS_OK",
                    "Performance Guardian status returned."),

            PerformanceGuardianProtocol.Shutdown =>
                HandleShutdown(),

            _ =>
                Reject(
                    "UNKNOWN_COMMAND",
                    "Unknown Performance Guardian command.")
        };
    }

    private PerformanceGuardianAuthorityResult HandleShutdown()
    {
        CpuEnabled =
            false;

        GpuEnabled =
            false;

        Phase =
            PerformanceGuardianAuthorityPhase.Stopped;

        return Accept(
            "SHUTDOWN_ACCEPTED",
            "Performance Guardian shutdown requested.");
    }

    internal PerformanceGuardianAuthorityResult MarkParentLost()
    {
        CpuEnabled =
            false;

        GpuEnabled =
            false;

        Phase =
            PerformanceGuardianAuthorityPhase.ParentLost;

        return Accept(
            "PARENT_LOST",
            "Performance Guardian owner process handle signaled exit.");
    }

    internal PerformanceGuardianAuthorityResult MarkStopped()
    {
        CpuEnabled =
            false;

        GpuEnabled =
            false;

        Phase =
            PerformanceGuardianAuthorityPhase.Stopped;

        return Accept(
            "STOPPED",
            "Performance Guardian stopped.");
    }

    private PerformanceGuardianAuthorityResult HandleHello(
        PerformanceGuardianRequest request)
    {
        if (request.OwnerPid !=
                _ownerPid ||
            request.OwnerStartUtcTicks !=
                _ownerStartUtcTicks)
        {
            return Reject(
                "OWNER_IDENTITY",
                "Performance Guardian owner identity mismatch.");
        }

        if (Phase ==
            PerformanceGuardianAuthorityPhase.WaitingForHello)
        {
            Phase =
                PerformanceGuardianAuthorityPhase.Idle;
        }

        return Accept(
            "HELLO_OK",
            "Performance Guardian owner identity and nonce accepted.");
    }

    private PerformanceGuardianAuthorityResult HandleEnable(
        PerformanceGuardianRequest request)
    {
        if (Phase ==
            PerformanceGuardianAuthorityPhase.WaitingForHello)
        {
            return Reject(
                "HELLO_REQUIRED",
                "ENABLE_SESSION requires HELLO first.");
        }

        var cpu =
            request.CpuEnabled ??
            false;

        var gpu =
            request.GpuEnabled ??
            false;

        if (!cpu &&
            !gpu)
        {
            return Reject(
                "EMPTY_SESSION",
                "ENABLE_SESSION requires CPU and/or GPU authority.");
        }

        if (Phase ==
            PerformanceGuardianAuthorityPhase.SessionEnabled)
        {
            if (CpuEnabled ==
                    cpu &&
                GpuEnabled ==
                    gpu)
            {
                return Accept(
                    "SESSION_ALREADY_ENABLED",
                    "Performance Guardian session is already enabled with the same domains.");
            }

            return Reject(
                "SESSION_ALREADY_ENABLED",
                "Performance Guardian session domain selection cannot change without DISABLE_SESSION.");
        }

        CpuEnabled =
            cpu;

        GpuEnabled =
            gpu;

        Phase =
            PerformanceGuardianAuthorityPhase.SessionEnabled;

        return Accept(
            "SESSION_ENABLED",
            "Explicit Performance Guardian startup/apply authority granted.");
    }

    private PerformanceGuardianAuthorityResult HandleDisable()
    {
        if (Phase ==
            PerformanceGuardianAuthorityPhase.WaitingForHello)
        {
            return Reject(
                "HELLO_REQUIRED",
                "DISABLE_SESSION requires HELLO first.");
        }

        CpuEnabled =
            false;

        GpuEnabled =
            false;

        Phase =
            PerformanceGuardianAuthorityPhase.Idle;

        return Accept(
            "SESSION_DISABLED",
            "Performance Guardian live session authority revoked.");
    }

    private PerformanceGuardianAuthorityResult Accept(
        string code,
        string message) =>
        new(
            Accepted: true,
            code,
            message,
            Phase,
            SessionEnabled,
            CpuEnabled,
            GpuEnabled);

    private PerformanceGuardianAuthorityResult Reject(
        string code,
        string message) =>
        new(
            Accepted: false,
            code,
            message,
            Phase,
            SessionEnabled,
            CpuEnabled,
            GpuEnabled);
}
