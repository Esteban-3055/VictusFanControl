namespace VictusFanControl.Hardware.PawnIo;

/// <summary>
/// Stabilizes the two independently exposed EC fan-setpoint bytes without
/// assuming that CPU/GPU values must be equal. A real external ownership change
/// is therefore still returned once it is stable; only one-off/torn snapshots
/// are rejected.
/// </summary>
internal static class FanSetpointSnapshotStabilizer
{
    internal const int RequiredConsecutiveMatchingSnapshots = 2;
    internal const int MaximumSnapshots = 6;

    internal readonly record struct Snapshot(byte CpuSetpoint, byte GpuSetpoint)
    {
        public override string ToString() =>
            $"{CpuSetpoint}/{GpuSetpoint}";
    }

    internal static Snapshot ReadStable(
        Func<Snapshot> readSnapshot,
        int requiredConsecutive =
            RequiredConsecutiveMatchingSnapshots,
        int maximumSnapshots = MaximumSnapshots)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);

        if (requiredConsecutive < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredConsecutive),
                "Setpoint coherence requires at least two consecutive snapshots.");
        }

        if (maximumSnapshots < requiredConsecutive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSnapshots),
                "Maximum snapshots must cover the consecutive-snapshot requirement.");
        }

        Snapshot? previous = null;
        var consecutive = 0;
        var observed = new List<Snapshot>(maximumSnapshots);

        for (var read = 0; read < maximumSnapshots; read++)
        {
            var current = readSnapshot();
            observed.Add(current);

            if (previous.HasValue &&
                current == previous.Value)
            {
                consecutive++;
            }
            else
            {
                consecutive = 1;
            }

            if (consecutive >= requiredConsecutive)
            {
                return current;
            }

            previous = current;
        }

        throw new InvalidDataException(
            $"EC fan setpoint pair did not stabilize across {maximumSnapshots} " +
            $"successful snapshots; required {requiredConsecutive} consecutive " +
            $"identical pairs. Observed: {string.Join(" -> ", observed)}.");
    }
}
