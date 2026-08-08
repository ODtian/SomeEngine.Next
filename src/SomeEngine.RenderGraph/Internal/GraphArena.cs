using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.RenderGraph;

/// <summary>One invocation-owned page-backed bump arena; allocations never move.</summary>
internal sealed unsafe class GraphArena : IDisposable
{
    private const int DefaultPageSize = 256 * 1024;
    private const int MaximumPooledPageSize = 16 * 1024 * 1024;
    private const int MaximumPooledBytes = 64 * 1024 * 1024;
    private static readonly Lock PoolGate = new();
    private static Page* s_pooledPages;
    private static int s_pooledBytes;

    private Page* _first;
    private Page* _current;
    private bool _disposed;

    private struct Page
    {
        internal Page* Next;
        internal int Index;
        internal int Capacity;
        internal int Offset;
    }

    internal int UsedBytes { get; private set; }
    internal int PageCount { get; private set; }
    internal int StaleBytes { get; private set; }
    internal int ClearedBytes { get; private set; }

    internal void MarkStale(int byteCount) =>
        StaleBytes = checked(StaleBytes + byteCount);

    internal static int AlignmentOf<T>()
    {
        int size = Unsafe.SizeOf<T>();
        return size >= 16 ? 16 : size >= 8 ? 8 : size >= 4 ? 4 : size >= 2 ? 2 : 1;
    }

    internal long AllocateBytes(int byteCount, int alignment, out Span<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(alignment));

        Page* page = _current;
        int offset = page is null ? 0 : GetAlignedOffset(page, alignment);
        if (page is null || checked(offset + byteCount) > page->Capacity)
        {
            page = AddPage(checked(byteCount + alignment));
            offset = GetAlignedOffset(page, alignment);
        }

