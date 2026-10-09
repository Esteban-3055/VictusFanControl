using System.Runtime.CompilerServices;

namespace VictusFanControl.Telemetry;

/// <summary>
/// Mirrors a validated fresh control read to live periodic readers using the
/// same native slot. It stores no sample and never supplies command proof.
/// Recipients and their epochs are captured before the native query starts.
/// </summary>
internal static class HpWmiFanSamplePublication
{
    private static readonly ConditionalWeakTable<SemaphoreSlim, Recipients> Slots = new();

    internal static void Register(SemaphoreSlim slot, HpWmiFanTelemetryReader reader) =>
        Slots.GetValue(slot, _ => new Recipients()).Register(reader);

    internal static Action<HpWmiFanTelemetrySample> Capture(SemaphoreSlim slot) =>
        Slots.GetValue(slot, _ => new Recipients()).Capture();

    internal static long NextSequence(SemaphoreSlim slot) =>
        Slots.GetValue(slot, _ => new Recipients()).NextSequence();

    private sealed class Recipients
    {
        private readonly object _gate = new();
        private readonly List<WeakReference<HpWmiFanTelemetryReader>> _readers = new();
        private long _sequence;

        internal long NextSequence() => Interlocked.Increment(ref _sequence);

        internal void Register(HpWmiFanTelemetryReader reader)
        {
            lock (_gate)
            {
                _readers.RemoveAll(reference => !reference.TryGetTarget(out _));
                _readers.Add(new(reader));
            }
        }

        internal Action<HpWmiFanTelemetrySample> Capture()
        {
            var sequence = NextSequence();
            HpWmiFanTelemetryReader[] readers;
            lock (_gate)
            {
                _readers.RemoveAll(reference => !reference.TryGetTarget(out _));
                readers = _readers.Select(reference =>
                    reference.TryGetTarget(out var reader) ? reader : null)
                    .OfType<HpWmiFanTelemetryReader>().ToArray();
            }

            // Do not hold the registry lock while acquiring a reader's lock.
            var sinks = readers.Select(reader => reader.CaptureControlSampleSink(sequence))
                .OfType<Action<HpWmiFanTelemetrySample>>().ToArray();
            return sample =>
            {
                foreach (var sink in sinks) sink(sample);
            };
        }
    }
}
