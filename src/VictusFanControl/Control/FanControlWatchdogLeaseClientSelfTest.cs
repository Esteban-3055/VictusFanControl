using System.IO.Pipes;
using VictusFanControl.Runtime;

namespace VictusFanControl.Control;

internal static class FanControlWatchdogLeaseClientSelfTest
{
    public static async Task<int> RunAsync(
        TextWriter output)
    {
        var failures = 0;

        failures += await RunCaseAsync(
            output,
            "real named-pipe client completes lease protocol sequence",
            HappyProtocolSequenceAsync);

        failures += await RunCaseAsync(
            output,
            "client rejects mismatched response request id",
            RequestIdMismatchAsync);

        failures += await RunCaseAsync(
            output,
            "broken watchdog pipe is classified as WATCHDOG_IPC_LOSS",
            BrokenPipeClassificationAsync);

        failures += await RunCaseAsync(
            output,
            "watchdog request timeout excludes suspended wall time",
            RequestTimeoutExcludesSuspendedWallTimeAsync);

        return failures;
    }

    private static async Task<int> RunCaseAsync(
        TextWriter output,
        string name,
        Func<Task> test)
    {
        try
        {
            await test().ConfigureAwait(false);
            output.WriteLine($"PASS  {name}");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                $"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task HappyProtocolSequenceAsync()
    {
        var pipeName =
            "VictusFanControl-LeaseClientTest-" +
            Guid.NewGuid().ToString("N");

        var sessionId = Guid.NewGuid();

        await using var server =
            new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync()
                .ConfigureAwait(false);

            var hello = await RequireRequestAsync(server);
            AssertType(
                hello,
                FanControlWatchdogLeaseContract.Hello);

            await ReplyAsync(
                server,
                hello,
                code: "HELLO_OK",
                message: "test hello");

            var prepare = await RequireRequestAsync(server);
            AssertType(
                prepare,
                FanControlWatchdogLeaseContract.Prepare);

            await ReplyAsync(
                server,
                prepare,
                sessionId: sessionId,
                generation: 1,
                phase: "Prepared");

            var intent = await RequireRequestAsync(server);
            AssertType(
                intent,
                FanControlWatchdogLeaseContract.WriteIntent);

            if (intent.SessionId != sessionId ||
                intent.Generation != 1 ||
                intent.CpuLevel != 30 ||
                intent.GpuLevel != 30)
            {
                throw new InvalidOperationException(
                    "WriteIntent payload does not match the active lease.");
            }

            await ReplyAsync(
                server,
                intent,
                sessionId: sessionId,
                generation: 2,
                phase: "WriteArmed");

            var commit = await RequireRequestAsync(server);
            AssertType(
                commit,
                FanControlWatchdogLeaseContract.Commit);

            if (commit.SessionId != sessionId ||
                commit.Generation != 2 ||
                commit.CpuLevel != 30 ||
                commit.GpuLevel != 30)
            {
                throw new InvalidOperationException(
                    "Commit payload does not match the active lease.");
            }

            await ReplyAsync(
                server,
                commit,
                sessionId: sessionId,
                generation: 3,
                phase: "Owned");

            var probe = await RequireRequestAsync(server);
            AssertType(
                probe,
                FanControlWatchdogLeaseContract.Probe);

            await ReplyAsync(
                server,
                probe,
                sessionId: sessionId,
                generation: 3,
                phase: "Owned");

            var heartbeat = await RequireRequestAsync(server);
            AssertType(
                heartbeat,
                FanControlWatchdogLeaseContract.Heartbeat);

            await ReplyAsync(
                server,
                heartbeat,
                sessionId: sessionId,
                generation: 3,
                phase: "Owned");

            var restoreBegin = await RequireRequestAsync(server);
            AssertType(
                restoreBegin,
                FanControlWatchdogLeaseContract.RestoreBegin);

            await ReplyAsync(
                server,
                restoreBegin,
                sessionId: sessionId,
                generation: 4,
                phase: "Restoring");

            var release = await RequireRequestAsync(server);
            AssertType(
                release,
                FanControlWatchdogLeaseContract.Release);

            await ReplyAsync(
                server,
                release,
                message: "released");
        });

        await using var client =
            new NamedPipeFanControlWatchdogLeaseClient(
                pipeName);

        await client.PrepareAsync(CancellationToken.None);
        await client.WriteIntentAsync(
            30,
            30,
            CancellationToken.None);
        await client.CommitAsync(
            30,
            30,
            CancellationToken.None);
        await client.ProbeAsync(
            CancellationToken.None);
        await client.HeartbeatAsync(
            CancellationToken.None);
        await client.RestoreBeginAsync(
            CancellationToken.None);
        await client.ReleaseAsync(
            CancellationToken.None);

        await serverTask.ConfigureAwait(false);
    }

