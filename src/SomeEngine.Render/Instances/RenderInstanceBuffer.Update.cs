using System.Runtime.InteropServices;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Optimistic transaction over one <see cref="RenderInstanceBuffer"/> revision. Mutations are
/// staged in logical instance coordinates and become visible atomically when <see cref="Commit"/>
/// succeeds. The transaction never exposes physical rows, renderer bindings, or semantic slots.
/// </summary>
public sealed class RenderInstanceUpdate : IDisposable
{
    private RenderInstanceBuffer? _owner;
    private readonly ulong _baseRevision;
    private readonly List<RenderInstanceBufferedMutation> _mutations = [];
    private int _count;
    private bool _completed;

    internal RenderInstanceUpdate(
        RenderInstanceBuffer owner,
        ulong baseRevision,
        int count)
    {
        _owner = owner;
        _baseRevision = baseRevision;
        _count = count;
    }

    public int Count => RequireOpen().ValidateUpdateCount(_count);

    public ulong BaseRevision => _baseRevision;

    public bool IsCompleted => _completed;

    public int Add() => AddRange(1);

    public int AddRange(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _ = RequireOpen();
        int start = _count;
        if (count == 0)
            return start;
        int next = checked(start + count);
        _mutations.Add(RenderInstanceBufferedMutation.SetCount(next));
        _count = next;
        return start;
    }

    public void SetCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _ = RequireOpen();
        if (count == _count)
            return;
        _mutations.Add(RenderInstanceBufferedMutation.SetCount(count));
        _count = count;
    }

    public void Clear() => SetCount(0);

    /// <summary>
    /// Stages dense swap-back removal. The returned remap is expressed in the transaction's
    /// current logical coordinates and is valid only if the transaction commits successfully.
    /// </summary>
    public RenderInstanceRemoval RemoveAtSwapBack(int index)
    {
        _ = RequireOpen();
        ValidateIndex(index);
        int last = _count - 1;
        int movedFrom = index == last ? -1 : last;
        _mutations.Add(RenderInstanceBufferedMutation.RemoveAtSwapBack(index));
        _count--;
        return new RenderInstanceRemoval(index, movedFrom);
    }

    public void Set<T>(
        RenderInstanceProperty<T> property,
        int index,
        in T value)
        where T : unmanaged =>
        Set(RequireOpen().Layout.Resolve(property), index, in value);

    public void Set<T>(
        ResolvedRenderInstanceProperty<T> property,
        int index,
        in T value)
        where T : unmanaged
    {
        RenderInstanceBuffer owner = RequireOpen();
        owner.ValidateUpdateProperty(property);
        ValidateIndex(index);
        byte[] bytes = new byte[property.Encoding.ValueSize];
        MemoryMarshal.Write(bytes, in value);
        _mutations.Add(RenderInstanceBufferedMutation.Range(
            property.Ordinal,
            index,
            1,
            bytes));
    }

    public void WriteRange<T>(
        RenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged =>
        WriteRange(RequireOpen().Layout.Resolve(property), start, values);

    public void WriteRange<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        RenderInstanceBuffer owner = RequireOpen();
        owner.ValidateUpdateProperty(property);
        ValidateRange(start, values.Length);
        if (values.IsEmpty)
            return;
        _mutations.Add(RenderInstanceBufferedMutation.Range(
            property.Ordinal,
            start,
            values.Length,
            MemoryMarshal.AsBytes(values).ToArray()));
    }

    public void Fill<T>(
        RenderInstanceProperty<T> property,
        int start,
        int count,
        in T value)
        where T : unmanaged =>
        Fill(RequireOpen().Layout.Resolve(property), start, count, in value);

    public void Fill<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        int count,
        in T value)
        where T : unmanaged
    {
        RenderInstanceBuffer owner = RequireOpen();
        owner.ValidateUpdateProperty(property);
        ValidateRange(start, count);
        if (count == 0)
            return;
        byte[] bytes = new byte[property.Encoding.ValueSize];
        MemoryMarshal.Write(bytes, in value);
        _mutations.Add(RenderInstanceBufferedMutation.Fill(
            property.Ordinal,
            start,
            count,
            bytes));
    }

    public void WriteSparse<T>(
        RenderInstanceProperty<T> property,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<T> values)
        where T : unmanaged =>
        WriteSparse(RequireOpen().Layout.Resolve(property), indices, values);

    public void WriteSparse<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        RenderInstanceBuffer owner = RequireOpen();
        owner.ValidateUpdateProperty(property);
        if (indices.Length != values.Length)
        {
            throw new ArgumentException(
                "Sparse instance indices and values must have identical lengths.",
                nameof(values));
        }
        if (indices.IsEmpty)
            return;

        int[] copiedIndices = indices.ToArray();
        var unique = new HashSet<int>(copiedIndices.Length);
        for (int index = 0; index < copiedIndices.Length; index++)
        {
            int instanceIndex = copiedIndices[index];
            ValidateIndex(instanceIndex);
            if (!unique.Add(instanceIndex))
            {
                throw new ArgumentException(
                    $"Sparse instance index {instanceIndex} appears more than once.",
                    nameof(indices));
            }
        }

        _mutations.Add(RenderInstanceBufferedMutation.Sparse(
            property.Ordinal,
            copiedIndices,
            MemoryMarshal.AsBytes(values).ToArray()));
    }

    /// <summary>
    /// Publishes all staged structure and property changes as one revision. Commit fails rather
    /// than merging implicitly if another writer changed the source after this transaction began.
    /// </summary>
    public ulong Commit()
    {
        RenderInstanceBuffer owner = RequireOpen();
        ulong revision = owner.CommitUpdate(_baseRevision, _mutations);
        _completed = true;
        _owner = null;
        _mutations.Clear();
        return revision;
    }

    public void Dispose()
    {
        _owner = null;
        _mutations.Clear();
        _completed = true;
    }

    private RenderInstanceBuffer RequireOpen()
    {
        if (_completed)
            throw new ObjectDisposedException(nameof(RenderInstanceUpdate));
        return _owner ?? throw new ObjectDisposedException(nameof(RenderInstanceUpdate));
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
}

