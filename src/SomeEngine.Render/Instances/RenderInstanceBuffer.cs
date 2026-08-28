using System.Runtime.InteropServices;

namespace SomeEngine.Render.Instances;

/// <summary>The result of swap-back removal from a dense instance buffer.</summary>
public readonly record struct RenderInstanceRemoval(int RemovedIndex, int MovedFromIndex)
{
    public bool Moved => MovedFromIndex >= 0;
}

/// <summary>
/// Editable, layout-driven instance memory. The buffer owns one tightly packed CPU column per
/// canonical render-instance property and deliberately has no built-in color, custom-data, mesh,
/// material, visibility, or renderer semantics. Contributors declare those properties through the
/// same typed contracts used by shader linking.
/// </summary>
public sealed partial class RenderInstanceBuffer : IRenderInstanceSource, IDisposable
{
    private readonly ReaderWriterLockSlim _gate = new(LockRecursionPolicy.NoRecursion);
    private readonly RenderInstancePropertyLayout _layout;
    private readonly RenderInstanceChangeJournal _changes;
    private readonly Column[] _columns;
    private int _capacity;
    private int _count;
    private ulong _revision = 1ul;
    private bool _disposed;

    public RenderInstanceBuffer(
        RenderInstancePropertyLayout layout,
        int capacity = 0)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (layout.Properties.Count == 0)
            throw new ArgumentException("An editable instance buffer requires at least one property.", nameof(layout));

