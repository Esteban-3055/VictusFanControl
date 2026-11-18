namespace VictusFanControl.Runtime;

public sealed record RuntimeProgressEntry(long Sequence,string Phase,long TickMilliseconds,DateTimeOffset ObservedUtc,string? Session,long Generation,DateTimeOffset? AcquiredUtc);

/// <summary>Bounded passive observations, not acquisitions or ownership receipts. No disk IO.</summary>
public static class RuntimeProgressJournal
{
    private static readonly object Sync=new();
    private static readonly RuntimeProgressEntry?[] Entries=new RuntimeProgressEntry?[256];
    private static long _sequence;
    public static void Record(string phase,string? session=null,long generation=0,DateTimeOffset? acquiredUtc=null)
    {
        lock(Sync)
        {
            var sequence=++_sequence;
            Entries[(sequence-1)%Entries.Length]=new(sequence,phase.Length>128?phase[..128]:phase,Environment.TickCount64,DateTimeOffset.UtcNow,
                session?.Length>128?session[..128]:session,generation,acquiredUtc);
        }
    }
    public static IReadOnlyList<RuntimeProgressEntry> Capture()
    {lock(Sync)return Array.AsReadOnly(Entries.Where(entry=>entry is not null).Cast<RuntimeProgressEntry>().OrderBy(entry=>entry.Sequence).ToArray());}
}