internal enum RenderInstanceBufferedMutationKind : byte
{
    SetCount,
    RemoveAtSwapBack,
    Range,
    Sparse,
    Fill,
}

internal sealed record RenderInstanceBufferedMutation(
    RenderInstanceBufferedMutationKind Kind,
    int PropertyOrdinal,
    int Start,
    int Count,
    int[]? Indices,
    byte[]? Data)
{
    internal static RenderInstanceBufferedMutation SetCount(int count) =>
        new(RenderInstanceBufferedMutationKind.SetCount, -1, 0, count, null, null);

    internal static RenderInstanceBufferedMutation RemoveAtSwapBack(int index) =>
        new(RenderInstanceBufferedMutationKind.RemoveAtSwapBack, -1, index, 1, null, null);

    internal static RenderInstanceBufferedMutation Range(
        int propertyOrdinal,
        int start,
        int count,
        byte[] data) =>
        new(RenderInstanceBufferedMutationKind.Range, propertyOrdinal, start, count, null, data);

    internal static RenderInstanceBufferedMutation Sparse(
        int propertyOrdinal,
        int[] indices,
        byte[] data) =>
        new(RenderInstanceBufferedMutationKind.Sparse, propertyOrdinal, 0, indices.Length, indices, data);

    internal static RenderInstanceBufferedMutation Fill(
        int propertyOrdinal,
        int start,
        int count,
        byte[] data) =>
        new(RenderInstanceBufferedMutationKind.Fill, propertyOrdinal, start, count, null, data);
}

