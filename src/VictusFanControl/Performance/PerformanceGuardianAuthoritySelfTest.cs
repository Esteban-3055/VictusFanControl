namespace VictusFanControl.Performance;

internal static class PerformanceGuardianAuthoritySelfTest
{
    internal static int Run(
        TextWriter output)
    {
        try
        {
            var nonce =
                Guid.NewGuid();

            var gate =
                new PerformanceGuardianAuthority(
                    "HP-8C40-9D0R1LA-F18",
                    ownerPid: 4242,
                    ownerStartUtcTicks: 638951234567890000,
                    sessionNonce: nonce);

            var enableBeforeHello =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.EnableSession,
                        cpu: true,
                        gpu: true));

            Require(
                !enableBeforeHello.Accepted &&
                enableBeforeHello.Code ==
                    "HELLO_REQUIRED",
                "enable before hello rejected");

            var badNonce =
                gate.Handle(
                    Request(
                        Guid.NewGuid(),
                        PerformanceGuardianProtocol.Hello,
                        ownerPid: 4242,
                        ownerTicks:
                            638951234567890000));

            Require(
                !badNonce.Accepted &&
                badNonce.Code ==
                    "AUTH_NONCE",
                "wrong nonce rejected");

            var hello =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Hello,
                        ownerPid: 4242,
                        ownerTicks:
                            638951234567890000));

            Require(
                hello.Accepted &&
                hello.Code ==
                    "HELLO_OK" &&
                gate.Phase ==
                    PerformanceGuardianAuthorityPhase.Idle,
                "hello accepted");

            var enabled =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.EnableSession,
                        cpu: true,
                        gpu: true));

            Require(
                enabled.Accepted &&
                gate.SessionEnabled &&
                gate.CpuEnabled &&
                gate.GpuEnabled,
                "explicit session authority enabled");

            var reconnectHello =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Hello,
                        ownerPid: 4242,
                        ownerTicks:
                            638951234567890000));

            Require(
                reconnectHello.Accepted &&
                gate.SessionEnabled,
                "same launch tuple may reconnect without losing authority");

            var changedDomains =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.EnableSession,
                        cpu: true,
                        gpu: false));

            Require(
                !changedDomains.Accepted,
                "active domain selection cannot mutate implicitly");

            var disabled =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.DisableSession));

            Require(
                disabled.Accepted &&
                gate.Phase ==
                    PerformanceGuardianAuthorityPhase.Idle &&
                !gate.CpuEnabled &&
                !gate.GpuEnabled,
                "disable revokes authority");

            _ =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.EnableSession,
                        cpu: true,
                        gpu: false));

            var parentLost =
                gate.MarkParentLost();

            Require(
                parentLost.Accepted &&
                gate.Phase ==
                    PerformanceGuardianAuthorityPhase.ParentLost &&
                !gate.SessionEnabled &&
                !gate.CpuEnabled &&
                !gate.GpuEnabled,
                "parent loss revokes startup/apply authority");

            var statusAfterParentLoss =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Status));

            Require(
                !statusAfterParentLoss.Accepted &&
                statusAfterParentLoss.Code ==
                    "GUARDIAN_STOPPING",
                "parent-lost guardian rejects later client authority");

            output.WriteLine(
                "Performance Guardian authority self-test: PASS (nonce/owner binding, explicit session authority, reconnect, parent-loss revoke, no hardware I/O).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "Performance Guardian authority self-test: FAIL - " +
                ex.Message);

            return 1;
        }
    }

    private static PerformanceGuardianRequest Request(
        Guid nonce,
        string type,
        int? ownerPid = null,
        long? ownerTicks = null,
        bool? cpu = null,
        bool? gpu = null) =>
        new(
            PerformanceGuardianProtocol.Version,
            Guid.NewGuid(),
            "HP-8C40-9D0R1LA-F18",
            nonce,
            type,
            ownerPid,
            ownerTicks,
            cpu,
            gpu);

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
    }
}
