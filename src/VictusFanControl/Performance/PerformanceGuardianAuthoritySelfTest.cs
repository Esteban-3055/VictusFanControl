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

            var update = Request(nonce, PerformanceGuardianProtocol.UpdateConfiguration) with
                { Configuration = new PerformanceGuiSessionConfiguration() };
            Require(!gate.Handle(update).Accepted, "update before enabled session rejected");

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

            Require(!gate.Handle(update with { SessionNonce = Guid.NewGuid() }).Accepted,
                "live update requires launch nonce");
            Require(gate.Handle(update).Accepted && gate.CpuEnabled && gate.GpuEnabled,
                "live update authorized without mutating active domain selection");
            Require(!gate.Handle(update with { Configuration = null }).Accepted,
                "live update requires presets");

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

            var shutdownAfterDisable =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Shutdown));

            Require(
                shutdownAfterDisable.Accepted &&
                shutdownAfterDisable.Code ==
                    "SHUTDOWN_ACCEPTED" &&
                shutdownAfterDisable.Phase ==
                    PerformanceGuardianAuthorityPhase.Stopped &&
                gate.Phase ==
                    PerformanceGuardianAuthorityPhase.Stopped &&
                !gate.SessionEnabled &&
                !gate.CpuEnabled &&
                !gate.GpuEnabled,
                "shutdown after DISABLE_SESSION must transition Idle to Stopped");

            var statusAfterShutdown =
                gate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Status));

            Require(
                !statusAfterShutdown.Accepted &&
                statusAfterShutdown.Code ==
                    "GUARDIAN_STOPPING",
                "stopped guardian rejects later client authority");

            Require(!gate.Handle(update).Accepted, "stopped guardian rejects live update");

            var parentGate =
                new PerformanceGuardianAuthority(
                    "HP-8C40-9D0R1LA-F18",
                    ownerPid: 4242,
                    ownerStartUtcTicks: 638951234567890000,
                    sessionNonce: nonce);

            _ =
                parentGate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Hello,
                        ownerPid: 4242,
                        ownerTicks:
                            638951234567890000));

            _ =
                parentGate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.EnableSession,
                        cpu: true,
                        gpu: false));

            var parentLost =
                parentGate.MarkParentLost();

            Require(
                parentLost.Accepted &&
                parentGate.Phase ==
                    PerformanceGuardianAuthorityPhase.ParentLost &&
                !parentGate.SessionEnabled &&
                !parentGate.CpuEnabled &&
                !parentGate.GpuEnabled,
                "parent loss revokes startup/apply authority");

            var statusAfterParentLoss =
                parentGate.Handle(
                    Request(
                        nonce,
                        PerformanceGuardianProtocol.Status));

            Require(
                !statusAfterParentLoss.Accepted &&
                statusAfterParentLoss.Code ==
                    "GUARDIAN_STOPPING",
                "parent-lost guardian rejects later client authority");

            output.WriteLine(
                "Performance Guardian authority self-test: PASS (nonce/owner binding, explicit session authority, reconnect, disable-then-shutdown Stopped transition, parent-loss revoke, no hardware I/O).");

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