public sealed partial class RenderInstanceBuffer
{
    /// <summary>Begins an atomic optimistic update against the current logical revision.</summary>
    public RenderInstanceUpdate BeginUpdate()
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new RenderInstanceUpdate(this, _revision, _count);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    internal int ValidateUpdateCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return count;
    }

    internal void ValidateUpdateProperty<T>(ResolvedRenderInstanceProperty<T> property)
        where T : unmanaged =>
        _layout.Validate(property, nameof(property));

    internal ulong CommitUpdate(
        ulong baseRevision,
        IReadOnlyList<RenderInstanceBufferedMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (_revision != baseRevision)
            {
                throw new InvalidOperationException(
                    $"Render-instance update started at revision {baseRevision}, but revision " +
                    $"{_revision} is now current.");
            }
            if (mutations.Count == 0)
                return _revision;

            bool structureChanged = false;
            bool anyChange = false;
            var ranges = new RenderInstanceRange[_columns.Length];
            var rangeChanged = new bool[_columns.Length];
            var sparse = new SortedSet<int>?[_columns.Length];

            for (int mutationIndex = 0; mutationIndex < mutations.Count; mutationIndex++)
            {
                RenderInstanceBufferedMutation mutation = mutations[mutationIndex];
                switch (mutation.Kind)
                {
                    case RenderInstanceBufferedMutationKind.SetCount:
                        if (mutation.Count == _count)
                            break;
                        ArgumentOutOfRangeException.ThrowIfNegative(mutation.Count);
                        EnsureCapacityCore(mutation.Count);
                        if (mutation.Count > _count)
                            ClearRangeCore(_count, mutation.Count - _count);
                        else
                            ClearRangeCore(mutation.Count, _count - mutation.Count);
                        _count = mutation.Count;
                        structureChanged = true;
                        anyChange = true;
                        break;

                    case RenderInstanceBufferedMutationKind.RemoveAtSwapBack:
                        ValidateIndex(mutation.Start);
                        int last = _count - 1;
                        if (mutation.Start != last)
                        {
                            for (int ordinal = 0; ordinal < _columns.Length; ordinal++)
                                _columns[ordinal].CopyElement(last, mutation.Start);
                        }
                        ClearRow(last);
                        _count--;
                        structureChanged = true;
                        anyChange = true;
                        break;

                    case RenderInstanceBufferedMutationKind.Range:
                    {
                        ValidateRange(mutation.Start, mutation.Count);
                        Column column = RequireMutationColumn(mutation);
                        byte[] data = RequireMutationData(mutation);
                        int expected = checked(mutation.Count * column.Property.Encoding.ValueSize);
                        if (data.Length != expected)
                            throw new InvalidOperationException("A staged range mutation has invalid encoded data.");
                        data.AsSpan().CopyTo(column.Range(mutation.Start, mutation.Count));
                        ranges[mutation.PropertyOrdinal] = ranges[mutation.PropertyOrdinal].Union(
                            new RenderInstanceRange(mutation.Start, mutation.Count));
                        rangeChanged[mutation.PropertyOrdinal] = true;
                        anyChange = mutation.Count != 0 || anyChange;
                        break;
                    }

                    case RenderInstanceBufferedMutationKind.Sparse:
                    {
                        Column column = RequireMutationColumn(mutation);
                        byte[] data = RequireMutationData(mutation);
                        int[] indices = mutation.Indices
                            ?? throw new InvalidOperationException("A sparse mutation has no indices.");
                        int valueSize = column.Property.Encoding.ValueSize;
                        if (data.Length != checked(indices.Length * valueSize))
                            throw new InvalidOperationException("A staged sparse mutation has invalid encoded data.");
                        SortedSet<int> changedIndices =
                            sparse[mutation.PropertyOrdinal] ??= [];
                        for (int index = 0; index < indices.Length; index++)
                        {
                            int instanceIndex = indices[index];
                            ValidateIndex(instanceIndex);
                            data.AsSpan(index * valueSize, valueSize)
                                .CopyTo(column.Element(instanceIndex));
                            changedIndices.Add(instanceIndex);
                        }
                        anyChange = indices.Length != 0 || anyChange;
                        break;
                    }

                    case RenderInstanceBufferedMutationKind.Fill:
                    {
                        ValidateRange(mutation.Start, mutation.Count);
                        Column column = RequireMutationColumn(mutation);
                        byte[] data = RequireMutationData(mutation);
                        if (data.Length != column.Property.Encoding.ValueSize)
                            throw new InvalidOperationException("A staged fill mutation has invalid encoded data.");
                        for (int index = 0; index < mutation.Count; index++)
                            data.AsSpan().CopyTo(column.Element(mutation.Start + index));
                        ranges[mutation.PropertyOrdinal] = ranges[mutation.PropertyOrdinal].Union(
                            new RenderInstanceRange(mutation.Start, mutation.Count));
                        rangeChanged[mutation.PropertyOrdinal] = true;
                        anyChange = mutation.Count != 0 || anyChange;
                        break;
                    }

                    default:
                        throw new ArgumentOutOfRangeException(nameof(mutation.Kind));
                }
            }

            if (!anyChange)
                return _revision;

            ulong revision = checked(_revision + 1ul);
            _revision = revision;
            if (structureChanged)
            {
                _changes.RecordStructure(revision, _count);
                return revision;
            }

            for (int ordinal = 0; ordinal < _columns.Length; ordinal++)
            {
                if (rangeChanged[ordinal] && !ranges[ordinal].IsEmpty)
                    _changes.RecordProperty(revision, ordinal, ranges[ordinal]);
                SortedSet<int>? indices = sparse[ordinal];
                if (indices is not null && indices.Count != 0)
                    _changes.RecordSparse(revision, ordinal, indices.ToArray());
            }
            return revision;
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    private Column RequireMutationColumn(RenderInstanceBufferedMutation mutation)
    {
        if ((uint)mutation.PropertyOrdinal >= (uint)_columns.Length)
            throw new InvalidOperationException("A staged mutation references an invalid property ordinal.");
        return _columns[mutation.PropertyOrdinal];
    }

    private static byte[] RequireMutationData(RenderInstanceBufferedMutation mutation) =>
        mutation.Data ?? throw new InvalidOperationException("A staged mutation has no encoded data.");
}