    private static async Task RequestTimeoutExcludesSuspendedWallTimeAsync()
    {
        var pipeName =
            "VictusFanControl-LeaseClientActiveTime-" +
            Guid.NewGuid().ToString("N");

        var sessionId = Guid.NewGuid();

        await using var server =
            new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync()
                .ConfigureAwait(false);

            var hello = await RequireRequestAsync(server);
            AssertType(
                hello,
                FanControlWatchdogLeaseContract.Hello);

            await ReplyAsync(
                server,
                hello,
                code: "HELLO_OK",
                message: "test hello");

            var prepare = await RequireRequestAsync(server);
            AssertType(
                prepare,
                FanControlWatchdogLeaseContract.Prepare);

            // Delay beyond the configured request timeout in wall time while
            // the injected active-time clock remains frozen, representing S3.
            await Task.Delay(150).ConfigureAwait(false);

            await ReplyAsync(
                server,
                prepare,
                sessionId: sessionId,
                generation: 1,
                phase: "Prepared");
        });

        var timing = new FanControlWatchdogLeaseClientTiming(
            ConnectTimeout: TimeSpan.FromMilliseconds(500),
            RequestTimeout: TimeSpan.FromMilliseconds(40),
            ReleaseTimeout: TimeSpan.FromMilliseconds(100));

        await using var client =
            new NamedPipeFanControlWatchdogLeaseClient(
                pipeName,
                activeTimeClock: new FrozenActiveTimeClock(),
                timing: timing);

        await client.PrepareAsync(CancellationToken.None);
        await serverTask.ConfigureAwait(false);
    }

    private static async Task BrokenPipeClassificationAsync()
    {
        var pipeName =
            "VictusFanControl-LeaseClientBrokenPipe-" +
            Guid.NewGuid().ToString("N");

        var sessionId = Guid.NewGuid();

        await using var server =
            new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync()
                .ConfigureAwait(false);

            var hello = await RequireRequestAsync(server);
            AssertType(
                hello,
                FanControlWatchdogLeaseContract.Hello);

            await ReplyAsync(
                server,
                hello,
                code: "HELLO_OK",
                message: "test hello");

            var prepare = await RequireRequestAsync(server);
            AssertType(
                prepare,
                FanControlWatchdogLeaseContract.Prepare);

            await ReplyAsync(
                server,
                prepare,
                sessionId: sessionId,
                generation: 1,
                phase: "Prepared");

            var intent = await RequireRequestAsync(server);
            AssertType(
                intent,
                FanControlWatchdogLeaseContract.WriteIntent);

            await ReplyAsync(
                server,
                intent,
                sessionId: sessionId,
                generation: 2,
                phase: "WriteArmed");

            var commit = await RequireRequestAsync(server);
            AssertType(
                commit,
                FanControlWatchdogLeaseContract.Commit);

            await ReplyAsync(
                server,
                commit,
                sessionId: sessionId,
                generation: 3,
                phase: "Owned");

            var probe = await RequireRequestAsync(server);
            AssertType(
                probe,
                FanControlWatchdogLeaseContract.Probe);

            // Simulate watchdog-process/pipe death after receiving the
            // non-renewing liveness probe but before returning a response.
            server.Disconnect();
        });

        await using var client =
            new NamedPipeFanControlWatchdogLeaseClient(
                pipeName);

        await client.PrepareAsync(CancellationToken.None);
        await client.WriteIntentAsync(
            30,
            30,
            CancellationToken.None);
        await client.CommitAsync(
            30,
            30,
            CancellationToken.None);

        var classified = false;

        try
        {
            await client.ProbeAsync(
                CancellationToken.None);
        }
        catch (FanControlWatchdogTransportException ex)
            when (ex.Message.Contains(
                FanControlWatchdogTransportException.Marker,
                StringComparison.Ordinal) &&
                  string.Equals(
                      ex.Operation,
                      FanControlWatchdogLeaseContract.Probe,
                      StringComparison.Ordinal))
        {
            classified = true;
        }

        await serverTask.ConfigureAwait(false);

        if (!classified)
        {
            throw new InvalidOperationException(
                "Broken watchdog pipe was not surfaced as a stable WATCHDOG_IPC_LOSS transport failure.");
        }
    }

    private static async Task RequestIdMismatchAsync()
    {
        var pipeName =
            "VictusFanControl-LeaseClientMismatch-" +
            Guid.NewGuid().ToString("N");

        await using var server =
            new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

        var serverTask = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync()
                .ConfigureAwait(false);

            var hello = await RequireRequestAsync(server);

            await ReplyAsync(
                server,
                hello,
                code: "HELLO_OK",
                message: "test hello");

            var prepare = await RequireRequestAsync(server);

            await FanControlWatchdogLeaseCodec.WriteResponseAsync(
                server,
                new FanControlWatchdogLeaseResponse(
                    FanControlWatchdogLeaseContract.ProtocolVersion,
                    Guid.NewGuid(),
                    Ok: true,
                    Code: "OK",
                    Message: "deliberately wrong request id",
                    SessionId: Guid.NewGuid(),
                    Generation: 1,
                    Phase: "Prepared"),
                CancellationToken.None);
        });

        await using var client =
            new NamedPipeFanControlWatchdogLeaseClient(
                pipeName);

        var rejected = false;

        try
        {
            await client.PrepareAsync(
                CancellationToken.None);
        }
        catch (InvalidDataException ex)
            when (ex.Message.Contains(
                "request-id mismatch",
                StringComparison.OrdinalIgnoreCase))
        {
            rejected = true;
        }

        await serverTask.ConfigureAwait(false);

        if (!rejected)
        {
            throw new InvalidOperationException(
                "Client accepted a response with the wrong request id.");
        }
    }

    private sealed class FrozenActiveTimeClock : IActiveTimeClock
    {
        public ulong Milliseconds => 0;
    }

    private static async Task<FanControlWatchdogLeaseRequest>
        RequireRequestAsync(
            Stream stream)
    {
        return
            await FanControlWatchdogLeaseCodec.ReadRequestAsync(
                stream,
                CancellationToken.None) ??
            throw new EndOfStreamException(
                "Client closed before sending the expected request.");
    }

    private static ValueTask ReplyAsync(
        Stream stream,
        FanControlWatchdogLeaseRequest request,
        string code = "OK",
        string message = "ok",
        Guid? sessionId = null,
        long? generation = null,
        string? phase = null) =>
        FanControlWatchdogLeaseCodec.WriteResponseAsync(
            stream,
            new FanControlWatchdogLeaseResponse(
                FanControlWatchdogLeaseContract.ProtocolVersion,
                request.RequestId,
                Ok: true,
                Code: code,
                Message: message,
                SessionId: sessionId,
                Generation: generation,
                Phase: phase),
            CancellationToken.None);

    private static void AssertType(
        FanControlWatchdogLeaseRequest request,
        string expected)
    {
        if (request.ProtocolVersion !=
                FanControlWatchdogLeaseContract.ProtocolVersion ||
            !string.Equals(
                request.Type,
                expected,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected request '{expected}', received '{request.Type}' v{request.ProtocolVersion}.");
        }
    }
}
