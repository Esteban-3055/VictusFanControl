namespace VictusFanControl.Performance;

internal static class CpuPowerSessionJournalSelfTest
{
    private const string TargetProfileId = "HP-8C40-9D0R1LA-F18";

    internal static int Run(TextWriter output)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "VictusFanControl-CpuPowerJournal-" +
            Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "active-session.json");
            var journal =
                new JsonCpuPowerSessionJournal(
                    path,
                    TargetProfileId);

            var created = DateTimeOffset.UtcNow;
            var session = Guid.NewGuid();
            var baseline =
                new CpuPowerLimitSnapshot(
                    0x0042839800DF8168UL,
                    45,
                    115,
                    false);

            var request =
                new CpuPowerLimitRequest(20, 40);

            var inactiveConflict =
                new CpuPowerConflictSnapshot(
                    CpuPowerConflictState.Inactive,
                    0,
                    CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
                    false,
                    null,
                    null,
                    0,
                    0);

            var armed =
                new CpuPowerSessionJournalRecord(
                    CpuPowerSessionJournalRecord.CurrentSchemaVersion,
                    TargetProfileId,
                    session,
                    1,
                    CpuPowerJournalPhase.WriteArmed,
                    baseline,
                    request,
                    0x0042814000DF80A0UL,
                    null,
                    inactiveConflict,
                    0x0042814000DF80A0UL,
                    created,
                    created);

            journal.Store(armed);
            Require(File.Exists(path), "journal file exists after durable store");

            var loadedArmed =
                journal.Load() ??
                throw new InvalidOperationException(
                    "stored journal could not be loaded");

            Require(loadedArmed == armed, "write-armed record round-trips exactly");

            var owned =
                armed with
                {
                    Generation = 2,
                    Phase = CpuPowerJournalPhase.Owned,
                    PendingRaw = null,
                    UpdatedAtUtc = created.AddSeconds(1)
                };

            journal.Store(owned);
            var loadedOwned =
                journal.Load() ??
                throw new InvalidOperationException(
                    "updated journal could not be loaded");

            Require(loadedOwned == owned, "atomic replacement exposes newest generation");
            Require(
                Directory.GetFiles(root, "*.tmp").Length == 0,
                "no temporary journal files remain after replace");

            var contestedConflict =
                new CpuPowerConflictSnapshot(
                    CpuPowerConflictState.Contested,
                    0,
                    CpuPowerConflictPolicy.DefaultMaxReacquireAttempts,
                    false,
                    1000,
                    1000,
                    1000,
                    0);

            var external =
                new CpuPowerLimitSnapshot(
                    0x0042825800DF80F0UL,
                    30,
                    60,
                    false);

            var contested =
                owned with
                {
                    Generation = 3,
                    Phase = CpuPowerJournalPhase.Contested,
                    ExternalHandoff = external,
                    Conflict = contestedConflict,
                    UpdatedAtUtc = created.AddSeconds(2)
                };

            journal.Store(contested);
            Require(journal.Load() == contested, "contested handoff round-trips exactly");

            var wrongTarget =
                new JsonCpuPowerSessionJournal(
                    path,
                    "HP-OTHER-TARGET");

            RequireThrows<InvalidDataException>(
                () => wrongTarget.Load(),
                "target mismatch is rejected");

            var invalidContested =
                contested with
                {
                    Generation = 4,
                    ExternalHandoff = null
                };

            RequireThrows<InvalidDataException>(
                () => journal.Store(invalidContested),
                "contested phase without handoff is rejected");

            Require(
                journal.Load() == contested,
                "invalid replacement leaves previous durable generation intact");

            File.WriteAllText(path, "{ definitely-not-json");
            RequireThrows<InvalidDataException>(
                () => journal.Load(),
                "malformed JSON is rejected");

            journal.Store(owned with
            {
                Generation = 5,
                UpdatedAtUtc = created.AddSeconds(3)
            });

            journal.Delete();
            Require(!File.Exists(path), "journal delete removes resolved session");

            output.WriteLine(
                "CPU power durable session journal self-test: PASS (atomic replace, target gate, phase invariants).");
            return 0;
        }
        catch (Exception ex)
        {
            output.WriteLine(
                "CPU power durable session journal self-test: FAIL - " +
                ex.Message);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
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
