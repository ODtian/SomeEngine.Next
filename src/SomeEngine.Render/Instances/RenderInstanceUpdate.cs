using System.Runtime.InteropServices;

namespace SomeEngine.Render.Instances;

/// <summary>
/// Stages a coherent update to one <see cref="RenderInstanceBuffer"/>. Values are copied into
/// transaction-owned memory when written; the buffer remains unchanged until <see cref="Commit"/>.
/// A commit holds the buffer write capability for the complete multi-property operation, so
/// snapshots can observe either the old revision or the complete new revision, never an
/// intermediate property state.
/// </summary>
public sealed class RenderInstanceUpdate : IDisposable
{
    private readonly RenderInstanceBuffer _buffer;
    private readonly List<ICommand> _commands = [];
    private int? _count;
    private bool _disposed;

    internal RenderInstanceUpdate(RenderInstanceBuffer buffer)
        => _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));

    /// <summary>Sets the dense logical row count published by this transaction.</summary>
    public void SetCount(int count)
    {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _count = count;
    }

    /// <summary>Stages one property value at one dense instance index.</summary>
    public void Write<T>(
        ResolvedRenderInstanceProperty<T> property,
        int index,
        in T value)
        where T : unmanaged
    {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        _buffer.Layout.Validate(property, nameof(property));
        _commands.Add(new RangeCommand<T>(property, index, [value]));
    }

    /// <summary>Stages one contiguous property range.</summary>
    public void WriteRange<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        ThrowIfClosed();
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        _buffer.Layout.Validate(property, nameof(property));
        if (values.IsEmpty)
            return;
        _commands.Add(new RangeCommand<T>(property, start, values.ToArray()));
    }

    /// <summary>
    /// Stages an arbitrary set of dense indices. Input order is irrelevant; duplicate indices are
    /// rejected so the transaction has one deterministic value for every property row.
    /// </summary>
    public void WriteSparse<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        ThrowIfClosed();
        _buffer.Layout.Validate(property, nameof(property));
        if (indices.Length != values.Length)
            throw new ArgumentException("Sparse instance indices and values must have identical lengths.");
        if (indices.IsEmpty)
            return;

        var pairs = new SparseValue<T>[indices.Length];
        for (int index = 0; index < pairs.Length; index++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(indices[index]);
            pairs[index] = new SparseValue<T>(indices[index], values[index]);
        }
        Array.Sort(pairs, static (left, right) => left.Index.CompareTo(right.Index));
        for (int index = 1; index < pairs.Length; index++)
        {
            if (pairs[index - 1].Index == pairs[index].Index)
            {
                throw new ArgumentException(
                    $"Sparse instance index {pairs[index].Index} appears more than once.",
                    nameof(indices));
            }
        }
        _commands.Add(new SparseCommand<T>(property, pairs));
    }

    /// <summary>Stages one value for every row in a contiguous property range.</summary>
    public void Fill<T>(
        ResolvedRenderInstanceProperty<T> property,
        RenderInstanceRange range,
        in T value)
        where T : unmanaged
    {
        ThrowIfClosed();
        _buffer.Layout.Validate(property, nameof(property));
        if (range.IsEmpty)
            return;
        var values = new T[range.Count];
        values.AsSpan().Fill(value);
        _commands.Add(new RangeCommand<T>(property, range.Start, values));
    }

    /// <summary>Atomically publishes all staged row-count and property changes.</summary>
    public void Commit()
    {
        ThrowIfClosed();
        _buffer.CommitUpdate(_count, _commands);
        _disposed = true;
        _commands.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _commands.Clear();
        _disposed = true;
    }

    internal interface ICommand
    {
        void Validate(int count);

        void Apply(RenderInstanceBuffer buffer);
    }

    private sealed class RangeCommand<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        T[] values) : ICommand
        where T : unmanaged
    {
        public void Validate(int count)
        {
            if ((uint)start > (uint)count || values.Length > count - start)
                throw new ArgumentOutOfRangeException(nameof(start));
        }

        public void Apply(RenderInstanceBuffer buffer) =>
            buffer.ApplyRange(property, start, values);
    }

    private sealed class SparseCommand<T> : ICommand
        where T : unmanaged
    {
        private readonly ResolvedRenderInstanceProperty<T> _property;
        private readonly int[] _indices;
        private readonly T[] _values;
        private readonly int _lastIndex;

        internal SparseCommand(
            ResolvedRenderInstanceProperty<T> property,
            SparseValue<T>[] values)
        {
            _property = property;
            _lastIndex = values[^1].Index;
            _indices = new int[values.Length];
            _values = new T[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                _indices[index] = values[index].Index;
                _values[index] = values[index].Value;
            }
        }

        public void Validate(int count)
        {
            if (_lastIndex >= count)
                throw new ArgumentOutOfRangeException(nameof(count));
        }

        public void Apply(RenderInstanceBuffer buffer) =>
            buffer.ApplySparse(_property, _indices, _values);
    }

    private readonly record struct SparseValue<T>(int Index, T Value)
        where T : unmanaged;

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

public sealed partial class RenderInstanceBuffer
{
    /// <summary>Begins a staged multi-property update.</summary>
    public RenderInstanceUpdate BeginUpdate()
    {
        _gate.EnterReadLock();
        try
        {
            ThrowIfDisposed();
            return new RenderInstanceUpdate(this);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    internal void CommitUpdate(
        int? requestedCount,
        IReadOnlyList<RenderInstanceUpdate.ICommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _gate.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            int targetCount = requestedCount ?? _count;
            ArgumentOutOfRangeException.ThrowIfNegative(targetCount);
            for (int index = 0; index < commands.Count; index++)
                commands[index].Validate(targetCount);

            ulong revisionBeforeCommit = _revision;
            if (targetCount != _count)
            {
                EnsureCapacityCore(targetCount);
                if (targetCount > _count)
                {
                    for (int index = _count; index < targetCount; index++)
                        ClearRow(index);
                }
                _count = targetCount;
                RecordStructure();
            }

            for (int index = 0; index < commands.Count; index++)
                commands[index].Apply(this);

            if (_revision != revisionBeforeCommit)
            {
                ulong committedRevision = checked(revisionBeforeCommit + 1ul);
                _changes.CollapseAfter(revisionBeforeCommit, committedRevision);
                _revision = committedRevision;
            }
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }

    internal void ApplyRange<T>(
        ResolvedRenderInstanceProperty<T> property,
        int start,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        Column column = RequireColumn(property);
        MemoryMarshal.AsBytes(values).CopyTo(column.Range(start, values.Length));
        if (!values.IsEmpty)
            RecordProperty(property.Ordinal, new RenderInstanceRange(start, values.Length));
    }

    internal void ApplySparse<T>(
        ResolvedRenderInstanceProperty<T> property,
        ReadOnlySpan<int> indices,
        ReadOnlySpan<T> values)
        where T : unmanaged
    {
        Column column = RequireColumn(property);
        for (int index = 0; index < indices.Length; index++)
            MemoryMarshal.Write(column.Element(indices[index]), in values[index]);
        if (indices.IsEmpty)
            return;
        _revision = checked(_revision + 1ul);
        _changes.RecordSparse(_revision, property.Ordinal, indices);
    }
}
