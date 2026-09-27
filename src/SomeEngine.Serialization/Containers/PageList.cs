using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Serialization.Containers;

/// <summary>
/// Elements in shared typed pages. Indexing returns an ordinary value or object reference.
/// Assignments isolate page contents; objects referred to by elements retain normal C#
/// reference semantics. Assets must declare shareable value shapes for nested isolation.
/// </summary>
public sealed class PageList<T> : IList<T>, IReadOnlyList<T>, IDisposable
{
    private readonly PageBuffer<T> _buffer;
    private int _count;

    public PageList(PageArena arena, PageAccess? access = null) => _buffer = new(arena, access: access);

    public PageList(PageArena arena, ReadOnlySpan<T> values) : this(arena)
    {
        try { AddRange(values); }
        catch { _buffer.Dispose(); throw; }
    }

    private PageList(PageBuffer<T> buffer, int count) { _buffer = buffer; _count = count; }
    public int Count { get { lock (Access.SyncRoot) { _ = _buffer.Length; return _count; } } }
    public bool IsReadOnly => false;
    public PageArena Arena => _buffer.Arena;
    public PageAccess Access => _buffer.Access;

    /// <summary>Copies values into the selected arena, or shares the source pages when that arena already holds them.</summary>
    public static PageList<T> Create(PageArena arena, IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is PageList<T> pages && ReferenceEquals(pages.Arena, arena)) return pages.Duplicate();
        var result = new PageList<T>(arena);
        try { foreach (T value in values) result.Add(value); return result; }
        catch { result.Dispose(); throw; }
    }

    public void Clear() { lock (Access.SyncRoot) { _buffer.Resize(0); _count = 0; } }
    public bool Contains(T item) => IndexOf(item) >= 0;
    public int IndexOf(T item)
    {
        lock (Access.SyncRoot)
        {
            for (int i = 0; i < Count; i++)
                if (EqualityComparer<T>.Default.Equals(this[i], item)) return i;
            return -1;
        }
    }
    public bool Remove(T item)
    {
        lock (Access.SyncRoot)
        {
            int index = IndexOf(item);
            if (index < 0) return false;
            RemoveAt(index);
            return true;
        }
    }
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        lock (Access.SyncRoot) _buffer.Read(0, array.AsSpan(arrayIndex, Count));
    }

    public T this[int index]
    {
        get { lock (Access.SyncRoot) { CheckIndex(index); return _buffer.ReadElement(index); } }
        set { lock (Access.SyncRoot) { CheckIndex(index); _buffer.WriteElement(index, value); } }
    }

    public PageList<T> Duplicate(PageAccess? access = null)
    {
        lock (Access.SyncRoot) return new(_buffer.Duplicate(access), _count);
    }
    public PageRange<T> Retain() { lock (Access.SyncRoot) return _buffer.Retain(0, Count); }
    public PageRange<T> Retain(int index, int? count = null)
    {
        lock (Access.SyncRoot)
        {
            int length = count ?? Count - index;
            if (index < 0 || length < 0 || index > Count || length > Count - index)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _buffer.Retain(index, length);
        }
    }
    public void Add(T value) => AddRange(MemoryMarshal.CreateReadOnlySpan(ref value, 1));

    public void AddRange(ReadOnlySpan<T> values)
    {
        lock (Access.SyncRoot)
        {
            int current = Count;
            int required = checked(current + values.Length);
            long capacity = _buffer.Length;
            if (required > capacity)
            {
                long growth = Math.Max(values.Length,
                    Math.Max(1, Math.Min(capacity, _buffer.Arena.MaximumPageSize / Unsafe.SizeOf<T>())));
                _buffer.Resize(checked(capacity + growth));
            }
            _buffer.Write(current, values);
            _count = required;
        }
    }

    public void Insert(int index, T value)
    {
        lock (Access.SyncRoot)
        {
            if (index == Count) { Add(value); return; }
            CheckIndex(index);
            if (_count == int.MaxValue) throw new InvalidOperationException("The collection cannot contain more elements.");
            _buffer.Insert(index, MemoryMarshal.CreateReadOnlySpan(ref value, 1));
            _count++;
        }
    }

    public void RemoveAt(int index)
    {
        lock (Access.SyncRoot)
        {
            CheckIndex(index);
            _buffer.Remove(index, 1);
            _count--;
        }
    }

    public void Write(int index, int count, PageWrite<T> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _buffer.Write(index, count, writer, () =>
        {
            if (index < 0 || count < 0 || index > _count || count > _count - index)
                throw new ArgumentOutOfRangeException(nameof(index));
        });
    }

    public T[] ToArray()
    {
        lock (Access.SyncRoot)
        {
            T[] values = new T[Count];
            _buffer.Read(0, values.AsSpan());
            return values;
        }
    }

    private void CheckIndex(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => _buffer.Dispose();

    public struct Enumerator : IEnumerator<T>
    {
        private readonly PageList<T> _values;
        private int _index;
        internal Enumerator(PageList<T> values) { _values = values; _index = -1; }
        public T Current => _values[_index];
        object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < _values.Count;
        public void Reset() => _index = -1;
        public void Dispose() { }
    }
}
