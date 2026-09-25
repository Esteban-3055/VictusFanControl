using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

namespace VictusFanControl.Watchdog;

internal static class GateCLeaseSelfTest
{
    private static readonly ControllerIdentity Controller =
        new(
            ProcessId: 4242,
            ProcessStartUtcTicks: 638000000000000000);

    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await CaseAsync(
            output,
            "happy state machine persists each critical transition",
            HappyStateMachineAsync);

        failures += await CaseAsync(
            output,
            "owner death in PREPARED clears journal without restore",
            PreparedOwnerLossAsync);

        failures += await CaseAsync(
            output,
            "restart after WRITE_ARMED before WMI clears FF/FF without restore",
            RestartWriteArmedBeforeWriteAsync);

        failures += await CaseAsync(
            output,
            "restart after WMI before Commit restores pending target",
            RestartAfterWriteBeforeCommitAsync);

        failures += await CaseAsync(
            output,
            "restart after Commit restores OWNED target",
            RestartAfterCommitAsync);

        failures += await CaseAsync(
            output,
            "restart during RESTORING completes firmware handoff",
            RestartDuringRestoringAsync);

        failures += await CaseAsync(
            output,
            "WRITE_ARMED transition accepts previous or pending target",
            TransitionAllowsPreviousOrPendingAsync);

        failures += await CaseAsync(
            output,
            "stale generation is rejected without journal mutation",
            StaleGenerationAsync);

        failures += await CaseAsync(
            output,
            "duplicate Release is rejected without hardware write",
            DuplicateReleaseAsync);

        failures += await CaseAsync(
            output,
            "OWNED heartbeat timeout restores",
            HeartbeatTimeoutAsync);

        failures += await CaseAsync(
            output,
            "WRITE_ARMED deadline restores",
            WriteArmedTimeoutAsync);

        failures += await CaseAsync(
            output,
            "RESTORING deadline takes over restore",
            RestoringTimeoutAsync);

        failures += await CaseAsync(
            output,
            "unknown setpoint with active lease is not cleared",
            AmbiguousOwnershipAsync);

        failures += await CaseAsync(
            output,
            "Prepare refuses an external fixed override",
            PrepareRefusesExternalOverrideAsync);

        failures += await CaseAsync(
            output,
            "PREPARED restart never clears an external fixed override",
            PreparedRestartPreservesExternalOverrideAsync);

        failures += await CaseAsync(
            output,
            "fixed setpoint without journal is treated as external",
            ExternalOverrideWithoutLeaseAsync);

        failures += await CaseAsync(
            output,
            "corrupt journal never triggers restore",
            CorruptJournalAsync);

        failures += await CaseAsync(
            output,
            "malformed protocol frame is rejected",
            MalformedFrameAsync);

        failures += await CaseAsync(
            output,
            "named-pipe identity mismatch is rejected",
            NamedPipeIdentityMismatchAsync);

        failures += await CaseAsync(
            output,
            "named-pipe loss while OWNED restores immediately",
            NamedPipeLossRestoresAsync);

        if (failures == 0)
        {
            output.WriteLine(
                "Watchdog Gate C lease/journal/named-pipe self-test: PASS");
            return 0;
        }