        _columns = new Column[layout.Properties.Count];
        for (int ordinal = 0; ordinal < _columns.Length; ordinal++)
        {
            RenderInstancePropertyDescriptor property = layout.Properties[ordinal];
            if (!property.Encoding.HasManagedStorage)
            {
                throw new ArgumentException(
                    $"Property '{property.Key}' owns custom metadata and cannot be stored in the generic linear buffer.",
                    nameof(layout));
            }
            _columns[ordinal] = new Column(property, capacity);
        }
        _capacity = capacity;
        _changes = new RenderInstanceChangeJournal(layout);
    }

    public RenderInstancePropertyLayout Layout => _layout;

    public int Count => Read(static buffer => buffer._count);

    public int Capacity => Read(static buffer => buffer._capacity);

    public ulong Revision => Read(static buffer => buffer._revision);

    /// <summary>Adds one zero-initialized logical row and returns its dense index.</summary>
    public int Add()
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            int index = _count;
            EnsureCapacityCore(checked(index + 1));
            ClearRow(index);
            _count++;
            RecordStructure();
            return index;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>Adds zero-initialized rows and returns the first dense index.</summary>
    public int AddRange(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            int start = _count;
            if (count == 0)
                return start;
            EnsureCapacityCore(checked(start + count));
            ClearRangeCore(start, count);
            _count += count;
            RecordStructure();
            return start;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Removes a dense row by moving the last row into its place. The returned move record lets a
    /// caller repair external dense-index tables without pretending indices are stable handles.
    /// </summary>
    public RenderInstanceRemoval RemoveAtSwapBack(int index)
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            ValidateIndex(index);
            int last = _count - 1;
            int movedFrom = index == last ? -1 : last;
            if (movedFrom >= 0)
            {
                for (int ordinal = 0; ordinal < _columns.Length; ordinal++)
                    _columns[ordinal].CopyElement(last, index);
            }
            ClearRow(last);
            _count--;
            RecordStructure();
            return new RenderInstanceRemoval(index, movedFrom);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Sets the active logical row count. Newly exposed rows are byte-zeroed; semantic defaults
    /// belong to the contributor that declared each property, not to generic instance storage.
    /// </summary>
    public void SetCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (count == _count)
                return;
            EnsureCapacityCore(count);
            if (count > _count)
                ClearRangeCore(_count, count - _count);
            else
                ClearRangeCore(count, _count - count);
            _count = count;
            RecordStructure();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void EnsureCapacity(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCapacity);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            EnsureCapacityCore(minimumCapacity);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Resize(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (capacity < _count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(capacity),
                    capacity,
                    "Capacity cannot be smaller than the logical row count.");
            }
            ResizeCore(capacity);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Clear()
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (_count == 0)
                return;
            ClearRangeCore(0, _count);
            _count = 0;
            RecordStructure();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Set<T>(
        RenderInstanceProperty<T> property,
        int index,
        in T value)
        where T : unmanaged =>
        Set(_layout.Resolve(property), index, in value);

    public void Set<T>(
        ResolvedRenderInstanceProperty<T> property,
        int index,
        in T value)
        where T : unmanaged
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateIndex(index);
            MemoryMarshal.Write(column.Element(index), in value);
            RecordProperty(property.Ordinal, new RenderInstanceRange(index, 1));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public T Get<T>(RenderInstanceProperty<T> property, int index)
        where T : unmanaged =>
        Get(_layout.Resolve(property), index);

    public T Get<T>(ResolvedRenderInstanceProperty<T> property, int index)
        where T : unmanaged
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateIndex(index);
            return MemoryMarshal.Read<T>(column.Element(index));
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void SetRange<T>(
        RenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged =>
        SetRange(_layout.Resolve(property), start, values);

    public void SetRange<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateRange(start, values.Length);
            MemoryMarshal.AsBytes(values).CopyTo(column.Range(start, values.Length));
            if (!values.IsEmpty)
                RecordProperty(property.Ordinal, new RenderInstanceRange(start, values.Length));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public void Copy<T>(
        RenderInstanceProperty<T> property,
        int start,
        Span<T> destination)
        where T : unmanaged =>
        Copy(_layout.Resolve(property), start, destination);

    public void Copy<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        Span<T> destination)
        where T : unmanaged
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateRange(start, destination.Length);
            column.Range(start, destination.Length).CopyTo(MemoryMarshal.AsBytes(destination));
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void Fill<T>(
        RenderInstanceProperty<T> property,
        int start,
        int count,
        in T value)
        where T : unmanaged =>
        Fill(_layout.Resolve(property), start, count, in value);

    public void Fill<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        int count,
        in T value)
        where T : unmanaged
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateRange(start, count);
            for (int index = 0; index < count; index++)
                MemoryMarshal.Write(column.Element(start + index), in value);
            if (count != 0)
                RecordProperty(property.Ordinal, new RenderInstanceRange(start, count));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    /// <summary>
    /// Maps one typed property range for direct editing. Disposing the scope publishes exactly one
    /// property-local revision and releases the write lock.
    /// </summary>
    public RenderInstanceColumnWriteScope<T> BeginWrite<T>(
        RenderInstanceProperty<T> property,
        int start,
        int count)
        where T : unmanaged =>
        BeginWrite(_layout.Resolve(property), start, count);

    public RenderInstanceColumnWriteScope<T> BeginWrite<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        int count)
        where T : unmanaged
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Column column = RequireColumn(property);
            ValidateRange(start, count);
            Span<T> values = MemoryMarshal.Cast<byte, T>(column.Range(start, count));
            return new RenderInstanceColumnWriteScope<T>(
                this,
                property.Ordinal,
                start,
                values);
        }
        catch
        {
            _gate.ExitWriteLock();
            throw;
        }
    }

    public void Invalidate<T>(
        RenderInstanceProperty<T> property,
        RenderInstanceRange range)
        where T : unmanaged =>
        Invalidate(_layout.Resolve(property), range);

    public void Invalidate<T>(
        ResolvedRenderInstanceProperty<T> property,
        RenderInstanceRange range)
        where T : unmanaged
    {
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            _ = RequireColumn(property);
            ValidateRange(range.Start, range.Count);
            if (!range.IsEmpty)
                RecordProperty(property.Ordinal, range);
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    public RenderInstanceSourceSnapshot Capture(ulong previousRevision = 0ul)
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new BufferSnapshot(
                this,
                _changes.Collect(previousRevision, _revision, _count));
        }
        catch
        {
            _gate.ExitReadLock();
            throw;
        }
    }

    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (Column column in _columns)
                column.ClearStorage();
            _capacity = 0;
            _count = 0;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    internal Span<T> ColumnSpan<T>(int ordinal, int start, int count)
        where T : unmanaged =>
        MemoryMarshal.Cast<byte, T>(_columns[ordinal].Range(start, count));

    internal void CompleteMappedWrite(int ordinal, int start, int count)
    {
        try
        {
            if (count != 0)
                RecordProperty(ordinal, new RenderInstanceRange(start, count));
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private T Read<T>(Func<RenderInstanceBuffer, T> selector)
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return selector(this);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    private Column RequireColumn<T>(
        ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged
    {
        _layout.Validate(property, nameof(property));
        return _columns[property.Ordinal];
    }

    private void EnsureCapacityCore(int minimumCapacity)
    {
        if (minimumCapacity <= _capacity)
            return;
        int grown = _capacity == 0 ? 4 : _capacity;
        while (grown < minimumCapacity)
            grown = checked(grown + Math.Max(4, grown / 2));
        ResizeCore(grown);
    }

    private void ResizeCore(int capacity)
    {
        if (capacity == _capacity)
            return;
        foreach (Column column in _columns)
            column.Resize(capacity);
        _capacity = capacity;
    }

    private void ClearRow(int index)
    {
        foreach (Column column in _columns)
            column.Element(index).Clear();
    }

    private void ClearRangeCore(int start, int count)
    {
        if (count == 0)
            return;
        foreach (Column column in _columns)
            column.Range(start, count).Clear();
    }

    private void ValidateIndex(int index)
    {
        if ((uint)index >= (uint)_count)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private void ValidateRange(int start, int count)
    {
        if ((uint)start > (uint)_count || (uint)count > (uint)(_count - start))
            throw new ArgumentOutOfRangeException(nameof(start));
    }

    private void RecordProperty(int ordinal, RenderInstanceRange range)
    {
        _revision = checked(_revision + 1ul);
        _changes.RecordProperty(_revision, ordinal, range);
    }

    private void RecordStructure()
    {
        _revision = checked(_revision + 1ul);
        _changes.RecordStructure(_revision, _count);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Column
    {
        private byte[] _data;

        internal Column(RenderInstancePropertyDescriptor property, int capacity)
        {
            Property = property;
            _data = new byte[checked(capacity * property.Encoding.ValueSize)];
        }

        internal RenderInstancePropertyDescriptor Property { get; }

        internal Span<byte> Element(int index) =>
            _data.AsSpan(
                checked(index * Property.Encoding.ValueSize),
                Property.Encoding.ValueSize);

        internal Span<byte> Range(int start, int count) =>
            _data.AsSpan(
                checked(start * Property.Encoding.ValueSize),
                checked(count * Property.Encoding.ValueSize));

        internal void CopyElement(int source, int destination) =>
            Element(source).CopyTo(Element(destination));

        internal void Resize(int capacity) =>
            Array.Resize(ref _data, checked(capacity * Property.Encoding.ValueSize));

        internal void ClearStorage() => _data = [];
    }

    private sealed class BufferSnapshot : RenderInstanceSourceSnapshot
    {
        private RenderInstanceBuffer? _owner;

        internal BufferSnapshot(
            RenderInstanceBuffer owner,
            RenderInstanceChangeSet changes)
            : base(owner._layout, owner._count, owner._capacity, owner._revision, changes)
        {
            _owner = owner;
        }

        protected override void WriteCore(
            int sourceStart,
            RenderInstanceWriteSlice destination)
        {
            RenderInstanceBuffer owner = RequireOwner();
            foreach (RenderInstancePropertyDescriptor requested in
                     destination.Properties.Properties)
            {
                RenderInstancePropertyDescriptor sourceProperty =
                    owner._layout.RequireCompatible(requested, nameof(destination));
                Column column = owner._columns[sourceProperty.Ordinal];
                destination.WriteEncoded(
                    sourceProperty,
                    column.Range(sourceStart, destination.Count));
            }
        }

        public override void Dispose()
        {
            RenderInstanceBuffer? owner = Interlocked.Exchange(ref _owner, null);
            owner?._gate.ExitReadLock();
        }

        private RenderInstanceBuffer RequireOwner() =>
            Volatile.Read(ref _owner)
            ?? throw new ObjectDisposedException(nameof(BufferSnapshot));
    }
}

/// <summary>Stack-only typed edit capability for one property-local range.</summary>
public ref struct RenderInstanceColumnWriteScope<T>
    where T : unmanaged
{
    private RenderInstanceBuffer? _owner;
    private readonly int _ordinal;
    private readonly int _start;

    internal RenderInstanceColumnWriteScope(
        RenderInstanceBuffer owner,
        int ordinal,
        int start,
        Span<T> values)
    {
        _owner = owner;
        _ordinal = ordinal;
        _start = start;
        Values = values;
    }

    public Span<T> Values { get; }

    public void Dispose()
    {
        RenderInstanceBuffer? owner = _owner;
        _owner = null;
        owner?.CompleteMappedWrite(_ordinal, _start, Values.Length);
    }
}
