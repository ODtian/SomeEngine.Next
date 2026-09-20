using System.Buffers;

namespace SomeEngine.Serialization.Containers;

/// <summary>A retained range. Dispose ends the allowed use of every memory or span obtained from it.</summary>
public sealed class PageRange<T> : IDisposable
{
    private PageSegment? _first;
    private ReadOnlySequence<T> _sequence;
    private int _disposed;

    internal PageRange(PageIndex<T>? root, long offset, long length)
    {
        PageSegment? last = null;
        try
        {
            while (length > 0)
            {
                PageIndex<T> leaf = root!.Find(offset, out int start);
                int count = (int)Math.Min(length, leaf.Length - start);
                var segment = new PageSegment(leaf.Page!, leaf.Start + start, count);
                if (last is null) _first = segment;
                else last.Append(segment);
                last = segment;
                offset += count;
                length -= count;
            }
            _sequence = _first is null ? ReadOnlySequence<T>.Empty
                : new(_first, 0, last!, last!.Memory.Length);
        }
        catch { Dispose(); throw; }
    }

    public ReadOnlySequence<T> Sequence
    {
        get { ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this); return _sequence; }
    }

    /// <summary>Creates an ordinary GC-managed copy which may outlive this read.</summary>
    public T[] ToArray() => Sequence.ToArray();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        PageSegment? current = _first;
        _first = null;
        _sequence = default;
        while (current is not null) { current.Page.Release(); current = current.NextPage; }
    }

    private sealed class PageSegment : ReadOnlySequenceSegment<T>
    {
        internal readonly Page<T> Page;
        internal PageSegment(Page<T> page, int start, int length)
        { Page = page.Retain(); Memory = page.Memory.Slice(start, length); }
        internal PageSegment? NextPage => (PageSegment?)Next;
        internal void Append(PageSegment next)
        { next.RunningIndex = RunningIndex + Memory.Length; Next = next; }
    }
}
