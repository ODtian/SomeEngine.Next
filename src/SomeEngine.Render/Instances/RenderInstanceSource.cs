using System.Collections.ObjectModel;

namespace SomeEngine.Render.Instances;

/// <summary>A logical contiguous instance range. It never denotes a physical storage row.</summary>
public readonly record struct RenderInstanceRange
{
    public RenderInstanceRange(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _ = checked(start + count);
        Start = start;
        Count = count;
    }

    public int Start { get; }

    public int Count { get; }

    public int End => checked(Start + Count);

    public bool IsEmpty => Count == 0;

    public static RenderInstanceRange Full(int count) => new(0, count);

    public RenderInstanceRange Union(RenderInstanceRange other)
    {
        if (IsEmpty)
            return other;
        if (other.IsEmpty)
            return this;
        int start = Math.Min(Start, other.Start);
        int end = Math.Max(End, other.End);
        return new RenderInstanceRange(start, checked(end - start));
    }
}

/// <summary>One property-local value change in logical instance coordinates.</summary>
public readonly record struct RenderInstancePropertyChange(
    RenderInstancePropertyKey Property,
    RenderInstanceRange Range);

/// <summary>
/// One property-local sparse value change in logical instance coordinates. Indices are sorted,
/// unique, and owned by the immutable change set.
/// </summary>
public readonly record struct RenderInstanceSparsePropertyChange(
    RenderInstancePropertyKey Property,
    ReadOnlyMemory<int> Indices);

/// <summary>
/// Changes visible to a source consumer since a previously observed revision. Structural changes
/// affect row membership/count. Value changes remain keyed by the same canonical property
/// identities used by layout linking; the instance system does not invent semantic channels.
/// </summary>
internal sealed class RenderInstanceChangeSet
{
    private static readonly ReadOnlyCollection<RenderInstancePropertyChange> s_emptyProperties =
        Array.AsReadOnly(Array.Empty<RenderInstancePropertyChange>());
    private static readonly ReadOnlyCollection<RenderInstanceSparsePropertyChange>
        s_emptySparseProperties =
            Array.AsReadOnly(Array.Empty<RenderInstanceSparsePropertyChange>());

    internal RenderInstanceChangeSet(
        bool structureChanged,
        RenderInstancePropertyChange[] properties,
        RenderInstanceSparsePropertyChange[] sparseProperties)
    {
        StructureChanged = structureChanged;
        Properties = properties.Length == 0
            ? s_emptyProperties
            : Array.AsReadOnly(properties);
        SparseProperties = sparseProperties.Length == 0
            ? s_emptySparseProperties
            : Array.AsReadOnly(sparseProperties);
    }

    public static RenderInstanceChangeSet None { get; } = new(false, [], []);

    public bool StructureChanged { get; }

    public IReadOnlyList<RenderInstancePropertyChange> Properties { get; }

    public IReadOnlyList<RenderInstanceSparsePropertyChange> SparseProperties { get; }

    public bool IsEmpty =>
        !StructureChanged && Properties.Count == 0 && SparseProperties.Count == 0;

    public bool TryGetRange(
        RenderInstancePropertyKey property,
        out RenderInstanceRange range)
    {
        if (!property.IsValid)
            throw new ArgumentException("The property key is uninitialized.", nameof(property));
        for (int index = 0; index < Properties.Count; index++)
        {
            RenderInstancePropertyChange change = Properties[index];
            if (change.Property == property)
            {
                range = change.Range;
                return true;
            }
        }
        range = default;
        return false;
    }

    public bool TryGetSparseIndices(
        RenderInstancePropertyKey property,
        out ReadOnlyMemory<int> indices)
    {
        if (!property.IsValid)
            throw new ArgumentException("The property key is uninitialized.", nameof(property));
        for (int index = 0; index < SparseProperties.Count; index++)
        {
            RenderInstanceSparsePropertyChange change = SparseProperties[index];
            if (change.Property == property)
            {
                indices = change.Indices;
                return true;
            }
        }
        indices = default;
        return false;
    }
}

/// <summary>
/// Logical instance data independent from mesh ownership, ECS, and rendering backend. Its exact
/// property contract is immutable. Implementations may own columns, stream pages, or generate
/// values procedurally; consumers observe only coherent snapshots.
/// </summary>
internal interface IRenderInstanceSource
{
    RenderInstancePropertyLayout Layout { get; }