        page->Offset = checked(offset + byteCount);
        UsedBytes = checked(UsedBytes + byteCount);
        bytes = new Span<byte>((byte*)(page + 1) + offset, byteCount);
        return Encode(page->Index, offset);
    }

    internal ReadOnlySpan<byte> GetBytes(long address, int byteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (byteCount < 0) throw new ArgumentOutOfRangeException(nameof(byteCount));
        Decode(address, out int pageIndex, out int offset);
        Page* page = FindPage(pageIndex);
        if ((uint)offset > (uint)page->Offset || (uint)byteCount > (uint)(page->Offset - offset))
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        return new ReadOnlySpan<byte>((byte*)(page + 1) + offset, byteCount);
    }

    internal ArenaSlice<T> AllocateSlice<T>(int count, bool clear = true) where T : unmanaged
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return default;
        int byteCount = checked(count * sizeof(T));
        _ = AllocateBytes(byteCount, AlignmentOf<T>(), out Span<byte> allocation);
        if (clear)
        {
            allocation.Clear();
            ClearedBytes = checked(ClearedBytes + byteCount);
        }
        fixed (byte* pointer = allocation)
            return new ArenaSlice<T>((T*)pointer, count);
    }

    internal void* GetPointer(long address, int byteCount)
    {
        Decode(address, out int pageIndex, out int offset);
        Page* page = FindPage(pageIndex);
        if ((uint)offset > (uint)page->Offset || (uint)byteCount > (uint)(page->Offset - offset))
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        return (byte*)(page + 1) + offset;
    }

    internal bool TryExtend(long address, int currentByteCount, int requiredByteCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (currentByteCount < 0 || requiredByteCount < currentByteCount)
            throw new ArgumentOutOfRangeException(nameof(requiredByteCount));
        Decode(address, out int pageIndex, out int offset);
        Page* page = _current;
        if (page is null || page->Index != pageIndex || offset + currentByteCount != page->Offset)
            return false;
        int requiredEnd = checked(offset + requiredByteCount);
        if (requiredEnd > page->Capacity) return false;
        page->Offset = requiredEnd;
        UsedBytes = checked(UsedBytes + requiredByteCount - currentByteCount);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Page* page = _first;
        _first = null;
        _current = null;
        while (page is not null)
        {
            Page* next = page->Next;
            ReturnPage(page);
            page = next;
        }
        UsedBytes = 0;
        PageCount = 0;
        StaleBytes = 0;
        ClearedBytes = 0;
    }

    private Page* AddPage(int requiredCapacity)
    {
        int nextCapacity = _current is null
            ? DefaultPageSize
            : checked(_current->Capacity * 2);
        int capacity = Math.Max(nextCapacity, requiredCapacity);
        Page* page = RentPage(capacity);
        page->Next = null;
        page->Index = _current is null ? 0 : checked(_current->Index + 1);
        page->Offset = 0;
        if (_first is null) _first = page;
        else _current->Next = page;
        _current = page;
        PageCount++;
        return page;
    }

    private static Page* RentPage(int minimumCapacity)
    {
        lock (PoolGate)
        {
            Page* previous = null;
            Page* candidate = s_pooledPages;
            while (candidate is not null && candidate->Capacity < minimumCapacity)
            {
                previous = candidate;
                candidate = candidate->Next;
            }
            if (candidate is not null)
            {
                if (previous is null)
                    s_pooledPages = candidate->Next;
                else
                    previous->Next = candidate->Next;
                s_pooledBytes -= candidate->Capacity;
                candidate->Next = null;
                return candidate;
            }
        }

        nuint bytes = checked((nuint)sizeof(Page) + (nuint)minimumCapacity);
        Page* page = (Page*)NativeMemory.Alloc(bytes);
        if (page is null) throw new OutOfMemoryException();
        page->Capacity = minimumCapacity;
        return page;
    }

    private static void ReturnPage(Page* page)
    {
        page->Offset = 0;
        page->Index = 0;
        if (page->Capacity > MaximumPooledPageSize)
        {
            NativeMemory.Free(page);
            return;
        }

        lock (PoolGate)
        {
            if (s_pooledBytes > MaximumPooledBytes - page->Capacity)
            {
                NativeMemory.Free(page);
                return;
            }
            page->Next = s_pooledPages;
            s_pooledPages = page;
            s_pooledBytes += page->Capacity;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetAlignedOffset(Page* page, int alignment)
    {
        nuint start = (nuint)(page + 1);
        nuint address = checked(start + (uint)page->Offset);
        nuint aligned = (address + (uint)alignment - 1) & ~((nuint)alignment - 1);
        return checked((int)(aligned - start));
    }

    private Page* FindPage(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        Page* page = _current is not null && index == _current->Index ? _current : _first;
        while (page is not null && page->Index != index) page = page->Next;
        if (page is null) throw new ArgumentOutOfRangeException(nameof(index));
        return page;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Encode(int pageIndex, int offset) =>
        checked(((long)pageIndex << 32) | (uint)offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Decode(long address, out int pageIndex, out int offset)
    {
        if (address < 0) throw new ArgumentOutOfRangeException(nameof(address));
        pageIndex = checked((int)(address >> 32));
        offset = checked((int)unchecked((uint)address));
    }
}

internal readonly unsafe struct ArenaSlice<T> : IReadOnlyList<T> where T : unmanaged
{
    private readonly T* _items;

    internal ArenaSlice(T* items, int length)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        _items = items;
        Length = length;
    }

    public int Length { get; }
    public int Count => Length;
    public bool IsEmpty
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Length == 0;
    }
    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items is null ? default : new Span<T>(_items, Length);
    }
    public ReadOnlySpan<T> ReadOnlySpan
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Span;
    }
    internal T* DangerousPointer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<T> AsSpan() => Span;
    T IReadOnlyList<T>.this[int index] => this[index];

    public T[] ToArray() => ReadOnlySpan.ToArray();

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ArenaSlice<T> Slice(int offset, int length)
    {
        if ((uint)offset > (uint)Length || (uint)length > (uint)(Length - offset))
            throw new ArgumentOutOfRangeException(nameof(length));
        return length == 0
            ? default
            : new ArenaSlice<T>(_items + offset, length);
    }

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _items[index];
        }
    }

    public struct Enumerator : IEnumerator<T>
    {
        private readonly ArenaSlice<T> _slice;
        private int _index;

        internal Enumerator(ArenaSlice<T> slice)
        {
            _slice = slice;
            _index = -1;
        }

        public T Current => _slice[_index];
        object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _slice.Length;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
