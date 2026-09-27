using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Containers;

/// <summary>A synchronous non-reentrant write over successive parts of a range; offsets count elements.</summary>
public delegate void PageWrite<T>(long offset, scoped Span<T> values);

/// <summary>
/// Editable elements with a shared balanced index. Branches share pages; writes detach only
/// the affected paths and pages. Element references retain ordinary C# reference semantics.
/// </summary>
public class PageBuffer<T> : IDisposable
{
    private object Gate => Access.SyncRoot;
    private readonly PageArena _arena;
    private readonly int _pageLength;
    private PageIndex<T>? _root;
    private bool _disposed;

    public PageBuffer(PageArena arena, long length = 0, PageAccess? access = null)
        : this(arena, length, (arena ?? throw new ArgumentNullException(nameof(arena))).MaximumPageSize / Unsafe.SizeOf<T>(), access: access) { }

    internal PageBuffer(PageArena arena, long length, int pageLength, ResidencyReservation? reservation = null, PageAccess? access = null)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (pageLength == 0) throw new ArgumentException("An element exceeds the maximum page size.", nameof(arena));
        _arena = arena;
        Access = access ?? new();
        _pageLength = pageLength;
        _root = CreatePages(length, reservation);
    }

    private PageBuffer(PageArena arena, PageIndex<T>? root, int pageLength, PageAccess? access)
    { _arena = arena; _root = root?.Retain(); _pageLength = pageLength; Access = access ?? new(); }

    protected PageBuffer(PageBuffer<T> source, PageAccess? access = null)
    {
        lock (source.Gate)
        {
            source.RequireIdle();
            _arena = source._arena;
            Access = access ?? new();
            _pageLength = source._pageLength;
            _root = source._root?.Retain();
        }
    }

    protected PageBuffer(PageBuffer<T> source, long offset, long length)
    {
        lock (source.Gate)
        {
            source.RequireIdle();
            source.CheckRange(offset, length);
            _arena = source._arena;
            Access = new();
            _pageLength = source._pageLength;
            _root = PageIndex<T>.Slice(source._root, offset, length);
        }
    }
    public long Length { get { lock (Gate) { RequireIdle(); return _root?.Length ?? 0; } } }
    public PageArena Arena => _arena;
    public PageAccess Access { get; }
    public T this[long index]
    {
        get => ReadElement(index);
        set => WriteElement(index, value);
    }

    public virtual PageBuffer<T> Duplicate(PageAccess? access = null)
    {
        lock (Gate) { RequireIdle(); return new(_arena, _root, _pageLength, access); }
    }

    /// <summary>Creates an editable branch over a subrange by sharing its index and pages.</summary>
    public virtual PageBuffer<T> Slice(long offset, long length)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, length);
            var result = new PageBuffer<T>(_arena, 0, _pageLength);
            result._root = PageIndex<T>.Slice(_root, offset, length);
            return result;
        }
    }

    public T ReadElement(long offset)
    {
        T value = default!;
        Read(offset, MemoryMarshal.CreateSpan(ref value, 1));
        return value;
    }
    public void Read(long offset, Span<T> destination)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, destination.Length);
            while (!destination.IsEmpty)
            {
                PageIndex<T> leaf = _root!.Find(offset, out int start);
                int count = Math.Min(destination.Length, (int)leaf.Length - start);
                leaf.Page!.Memory.Span.Slice(leaf.Start + start, count).CopyTo(destination);
                destination = destination[count..];
                offset += count;
            }
        }
    }

    public void WriteElement(long offset, T value)
        => Write(offset, MemoryMarshal.CreateReadOnlySpan(ref value, 1));
    public void Write(long offset, ReadOnlySpan<T> source)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, source.Length);
            while (!source.IsEmpty)
            {
                PageIndex<T> leaf = GetWritablePage(ref _root, offset, out int start);
                int count = Math.Min(source.Length, (int)leaf.Length - start);
                source[..count].CopyTo(leaf.Page!.Memory.Span.Slice(leaf.Start + start, count));
                offset += count;
                source = source[count..];
            }
        }
    }

    /// <summary>
    /// Calls the writer once per affected page. The offset is relative to the requested range.
    /// A throwing callback may leave earlier writes applied; the branch becomes usable again.
    /// Its scoped span cannot escape through safe C#; all reentrant branch access is rejected.
    /// </summary>
    public void Write(long offset, long length, PageWrite<T> writer) => Write(offset, length, writer, null);

    internal void Write(long offset, long length, PageWrite<T> writer, Action? validate)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, length);
            validate?.Invoke();
            Access.BeginWrite();
        }
        try
        {
            long position = 0;
            while (position < length)
            {
                PageIndex<T> leaf = GetWritablePage(ref _root, offset + position, out int start);
                int count = (int)Math.Min(length - position, leaf.Length - start);
                writer(position, leaf.Page!.Memory.Span.Slice(leaf.Start + start, count));
                position += count;
            }
        }
        finally { lock (Gate) Access.EndWrite(); }
    }

    /// <summary>
    /// Retains only pages intersecting the range and builds its BCL sequence once. Memory and
    /// spans obtained from the result remain valid until that result is disposed.
    /// </summary>
    public PageRange<T> Retain(long offset, long length)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, length);
            return new PageRange<T>(_root, offset, length);
        }
    }

    public PageRange<T> Retain()
    {
        lock (Gate) { RequireIdle(); return new PageRange<T>(_root, 0, _root?.Length ?? 0); }
    }

    public void Resize(long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        lock (Gate)
        {
            RequireIdle();
            long current = _root?.Length ?? 0;
            if (length == current) return;
            PageIndex<T>? replacement;
            if (length > current)
            {
                PageIndex<T>? extra = CreatePages(length - current);
                try { replacement = PageIndex<T>.Join(_root, extra); }
                finally { extra?.Release(); }
            }
            else replacement = PageIndex<T>.Slice(_root, 0, length);
            _root?.Release();
            _root = replacement;
        }
    }

    /// <summary>Inserts bytes by replacing only index paths; the existing suffix is shared.</summary>
    public void Insert(long offset, ReadOnlySpan<T> bytes)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, 0);
            using var inserted = new PageBuffer<T>(_arena, bytes.Length, _pageLength);
            inserted.Write(0, bytes);
            ReplaceRange(offset, 0, inserted._root);
        }
    }

    public void Remove(long offset, long length)
    {
        lock (Gate)
        {
            RequireIdle();
            CheckRange(offset, length);
            ReplaceRange(offset, length, null);
        }
    }

    private void ReplaceRange(long offset, long length, PageIndex<T>? inserted)
    {
        PageIndex<T>? first = null, last = null, prefix = null;
        try
        {
            first = PageIndex<T>.Slice(_root, 0, offset);
            last = PageIndex<T>.Slice(_root, offset + length, (_root?.Length ?? 0) - offset - length);
            prefix = PageIndex<T>.Join(first, inserted);
            PageIndex<T>? replacement = PageIndex<T>.Join(prefix, last);
            _root?.Release();
            _root = replacement;
        }
        finally { first?.Release(); last?.Release(); prefix?.Release(); }
    }

    // Document decoding and source reads call this on a newly allocated, unpublished buffer. Awaiting each
    // write is essential: cancellation must not recycle memory while the source still uses it.
    internal async ValueTask FillAsync(Func<long, Memory<T>, CancellationToken, ValueTask> fill,
        CancellationToken cancellationToken)
    {
        long length;
        lock (Gate)
        {
            RequireIdle();
            length = _root?.Length ?? 0;
            Access.BeginWrite();
        }
        try
        {
            long offset = 0;
            while (offset < length)
            {
                PageIndex<T> leaf = _root!.Find(offset, out int start);
                int count = (int)leaf.Length - start;
                await fill(offset, leaf.Page!.Memory.Slice(leaf.Start + start, count), cancellationToken).ConfigureAwait(false);
                offset += count;
            }
        }
        finally { lock (Gate) Access.EndWrite(); }
    }

    internal ReadOnlyMemory<T> ReadSingleMemory()
    {
        lock (Gate)
        {
            RequireIdle();
            if (_root is null) return ReadOnlyMemory<T>.Empty;
            if (_root.Page is null) throw new InvalidOperationException("The content spans multiple pages; retain a page range to read its sequence.");
            return _root.Page.Memory.Slice(_root.Start, (int)_root.Length);
        }
    }

    private PageIndex<T>? CreatePages(long length, ResidencyReservation? reservation = null)
    {
        PageIndex<T>? result = null;
        try
        {
            while (length > 0)
            {
                int size = (int)Math.Min(length, _pageLength);
                var leaf = new PageIndex<T>(_arena.Rent<T>(size, reservation), 0, size);
                PageIndex<T>? next;
                try { next = PageIndex<T>.Join(result, leaf); }
                finally { leaf.Release(); }
                result?.Release();
                result = next;
                length -= size;
            }
            return result;
        }
        catch { result?.Release(); throw; }
    }

    private PageIndex<T> GetWritablePage(ref PageIndex<T>? node, long offset, out int start)
    {
        if (node!.IsShared)
        {
            var replacement = node.Copy();
            node.Release();
            node = replacement;
        }
        if (node.Page is not null)
        {
            if (node.Page.IsShared)
            {
                Page<T> replacement = _arena.GetWritablePage(node.Page);
                if (!ReferenceEquals(replacement, node.Page)) node.Page.Release();
                node.Page = replacement;
            }
            start = (int)offset;
            return node;
        }
        long leftLength = node.Left!.Length;
        return offset < leftLength
            ? GetWritablePage(ref node.Left, offset, out start)
            : GetWritablePage(ref node.Right, offset - leftLength, out start);
    }

    private void CheckRange(long offset, long length)
    {
        long available = _root?.Length ?? 0;
        if (offset < 0 || offset > available || length < 0 || length > available - offset)
            throw new ArgumentOutOfRangeException(nameof(offset), "The range is outside the buffer.");
    }

    private void RequireIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Access.RequireIdle();
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            RequireIdle();
            _disposed = true;
            _root?.Release();
            _root = null;
        }
    }
}