    int Count { get; }

    int Capacity { get; }

    ulong Revision { get; }

    RenderInstanceSourceSnapshot Capture(ulong previousRevision = 0ul);
}

/// <summary>Coherent read lease over one source revision.</summary>
internal abstract class RenderInstanceSourceSnapshot : IDisposable
{
    protected RenderInstanceSourceSnapshot(
        RenderInstancePropertyLayout layout,
        int count,
        int capacity,
        ulong revision,
        RenderInstanceChangeSet changes)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (capacity < count)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        if (revision == 0ul)
            throw new ArgumentOutOfRangeException(nameof(revision));
        Changes = changes ?? throw new ArgumentNullException(nameof(changes));
        Count = count;
        Capacity = capacity;
        Revision = revision;
    }

    public RenderInstancePropertyLayout Layout { get; }

    public int Count { get; }

    public int Capacity { get; }

    public ulong Revision { get; }

    public RenderInstanceChangeSet Changes { get; }

    /// <summary>
    /// Writes one source range into a destination capability restricted to this exact layout.
    /// The source never receives an allocator, physical row, GPU buffer, mesh, or pipeline object.
    /// </summary>
    public void Write(int sourceStart, RenderInstanceWriteSlice destination)
    {
        if (!destination.IsValid)
            throw new ArgumentException("The destination write capability is uninitialized.", nameof(destination));
        foreach (RenderInstancePropertyDescriptor property in destination.Properties.Properties)
        {
            _ = Layout.RequireCompatible(property, nameof(destination));
        }
        if ((uint)sourceStart > (uint)Count
            || (uint)destination.Count > (uint)(Count - sourceStart))
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStart));
        }
        WriteCore(sourceStart, destination);
    }

    protected abstract void WriteCore(
        int sourceStart,
        RenderInstanceWriteSlice destination);

    public virtual void Dispose()
    {
    }
}

/// <summary>
/// Fills an arbitrary logical range using only the source's declared write capability. Property
/// meaning remains with the contributor that declared the canonical property contract.
/// </summary>
internal delegate void RenderInstanceSourceWriter(
    int sourceStart,
    RenderInstanceWriteSlice destination);

/// <summary>
/// General procedural/streaming source. Grids, particles, vegetation, imported arrays, and tools
/// are ordinary producers of this contract rather than renderer-recognized source kinds.
/// </summary>
internal sealed class RenderInstanceProceduralSource : IRenderInstanceSource
{
    private readonly object _gate = new();
    private readonly RenderInstanceChangeJournal _changes;
    private readonly RenderInstancePropertyLayout _layout;
    private State _state;

