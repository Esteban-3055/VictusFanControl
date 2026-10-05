namespace VictusFanControl.Performance;

internal static class GpuClockSessionJournalSelfTest
{
    private const string TargetProfileId =
        "HP-8C40-9D0R1LA-F18";

    internal static int Run(
        TextWriter output)
    {
        var root =
            Path.Combine(
                Path.GetTempPath(),
                "VictusFanControl-GpuClockJournal-" +
                Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);

            var path =
                Path.Combine(
                    root,
                    "active-gpu-clock-session.json");

            var journal =
                new JsonGpuClockSessionJournal(
                    path,
                    TargetProfileId);

            var created =
                DateTimeOffset.UtcNow;

            var session =
                Guid.NewGuid();

            var ac =
                new GpuClockLimitRequest(
                    210,
                    1850);

            var battery =
                new GpuClockLimitRequest(
                    210,
                    1200);

            var armed =
                new GpuClockSessionJournalRecord(
                    GpuClockSessionJournalRecord.CurrentSchemaVersion,
                    TargetProfileId,
                    session,
                    1,
                    GpuClockJournalPhase.ApplyWriteArmed,
                    CommittedRequest: null,
                    PendingRequest: ac,
                    RecoveryReason: null,
                    CreatedAtUtc: created,
                    UpdatedAtUtc: created);

            journal.Store(armed);

            Require(
                journal.Load() == armed,
                "apply armed GPU journal round-trips");

            var active =
                armed with
                {
                    Generation = 2,
                    Phase =
                        GpuClockJournalPhase.ActiveUnverified,
                    CommittedRequest = ac,
                    PendingRequest = null,
                    UpdatedAtUtc =
                        created.AddSeconds(1)
                };

            journal.Store(active);

            Require(
                journal.Load() == active,
                "active-unverified GPU journal round-trips");

            var concurrentReader =
                new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite |
                    FileShare.Delete);

            var releaseReader =
                Task.Run(
                    async () =>
                    {
                        await Task.Delay(30);
                        concurrentReader.Dispose();
                    });

            var pollingSafe =
                active with
                {
                    Generation = 6,
                    UpdatedAtUtc =
                        created.AddMilliseconds(1500)
                };

            journal.Store(
                pollingSafe);

            releaseReader.GetAwaiter()
                .GetResult();

            active =
                pollingSafe;

            Require(
                journal.Load() == active,
                "GPU journal replacement retries across a transient polling-reader conflict");

            var switching =
                active with
                {
                    Generation = 3,
                    Phase =
                        GpuClockJournalPhase.PresetSwitchWriteArmed,
                    PendingRequest = battery,
                    UpdatedAtUtc =
                        created.AddSeconds(2)
                };

            journal.Store(switching);

            Require(
                journal.Load() == switching,
                "preset-switch GPU journal preserves old and pending requests");

            RequireThrows<InvalidDataException>(
                () =>
                    journal.Store(
                        switching with
                        {
                            Generation = 4,
                            PendingRequest = ac
                        }),
                "preset switch rejects identical old/new requests");

            Require(
                journal.Load() == switching,
                "invalid replacement leaves previous GPU journal generation");

            var recovery =
                switching with
                {
                    Generation = 4,
                    Phase =
                        GpuClockJournalPhase.RecoveryRequired,
                    RecoveryReason =
                        "SUSPEND_OR_DRIVER_RESET",
                    UpdatedAtUtc =
                        created.AddSeconds(3)
                };

            journal.Store(recovery);

            Require(
                journal.Load() == recovery,
                "recovery-required GPU evidence round-trips");

            var wrongTarget =
                new JsonGpuClockSessionJournal(
                    path,
                    "HP-OTHER-TARGET");

            RequireThrows<InvalidDataException>(
                () => wrongTarget.Load(),
                "GPU journal target mismatch rejected");

            journal.Delete();

            Require(
                !File.Exists(path),
                "GPU journal delete removes resolved session");

            output.WriteLine(
                "GPU clock durable session journal self-test: PASS (independent journal, atomic replace, concurrent poller sharing, target/phase invariants).");

            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "GPU clock durable session journal self-test: FAIL - " +
                ex.Message);

            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(
                        root,
                        recursive: true);
                }
            }
            catch
            {
                // Test cleanup failure does not hide the primary assertion.
            }
        }
    }

    private static void Require(
        bool condition,
        string label)
    {
        if (!condition)
            throw new InvalidOperationException(label);
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

        throw new InvalidOperationException(label);
    }
}
