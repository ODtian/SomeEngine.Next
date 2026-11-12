namespace SomeEngine.Job;

public readonly record struct EventReaderPosition(Guid Id, ulong Position);

/// <summary>Independent event values and consumer progress for a checkpoint or a file encoder.</summary>
public sealed class EventLogData<T> where T : unmanaged
{
    public T[] Values { get; set; } = [];
    public int Count { get; set; }
    public ulong Through { get; set; }
    public EventReaderPosition[] Readers { get; set; } = [];
    public int ReaderCount { get; set; }
}