    public RenderInstanceProceduralSource(
        RenderInstancePropertyLayout layout,
        int count,
        RenderInstanceSourceWriter writer)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(writer);
        _changes = new RenderInstanceChangeJournal(layout);
        _state = new State(count, writer, Revision: 1ul);
    }

    public RenderInstancePropertyLayout Layout => _layout;

    public int Count => Read(static state => state.Count);

    public int Capacity => Count;

    public ulong Revision => Read(static state => state.Revision);

    public void SetData(int count, RenderInstanceSourceWriter writer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentNullException.ThrowIfNull(writer);
        lock (_gate)
        {
            ulong revision = NextRevision(_state.Revision);
            _state = new State(count, writer, revision);
            _changes.RecordStructure(revision, count);
        }
    }

    public void Invalidate<T>(
        ResolvedRenderInstanceProperty<T> property,
        RenderInstanceRange range)
        where T : unmanaged
    {
        _layout.Validate(property, nameof(property));
        lock (_gate)
        {
            ValidateRange(range, _state.Count);
            ulong revision = NextRevision(_state.Revision);
            _state = _state with { Revision = revision };
            _changes.RecordProperty(revision, property.Ordinal, range);
        }
    }

    public void Invalidate(
        RenderInstancePropertyLayout properties,
        RenderInstanceRange range)
    {
        ArgumentNullException.ThrowIfNull(properties);
        lock (_gate)
        {
            ValidateRange(range, _state.Count);
            foreach (RenderInstancePropertyDescriptor property in properties.Properties)
                _ = _layout.RequireCompatible(property, nameof(properties));
            ulong revision = NextRevision(_state.Revision);
            _state = _state with { Revision = revision };
            foreach (RenderInstancePropertyDescriptor property in properties.Properties)
            {
                RenderInstancePropertyDescriptor destination =
                    _layout.RequireCompatible(property, nameof(properties));
                _changes.RecordProperty(revision, destination.Ordinal, range);
            }
        }
    }

    public void InvalidateAll(RenderInstanceRange range)
    {
        lock (_gate)
        {
            ValidateRange(range, _state.Count);
            ulong revision = NextRevision(_state.Revision);
            _state = _state with { Revision = revision };
            _changes.RecordAllProperties(revision, range);
        }
    }

    public void InvalidateAll() => InvalidateAll(RenderInstanceRange.Full(Count));

    public RenderInstanceSourceSnapshot Capture(ulong previousRevision = 0ul)
    {
        lock (_gate)
        {
            State state = _state;
            return new ProceduralSnapshot(
                _layout,
                state,
                _changes.Collect(previousRevision, state.Revision, state.Count));
        }
    }

    private T Read<T>(Func<State, T> selector)
    {
        lock (_gate)
            return selector(_state);
    }

    private static void ValidateRange(RenderInstanceRange range, int count)
    {
        if ((uint)range.Start > (uint)count
            || (uint)range.Count > (uint)(count - range.Start))
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }
    }

    private static ulong NextRevision(ulong revision) => checked(revision + 1ul);

    private readonly record struct State(
        int Count,
        RenderInstanceSourceWriter Writer,
        ulong Revision);

    private sealed class ProceduralSnapshot : RenderInstanceSourceSnapshot
    {
        private readonly RenderInstanceSourceWriter _writer;

        internal ProceduralSnapshot(
            RenderInstancePropertyLayout layout,
            State state,
            RenderInstanceChangeSet changes)
            : base(layout, state.Count, state.Count, state.Revision, changes)
        {
            _writer = state.Writer;
        }

        protected override void WriteCore(
            int sourceStart,
            RenderInstanceWriteSlice destination) =>
            _writer(sourceStart, destination);
    }
}

internal sealed class RenderInstanceChangeJournal
{
    private const int MaximumRecords = 128;
    private const int AllProperties = -1;
    private const int Structure = -2;

    private readonly RenderInstancePropertyLayout _layout;
    private readonly Queue<Entry> _records = new();

