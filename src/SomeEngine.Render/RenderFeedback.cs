namespace SomeEngine.Render;

public readonly record struct RenderFeedbackRecord<T>(ulong FrameIdentity, T Value) where T : unmanaged;

/// <summary>Transfers completed GPU observations to the next game update.</summary>
public sealed class RenderFeedback<T> where T : unmanaged
{
    private readonly object _gate = new();
    private readonly Queue<RenderFeedbackRecord<T>> _records = new();

    public void Write(ulong frameIdentity, in T value)
    {
        lock (_gate) _records.Enqueue(new(frameIdentity, value));
    }

    public void Write(ulong frameIdentity, scoped ReadOnlySpan<T> values)
    {
        lock (_gate)
            foreach (T value in values) _records.Enqueue(new(frameIdentity, value));
    }

    public bool TryRead(ulong completedFrame, out RenderFeedbackRecord<T> record)
    {
        lock (_gate)
        {
            if (_records.TryPeek(out record) && record.FrameIdentity <= completedFrame)
                return _records.TryDequeue(out record);
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate) _records.Clear();
    }
}