        output.WriteLine(
            $"Watchdog Gate C lease/journal/named-pipe self-test: FAIL ({failures} case(s))");
        return 1;
    }

    private static async Task<int> CaseAsync(
        TextWriter output,
        string name,
        Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            output.WriteLine($"  PASS {name}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                $"  FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task HappyStateMachineAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            Assert(prepared.Phase == WatchdogLeasePhase.Prepared);
            Assert((await env.Journal.LoadAsync(CancellationToken.None))?.Phase ==
                   WatchdogLeasePhase.Prepared);

            var armed =
                await env.Manager.WriteIntentAsync(
                    prepared.SessionId,
                    prepared.Generation,
                    new FanSetpoint(30, 30),
                    CancellationToken.None);

            Assert(armed.Phase == WatchdogLeasePhase.WriteArmed);
            Assert((await env.Journal.LoadAsync(CancellationToken.None))?.Phase ==
                   WatchdogLeasePhase.WriteArmed);

            env.Hardware.Set(new FanSetpoint(30, 30));

            var owned =
                await env.Manager.CommitAsync(
                    armed.SessionId,
                    armed.Generation,
                    new FanSetpoint(30, 30),
                    CancellationToken.None);

            Assert(owned.Phase == WatchdogLeasePhase.Owned);
            Assert(owned.Generation == 3);

            await env.Manager.HeartbeatAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            Assert(restoring.Phase == WatchdogLeasePhase.Restoring);

            env.Hardware.Set(new FanSetpoint(255, 255));

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task PreparedOwnerLossAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            var recovery =
                await env.Manager.HandleOwnerLossAsync(
                    Controller,
                    "synthetic process death",
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.ClearedPrepared);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static async Task RestartWriteArmedBeforeWriteAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var armed = await PrepareAndArmAsync(env, 30);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(armed.Phase == WatchdogLeasePhase.WriteArmed);
            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ClearedAlreadyFirmware);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task RestartAfterWriteBeforeCommitAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(30, 30));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
        });
    }

    private static async Task RestartAfterCommitAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);
            Assert(owned.Phase == WatchdogLeasePhase.Owned);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task RestartDuringRestoringAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            Assert(restoring.Phase == WatchdogLeasePhase.Restoring);

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task TransitionAllowsPreviousOrPendingAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned30 = await PrepareArmCommitAsync(env, 30);

            var armed40 =
                await env.Manager.WriteIntentAsync(
                    owned30.SessionId,
                    owned30.Generation,
                    new FanSetpoint(40, 40),
                    CancellationToken.None);

            Assert(armed40.Phase == WatchdogLeasePhase.WriteArmed);

            env.Hardware.Set(new FanSetpoint(30, 30));
            var oldRecovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(oldRecovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);

            await env.ResetAsync();

            var ownedAgain = await PrepareArmCommitAsync(env, 30);
            var armedAgain =
                await env.Manager.WriteIntentAsync(
                    ownedAgain.SessionId,
                    ownedAgain.Generation,
                    new FanSetpoint(40, 40),
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(40, 40));

            var newRecovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(armedAgain.Phase == WatchdogLeasePhase.WriteArmed);
            Assert(newRecovery.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task StaleGenerationAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var prepared =
                await env.Manager.PrepareAsync(
                    Controller,
                    CancellationToken.None);

            var before =
                await env.Journal.LoadAsync(CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.WriteIntentAsync(
                        prepared.SessionId,
                        prepared.Generation - 1,
                        new FanSetpoint(30, 30),
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "STALE_GENERATION");

            var after =
                await env.Journal.LoadAsync(CancellationToken.None);

            Assert(before == after);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task DuplicateReleaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            var restoring =
                await env.Manager.RestoreBeginAsync(
                    owned.SessionId,
                    owned.Generation,
                    CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(255, 255));

            await env.Manager.ReleaseAsync(
                restoring.SessionId,
                restoring.Generation,
                CancellationToken.None);

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.ReleaseAsync(
                        restoring.SessionId,
                        restoring.Generation,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "NO_ACTIVE_LEASE");
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task HeartbeatTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareArmCommitAsync(env, 30);
            env.Clock.Advance(TimeSpan.FromSeconds(6));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
            Assert(env.Hardware.RestoreCalls == 1);
        });
    }

    private static async Task WriteArmedTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(30, 30));
            env.Clock.Advance(TimeSpan.FromSeconds(13));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task RestoringTimeoutAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var owned = await PrepareArmCommitAsync(env, 30);

            await env.Manager.RestoreBeginAsync(
                owned.SessionId,
                owned.Generation,
                CancellationToken.None);

            env.Clock.Advance(TimeSpan.FromSeconds(9));

            var recovery =
                await env.Manager.CheckDeadlinesAsync(
                    CancellationToken.None);

            Assert(recovery?.Disposition ==
                   LeaseRecoveryDisposition.RestoredFirmware);
        });
    }

    private static async Task AmbiguousOwnershipAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await PrepareAndArmAsync(env, 30);
            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.OwnershipAmbiguous);
            Assert(recovery.JournalRetained);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is not null);
        });
    }

    private static async Task PrepareRefusesExternalOverrideAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            env.Hardware.Set(new FanSetpoint(31, 31));

            var ex =
                await ThrowsAsync<LeaseProtocolException>(
                    () => env.Manager.PrepareAsync(
                        Controller,
                        CancellationToken.None).AsTask());

            Assert(ex.Code == "EXTERNAL_OVERRIDE");
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 0);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
        });
    }

    private static async Task PreparedRestartPreservesExternalOverrideAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ExternalOverrideBlocked);
            Assert(!recovery.RestoreAttempted);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.Current == new FanSetpoint(31, 31));
        });
    }

    private static async Task ExternalOverrideWithoutLeaseAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            env.Hardware.Set(new FanSetpoint(31, 31));

            var recovery =
                await env.Manager.RecoverOnStartupAsync(
                    CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.ExternalOverrideBlocked);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task CorruptJournalAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            Directory.CreateDirectory(
                Path.GetDirectoryName(env.Journal.Path)!);

            await File.WriteAllTextAsync(
                env.Journal.Path,
                "{ definitely not valid JSON");

            env.Hardware.Set(new FanSetpoint(30, 30));

            var recovery =
                await env.RestartManager()
                    .RecoverOnStartupAsync(CancellationToken.None);

            Assert(recovery.Disposition ==
                   LeaseRecoveryDisposition.JournalInvalid);
            Assert(recovery.JournalRetained);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task MalformedFrameAsync()
    {
        var payload =
            Encoding.UTF8.GetBytes("{broken");

        var bytes = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(0, 4),
            payload.Length);
        payload.CopyTo(bytes.AsSpan(4));

        await using var stream =
            new MemoryStream(bytes, writable: false);

        var ex =
            await ThrowsAsync<LeaseProtocolException>(
                () => GateCProtocolCodec
                    .ReadRequestAsync(
                        stream,
                        CancellationToken.None)
                    .AsTask());

        Assert(ex.Code == "MALFORMED_MESSAGE");
    }

    private static async Task NamedPipeIdentityMismatchAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var name =
                "VictusFanControl-GateC-" +
                Guid.NewGuid().ToString("N");

            await using var server =
                NewServer(name);

            var serverTask =
                GateCPipeServerSession.RunAsync(
                    server,
                    env.Manager,
                    CancellationToken.None);

            await using var client =
                new NamedPipeClientStream(
                    ".",
                    name,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

            await client.ConnectAsync(5000);

            var actual =
                WindowsNamedPipeIdentity.CurrentProcessIdentity();

            var hello = new GateCRequest(
                GateCProtocol.Version,
                Guid.NewGuid(),
                GateCProtocol.Hello,
                ControllerPid: actual.ProcessId + 1,
                ControllerStartUtcTicks:
                    actual.ProcessStartUtcTicks);

            await GateCProtocolCodec.WriteRequestAsync(
                client,
                hello,
                CancellationToken.None);

            var response =
                await GateCProtocolCodec.ReadResponseAsync(
                    client,
                    CancellationToken.None);

            Assert(response is not null);
            Assert(!response!.Ok);
            Assert(response.Code == "IDENTITY_MISMATCH");

            client.Dispose();
            await serverTask.ConfigureAwait(false);

            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
            Assert(env.Hardware.RestoreCalls == 0);
        });
    }

    private static async Task NamedPipeLossRestoresAsync()
    {
        await WithEnvironmentAsync(async env =>
        {
            var name =
                "VictusFanControl-GateC-" +
                Guid.NewGuid().ToString("N");

            await using var server =
                NewServer(name);

            var serverTask =
                GateCPipeServerSession.RunAsync(
                    server,
                    env.Manager,
                    CancellationToken.None);

            await using (var client =
                new NamedPipeClientStream(
                    ".",
                    name,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous))
            {
                await client.ConnectAsync(5000);

                var actual =
                    WindowsNamedPipeIdentity.CurrentProcessIdentity();

                var hello =
                    await RoundTripAsync(
                        client,
                        new GateCRequest(
                            GateCProtocol.Version,
                            Guid.NewGuid(),
                            GateCProtocol.Hello,
                            ControllerPid: actual.ProcessId,
                            ControllerStartUtcTicks:
                                actual.ProcessStartUtcTicks));

                Assert(hello.Ok);

                var prepared =
                    await RoundTripAsync(
                        client,
                        Request(GateCProtocol.Prepare));

                Assert(prepared.Ok);

                var armed =
                    await RoundTripAsync(
                        client,
                        Request(
                            GateCProtocol.WriteIntent,
                            prepared.SessionId,
                            prepared.Generation,
                            30,
                            30));

                Assert(armed.Ok);

                env.Hardware.Set(new FanSetpoint(30, 30));

                var owned =
                    await RoundTripAsync(
                        client,
                        Request(
                            GateCProtocol.Commit,
                            armed.SessionId,
                            armed.Generation,
                            30,
                            30));

                Assert(owned.Ok);
            }

            await serverTask.ConfigureAwait(false);

            Assert(env.Hardware.RestoreCalls == 1);
            Assert(env.Hardware.Current.IsFirmwareOwned);
            Assert(
                await env.Journal.LoadAsync(CancellationToken.None) is null);
        });
    }

    private static NamedPipeServerStream NewServer(
        string name) =>
        new(
            name,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

    private static GateCRequest Request(
        string type,
        Guid? sessionId = null,
        long? generation = null,
        int? cpu = null,
        int? gpu = null) =>
        new(
            GateCProtocol.Version,
            Guid.NewGuid(),
            type,
            SessionId: sessionId,
            Generation: generation,
            CpuLevel: cpu,
            GpuLevel: gpu);

    private static async Task<GateCResponse> RoundTripAsync(
        Stream stream,
        GateCRequest request)
    {
        await GateCProtocolCodec.WriteRequestAsync(
            stream,
            request,
            CancellationToken.None);

        return
            await GateCProtocolCodec.ReadResponseAsync(
                stream,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "Named-pipe server closed before sending a response.");
    }

    private static async Task<LeaseOperationResult> PrepareAndArmAsync(
        TestEnvironment env,
        byte level)
    {
        var prepared =
            await env.Manager.PrepareAsync(
                Controller,
                CancellationToken.None);

        return await env.Manager.WriteIntentAsync(
            prepared.SessionId,
            prepared.Generation,
            new FanSetpoint(level, level),
            CancellationToken.None);
    }

    private static async Task<LeaseOperationResult> PrepareArmCommitAsync(
        TestEnvironment env,
        byte level)
    {
        var armed =
            await PrepareAndArmAsync(env, level);

        env.Hardware.Set(new FanSetpoint(level, level));

        return await env.Manager.CommitAsync(
            armed.SessionId,
            armed.Generation,
            new FanSetpoint(level, level),
            CancellationToken.None);
    }

    private static async Task WithEnvironmentAsync(
        Func<TestEnvironment, Task> action)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-GateC-" +
                Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            var env = new TestEnvironment(root);
            await action(env).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task<TException> ThrowsAsync<TException>(
        Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name} was not thrown.");
    }

    private static void Assert(
        bool condition,
        string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                message ?? "Assertion failed.");
        }
    }

    private sealed class TestEnvironment
    {
        public TestEnvironment(string root)
        {
            Journal =
                new JsonLeaseJournal(
                    Path.Combine(root, "lease.json"));

            Hardware = new FakeHardware();
            Clock = new FakeClock();
            Manager = NewManager();
        }

        public JsonLeaseJournal Journal { get; }
        public FakeHardware Hardware { get; }
        public FakeClock Clock { get; }
        public WatchdogLeaseManager Manager { get; private set; }

        public WatchdogLeaseManager RestartManager()
        {
            Manager = NewManager();
            return Manager;
        }

        public async Task ResetAsync()
        {
            await Journal.DeleteAsync(CancellationToken.None);
            Hardware.Reset();
            Clock.Reset();
            Manager = NewManager();
        }

        private WatchdogLeaseManager NewManager() =>
            new(
                Journal,
                Hardware,
                Clock);
    }

    private sealed class FakeHardware : ILeaseRecoveryHardware
    {
        public FanSetpoint Current { get; private set; } =
            new(255, 255);

        public int RestoreCalls { get; private set; }

        public bool FailRestore { get; set; }

        public ValueTask<FanSetpoint> ReadSetpointAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Current);
        }

        public ValueTask RestoreFirmwareAutoAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreCalls++;

            if (FailRestore)
            {
                throw new InvalidOperationException(
                    "synthetic restore failure");
            }

            Current = new FanSetpoint(255, 255);
            return ValueTask.CompletedTask;
        }

        public void Set(FanSetpoint setpoint) =>
            Current = setpoint;

        public void Reset()
        {
            Current = new FanSetpoint(255, 255);
            RestoreCalls = 0;
            FailRestore = false;
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public ulong Milliseconds { get; private set; }

        public void Advance(TimeSpan duration) =>
            Milliseconds += checked((ulong)duration.TotalMilliseconds);

        public void Reset() =>
            Milliseconds = 0;
    }
}