    internal RenderInstanceChangeJournal(RenderInstancePropertyLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    internal void RecordProperty(
        ulong revision,
        int propertyOrdinal,
        RenderInstanceRange range)
    {
        if ((uint)propertyOrdinal >= (uint)_layout.Properties.Count)
            throw new ArgumentOutOfRangeException(nameof(propertyOrdinal));
        Enqueue(new Entry(revision, propertyOrdinal, range, null));
    }

    internal void RecordAllProperties(
        ulong revision,
        RenderInstanceRange range) =>
        Enqueue(new Entry(revision, AllProperties, range, null));

    internal void RecordStructure(ulong revision, int count) =>
        Enqueue(new Entry(revision, Structure, RenderInstanceRange.Full(count), null));

    internal void RecordSparse(
        ulong revision,
        int propertyOrdinal,
        ReadOnlySpan<int> indices)
    {
        if ((uint)propertyOrdinal >= (uint)_layout.Properties.Count)
            throw new ArgumentOutOfRangeException(nameof(propertyOrdinal));
        if (indices.IsEmpty)
            return;
        int[] values = indices.ToArray();
        Array.Sort(values);
        int uniqueCount = 1;
        for (int index = 1; index < values.Length; index++)
        {
            if (values[index] == values[uniqueCount - 1])
                continue;
            values[uniqueCount++] = values[index];
        }
        if (uniqueCount != values.Length)
            Array.Resize(ref values, uniqueCount);
        Enqueue(new Entry(revision, propertyOrdinal, default, values));
    }

    internal RenderInstanceChangeSet Collect(
        ulong previousRevision,
        ulong currentRevision,
        int currentCount)
    {
        if (previousRevision == currentRevision)
            return RenderInstanceChangeSet.None;
        if (previousRevision == 0ul
            || previousRevision > currentRevision
            || _records.Count == 0
            || previousRevision < _records.Peek().Revision - 1ul)
        {
            return Full(currentCount, structureChanged: true);
        }

        bool structureChanged = false;
        var ranges = new RenderInstanceRange[_layout.Properties.Count];
        var changed = new bool[ranges.Length];
        var sparse = new SortedSet<int>?[ranges.Length];
        foreach (Entry entry in _records)
        {
            if (entry.Revision <= previousRevision)
                continue;
            if (entry.Kind == Structure)
            {
                structureChanged = true;
                continue;
            }
            if (entry.Kind == AllProperties)
            {
                for (int ordinal = 0; ordinal < ranges.Length; ordinal++)
                {
                    ranges[ordinal] = ranges[ordinal].Union(entry.Range);
                    changed[ordinal] = true;
                }
                continue;
            }
            if (entry.SparseIndices is not null)
            {
                SortedSet<int> values = sparse[entry.Kind] ??= [];
                foreach (int instanceIndex in entry.SparseIndices)
                {
                    if ((uint)instanceIndex < (uint)currentCount)
                        values.Add(instanceIndex);
                }
                continue;
            }
            ranges[entry.Kind] = ranges[entry.Kind].Union(entry.Range);
            changed[entry.Kind] = true;
        }

        if (structureChanged)
            return Full(currentCount, structureChanged: true);

        var result = new List<RenderInstancePropertyChange>();
        var sparseResult = new List<RenderInstanceSparsePropertyChange>();
        for (int ordinal = 0; ordinal < changed.Length; ordinal++)
        {
            RenderInstanceRange range = changed[ordinal]
                ? Clamp(ranges[ordinal], currentCount)
                : default;
            if (changed[ordinal] && !range.IsEmpty)
            {
                result.Add(new RenderInstancePropertyChange(
                    _layout.Properties[ordinal].Key,
                    range));
            }

            SortedSet<int>? sparseIndices = sparse[ordinal];
            if (sparseIndices is null || sparseIndices.Count == 0)
                continue;
            if (!range.IsEmpty)
            {
                sparseIndices.RemoveWhere(index =>
                    index >= range.Start && index < range.End);
            }
            if (sparseIndices.Count != 0)
            {
                sparseResult.Add(new RenderInstanceSparsePropertyChange(
                    _layout.Properties[ordinal].Key,
                    sparseIndices.ToArray()));
            }
        }
        return result.Count == 0 && sparseResult.Count == 0
            ? RenderInstanceChangeSet.None
            : new RenderInstanceChangeSet(false, [.. result], [.. sparseResult]);
    }

    /// <summary>
    /// Re-labels all entries created by one externally atomic update with its single published
    /// revision. Callers hold the owning source write lock, so consumers can never observe the
    /// intermediate revision labels.
    /// </summary>
    internal void CollapseAfter(ulong previousRevision, ulong committedRevision)
    {
        if (committedRevision <= previousRevision)
            throw new ArgumentOutOfRangeException(nameof(committedRevision));
        if (_records.Count == 0)
            return;

        Entry[] entries = [.. _records];
        _records.Clear();
        for (int index = 0; index < entries.Length; index++)
        {
            Entry entry = entries[index];
            _records.Enqueue(entry.Revision > previousRevision
                ? entry with { Revision = committedRevision }
                : entry);
        }
    }

    private RenderInstanceChangeSet Full(int count, bool structureChanged)
    {
        if (count == 0)
            return new RenderInstanceChangeSet(structureChanged, [], []);
        RenderInstanceRange full = RenderInstanceRange.Full(count);
        var properties = new RenderInstancePropertyChange[_layout.Properties.Count];
        for (int ordinal = 0; ordinal < properties.Length; ordinal++)
        {
            properties[ordinal] = new RenderInstancePropertyChange(
                _layout.Properties[ordinal].Key,
                full);
        }
        return new RenderInstanceChangeSet(structureChanged, properties, []);
    }

    private void Enqueue(Entry entry)
    {
        _records.Enqueue(entry);
        while (_records.Count > MaximumRecords)
            _ = _records.Dequeue();
    }

    private static RenderInstanceRange Clamp(RenderInstanceRange range, int count)
    {
        int start = Math.Min(range.Start, count);
        int end = Math.Min(range.End, count);
        return new RenderInstanceRange(start, Math.Max(0, end - start));
    }

    private readonly record struct Entry(
        ulong Revision,
        int Kind,
        RenderInstanceRange Range,
        int[]? SparseIndices);
}
