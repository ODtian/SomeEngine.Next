using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Archetype 内的固定容量列存储块。约 16KB 逻辑大小。
/// Entity 以紧凑方式存储，删除时通过 swap-remove 保持无空洞。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.3, §4.4
/// - entities 是特殊列（固定存在，不通过组件列索引）
/// - columns[i] 是类型擦除的 object（T[]），通过注册表操作函数还原强类型
/// </remarks>
public class Chunk
{
    private static long s_nextOwnershipIdentity;
    private static long s_nextPersistentIdentity;
    private static long s_nextStorageIdentity;

    private readonly long _ownershipIdentity;
    private readonly long _persistentIdentity;
    private ChunkStorage _storage;
    private long _bufferOverflowDetachCount;

    /// <summary>每行的 Entity。</summary>
    internal Span<Entity> Entities => Volatile.Read(ref _storage).Entities;

    internal int ColumnCount => Volatile.Read(ref _storage).Columns.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private object ColumnArray(int column) =>
        Volatile.Read(ref _storage).Columns[column];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> ComponentRows<T>(int column) where T : struct =>
        Unsafe.As<T[]>(Volatile.Read(ref _storage).Columns[column]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe ref byte ComponentRowReference(
        int column,
        int row,
        in ComponentOperations operations) =>
        ref operations.GetReference(ColumnArray(column), row);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal unsafe void CopyComponentTo(
        int sourceColumn,
        int sourceRow,
        Chunk destination,
        int destinationColumn,
        int destinationRow,
        in ComponentOperations operations)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.EnsureWritable();
        ref byte source = ref operations.GetReference(ColumnArray(sourceColumn), sourceRow);
        ref byte target = ref operations.GetReference(
            destination.ColumnArray(destinationColumn),
            destinationRow);
        operations.CopyValue(ref source, ref target);
    }

    /// <summary>
    /// Materializes one erased component value as an independently owned one-row snapshot. This is
    /// an explicit rollback/hook boundary, not a borrow of the chunk column.
    /// </summary>
    internal unsafe Array CaptureComponentValue(
        int column,
        int row,
        in ComponentOperations operations)
    {
        object snapshot = operations.CreateArray(1);
        ref byte source = ref operations.GetReference(ColumnArray(column), row);
        ref byte target = ref operations.GetReference(snapshot, 0);
        operations.CopyValue(ref source, ref target);
        return (Array)snapshot;
    }

    internal void CopyColumnPrefixTo(int column, Chunk destination, int count)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.EnsureWritable();
        Array.Copy(
            (Array)ColumnArray(column),
            (Array)destination.ColumnArray(column),
            count);
    }

    internal bool SharesColumnBackingWith(Chunk other, int column)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(ColumnArray(column), other.ColumnArray(column));
    }

    /// <summary>当前已分配的行数。</summary>
    internal int Count;

    /// <summary>最大行数。</summary>
    internal readonly int Capacity;

    /// <summary>Per-column change version tick.</summary>
    internal Span<uint> ChangeVersions => Volatile.Read(ref _storage).ChangeVersions;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<uint> AddVersionRows(int column) =>
        Volatile.Read(ref _storage).AddVersions[column];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<uint> WriteVersionRows(int column) =>
        Volatile.Read(ref _storage).WriteVersions[column];

    /// <summary>每个 enableable 列对应一个 128-bit mask。</summary>
    internal Span<UInt128> EnableMasks =>
        Volatile.Read(ref _storage).EnableMasks is { } masks
            ? masks
            : Span<UInt128>.Empty;

    /// <summary>行序变更版本（swap-remove 时递增）。</summary>
    internal uint OrderVersion;

    /// <summary>该 Chunk 在 Archetype.Chunks 列表中的索引。由 AllocateInChunk 设置，TryRecycleChunk swap 时更新。</summary>
    internal int IndexInArchetype;

    /// <summary>
    /// Chunk 级 SharedComponent 索引。parallel 到 Archetype.SharedComponentIds。
    /// null = archetype 不含 SharedComponent。
    /// </summary>
    internal SharedComponentTuple? SharedValues { get; }

    /// <summary>
    /// 构造 Chunk。
    /// </summary>
    /// <param name="capacity">每 chunk 的 entity 容量。</param>
    /// <param name="columnOperations">每列的类型擦除存储操作。</param>
    internal Chunk(
        int capacity,
        ReadOnlySpan<ComponentOperations> columnOperations,
        int enableableColumnCount = 0,
        SharedComponentTuple? sharedValues = null)
    {
        _ownershipIdentity = Interlocked.Increment(ref s_nextOwnershipIdentity);
        _persistentIdentity = Interlocked.Increment(ref s_nextPersistentIdentity);
        Capacity = capacity;
        Count = 0;
        SharedValues = sharedValues;
        var columns = new object[columnOperations.Length];
        var addVersions = new uint[columnOperations.Length][];
        var writeVersions = new uint[columnOperations.Length][];

        for (int i = 0; i < columnOperations.Length; i++)
        {
            unsafe
            {
                columns[i] = columnOperations[i].CreateArray(capacity);
            }

            addVersions[i] = new uint[capacity];
            writeVersions[i] = new uint[capacity];
        }

        _storage = new ChunkStorage(
            Interlocked.Increment(ref s_nextStorageIdentity),
            _ownershipIdentity,
            version: 0,
            new Entity[capacity],
            columns,
            new uint[columnOperations.Length],
            addVersions,
            writeVersions,
            enableableColumnCount > 0 ? new UInt128[enableableColumnCount] : null);
    }

    private Chunk(Chunk source)
    {
        _ownershipIdentity = Interlocked.Increment(ref s_nextOwnershipIdentity);
        _persistentIdentity = source._persistentIdentity;
        _storage = Volatile.Read(ref source._storage);
        Capacity = source.Capacity;
        Count = source.Count;
        OrderVersion = source.OrderVersion;
        IndexInArchetype = source.IndexInArchetype;
        SharedValues = source.SharedValues;
    }

    internal long PersistentIdentity => _persistentIdentity;

    internal long StorageIdentity => Volatile.Read(ref _storage).Identity;

    internal long StorageVersion => Volatile.Read(ref _storage).Version;

    /// <summary>
    /// Number of inherited per-row buffer overflow backings detached by this chunk shell.
    /// Ordinary chunk COW and newly allocated/grown private arrays do not increment it.
    /// </summary>
    internal long BufferOverflowDetachCount => Volatile.Read(ref _bufferOverflowDetachCount);

    internal bool OwnsStorage =>
        Volatile.Read(ref _storage).OwnerIdentity == _ownershipIdentity;

    internal bool SharesStorageWith(Chunk other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return ReferenceEquals(
            Volatile.Read(ref _storage),
            Volatile.Read(ref other._storage));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool OwnsBufferOverflow<T>(in DynamicBufferHeader<T> header)
        where T : struct, IBufferElement =>
        !header.HasOverflow || header.OverflowOwnerIdentity == _ownershipIdentity;

    /// <summary>
    /// Returns the row header after making an inherited overflow backing private to this chunk.
    /// The chunk backing is detached first; only the requested row's array is copied.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref DynamicBufferHeader<T> GetBufferHeaderWithWritableOverflow<T>(
        int headerColumn,
        int row)
        where T : struct, IBufferElement
    {
        EnsureWritable();
        ref DynamicBufferHeader<T> header =
            ref ComponentRows<DynamicBufferHeader<T>>(headerColumn)[row];
        if (!header.HasOverflow || header.OverflowOwnerIdentity == _ownershipIdentity)
            return ref header;

        _ = EnsureOwnedBufferOverflow(ref header);
        return ref header;
    }

    /// <summary>
    /// Makes one inherited overflow backing writable while retaining its capacity. Only the live
    /// prefix is copied; inactive capacity is intentionally left at its default value.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> EnsureOwnedBufferOverflow<T>(ref DynamicBufferHeader<T> header)
        where T : struct, IBufferElement
    {
        if (!OwnsStorage)
        {
            throw new InvalidOperationException(
                "Chunk storage must be detached before an overflow header can become writable.");
        }

        ReadOnlySpan<T> overflow = header.OverflowReadSpan;
        if (overflow.IsEmpty)
            throw new InvalidOperationException("Cannot detach a missing buffer overflow backing.");
        if (header.OverflowOwnerIdentity == _ownershipIdentity)
            return header.OverflowWriteSpan;

        if ((uint)header.Count > (uint)overflow.Length)
        {
            throw new InvalidOperationException(
                "Buffer count exceeds its overflow backing capacity.");
        }

        var detached = new T[overflow.Length];
        overflow[..header.Count].CopyTo(detached);
        SetOwnedBufferOverflow(ref header, detached);
        Interlocked.Increment(ref _bufferOverflowDetachCount);
        return header.OverflowWriteSpan;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetOwnedBufferOverflow<T>(
        ref DynamicBufferHeader<T> header,
        T[]? ownedOverflow)
        where T : struct, IBufferElement
    {
        header.SetOwnedOverflow(ownedOverflow, _ownershipIdentity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordInheritedBufferOverflowReplacement<T>(
        in DynamicBufferHeader<T> header)
        where T : struct, IBufferElement
    {
        if (header.HasOverflow && header.OverflowOwnerIdentity != _ownershipIdentity)
            Interlocked.Increment(ref _bufferOverflowDetachCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal object? BufferOverflowBackingIdentity<T>(int headerColumn, int row)
        where T : struct, IBufferElement =>
        ReadComponent<DynamicBufferHeader<T>>(headerColumn, row).OverflowBackingIdentity;

    /// <summary>是否已满。</summary>
    public bool IsFull => Count >= Capacity;

    /// <summary>
    /// 分配新行。在 entities[Count] 写入 entityId，Count++，返回行号。
    /// </summary>
    /// <returns>分配的行号。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int AllocateRow(Entity entityId)
    {
        EnsureWritable();
        return AllocateOwnedRow(entityId);
    }

    /// <summary>
    /// Prepared bundle batches retain a chunk only after AllocateRow has established ownership.
    /// Their inner loop may therefore append without repeating the COW ownership branch per row.
    /// This method is intentionally internal and must never receive a freshly forked chunk.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int AllocateOwnedRow(Entity entityId)
    {
        ChunkStorage storage = Volatile.Read(ref _storage);
        int row = Count;
        storage.Entities[row] = entityId;
        Count++;
        return row;
    }

    /// <summary>
    /// Swap-remove 指定行。将最后一行的数据覆盖到被删除行，Count--。
    /// </summary>
    /// <param name="row">要删除的行号。</param>
    /// <param name="columnOperations">每列的 SwapRemove 操作。</param>
    /// <returns>被移动到 row 位置的 Entity。如果 row == lastRow 返回 Entity.Null。</returns>
    internal Entity RemoveRow(int row, ReadOnlySpan<ComponentOperations> columnOperations)
    {
        EnsureWritable();
        int lastRow = Count - 1;
        MoveEnableBits(row, lastRow);

        if (row != lastRow)
        {
            // swap entity ids
            Entities[row] = Entities[lastRow];
            Entities[lastRow] = default;

            // swap each column
            for (int i = 0; i < ColumnCount; i++)
            {
                unsafe
                {
                    columnOperations[i].SwapRemove(
                        ColumnArray(i),
                        row,
                        lastRow);
                }
            }

            MoveVersions(row, lastRow);
            Count--;
            OrderVersion++;
            return Entities[row]; // 返回被移动到 row 位置的 entity
        }
        else
        {
            // 删除最后一行，无需移动
            Entities[lastRow] = default;
            // 清除末位数据（对 managed 组件重要）
            for (int i = 0; i < ColumnCount; i++)
            {
                unsafe
                {
                    columnOperations[i].SwapRemove(
                        ColumnArray(i),
                        lastRow,
                        lastRow);
                }
            }

            ClearVersions(lastRow);
            Count--;
            return Entity.Null;
        }
    }

    /// <summary>
    /// 写入 enableable 组件在指定 row 的 bit。
    /// </summary>
    internal void WriteEnabled(int maskIndex, int row, bool enabled)
    {
        UInt128[]? masks = Volatile.Read(ref _storage).EnableMasks;
        if (masks is null)
            throw new InvalidOperationException("Chunk does not have enable masks.");

        UInt128 rowMask = (UInt128)1 << row;
        if (((masks[maskIndex] & rowMask) != 0) == enabled)
            return;

        EnsureWritable();
        masks = Volatile.Read(ref _storage).EnableMasks!;
        if (enabled)
            masks[maskIndex] |= rowMask;
        else
            masks[maskIndex] &= ~rowMask;
    }

    /// <summary>
    /// 读取 enableable 组件在指定 row 的状态。
    /// </summary>
    internal bool IsEnabled(int maskIndex, int row)
    {
        Span<UInt128> masks = EnableMasks;
        if (masks.IsEmpty)
            throw new InvalidOperationException("Chunk does not have enable masks.");

        UInt128 rowMask = (UInt128)1 << row;
        return (masks[maskIndex] & rowMask) != 0;
    }

    /// <summary>
    /// 写入组件值。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteComponent<T>(int columnIndex, int row, in T value) where T : struct
    {
        EnsureWritable();
        ComponentRows<T>(columnIndex)[row] = value;
    }

    /// <summary>
    /// 读取组件值。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T ReadComponent<T>(int columnIndex, int row) where T : struct
    {
        return ComponentRows<T>(columnIndex)[row];
    }

    /// <summary>
    /// 获取组件的 ref 引用，支持原地修改。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex, int row) where T : struct
    {
        EnsureWritable();
        return ref ComponentRows<T>(columnIndex)[row];
    }

    /// <summary>
    /// Writes the complete ordinary-component row facts for a prepared batch row. The row was
    /// allocated through AllocateOwnedRow on the same retained chunk, so its backing is already
    /// private to this candidate and no per-field COW checks are required.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WritePreparedComponent<T>(
        int columnIndex,
        int row,
        in T value,
        uint version,
        int enableMaskIndex)
        where T : struct
    {
        ChunkStorage storage = Volatile.Read(ref _storage);
        Unsafe.As<T[]>(storage.Columns[columnIndex])[row] = value;
        storage.AddVersions[columnIndex][row] = version;
        storage.WriteVersions[columnIndex][row] = version;
        storage.ChangeVersions[columnIndex] = version;

        if (enableMaskIndex >= 0)
            storage.EnableMasks![enableMaskIndex] |= (UInt128)1 << row;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly T GetComponentReadOnlyRef<T>(int columnIndex, int row) where T : struct
    {
        return ref ComponentRows<T>(columnIndex)[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyVersions(int sourceColumn, int sourceRow, int targetColumn, int targetRow, Chunk target)
    {
        target.EnsureWritable();
        uint addVersion = AddVersionRows(sourceColumn)[sourceRow];
        uint writeVersion = WriteVersionRows(sourceColumn)[sourceRow];
        uint chunkVersion = ChangeVersions[sourceColumn];
        target.AddVersionRows(targetColumn)[targetRow] = addVersion;
        target.WriteVersionRows(targetColumn)[targetRow] = writeVersion;

        // A structural move must preserve both the exact row facts and the chunk-level coarse
        // filter. Without this aggregation an Added/Changed row can be copied into a destination
        // chunk whose ChangeVersions entry is zero, causing query matching to reject the entire
        // chunk before it reaches the exact row filter.
        if (SomeEngine.ECS.VersionClock.IsNewer(
                writeVersion,
                target.ChangeVersions[targetColumn]))
        {
            target.ChangeVersions[targetColumn] = writeVersion;
        }
        // Some logical stores (notably dynamic-buffer header/inline backing) publish only the
        // coarse chunk version. A structural row move must retain that fact even when the row's
        // ordinary WriteVersion did not change.
        if (SomeEngine.ECS.VersionClock.IsNewer(
                chunkVersion,
                target.ChangeVersions[targetColumn]))
        {
            target.ChangeVersions[targetColumn] = chunkVersion;
        }
    }

    /// <summary>
    /// Creates a detached chunk shell which shares its immutable backing with this chunk until the
    /// candidate performs its first chunk mutation. Detach is capacity-exact and preserves
    /// immutable buffer-overflow references; each row detaches its own overflow only before a
    /// real buffer content write.
    /// </summary>
    internal Chunk ForkDetached(ReadOnlySpan<ComponentOperations> candidateColumnOperations)
    {
        if (candidateColumnOperations.Length != ColumnCount)
            throw new InvalidOperationException("Candidate archetype column shape does not match the source chunk.");
        if (!EnableMasks.IsEmpty && Capacity > 128)
            throw new InvalidOperationException("Enableable chunk capacity cannot exceed 128 rows.");
        if ((uint)Count > (uint)Capacity)
            throw new InvalidOperationException("Chunk Count is outside its capacity.");

        for (int column = 0; column < ColumnCount; column++)
        {
            Array sourceColumn = (Array)ColumnArray(column);
            if (sourceColumn.Length != Capacity)
                throw new InvalidOperationException("Chunk column length does not match chunk capacity.");
            if (AddVersionRows(column).Length != Capacity || WriteVersionRows(column).Length != Capacity)
                throw new InvalidOperationException("Chunk row-version length does not match chunk capacity.");

        }

        return new Chunk(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAdd(int column, int row, uint version)
    {
        EnsureWritable();
        AddVersionRows(column)[row] = version;
        WriteVersionRows(column)[row] = version;
        VersionClock.PublishNewest(ref ChangeVersions[column], version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAddRange(int column, int startRow, int count, uint version)
    {
        EnsureWritable();
        AddVersionRows(column).Slice(startRow, count).Fill(version);
        WriteVersionRows(column).Slice(startRow, count).Fill(version);
        VersionClock.PublishNewest(ref ChangeVersions[column], version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWrite(int column, int row, uint version)
    {
        EnsureWritable();
        WriteVersionRows(column)[row] = version;
        VersionClock.PublishNewest(ref ChangeVersions[column], version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWriteRange(int column, int startRow, int count, uint version)
    {
        EnsureWritable();
        WriteVersionRows(column).Slice(startRow, count).Fill(version);
        VersionClock.PublishNewest(ref ChangeVersions[column], version);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkChunk(int column, uint version)
    {
        EnsureWritable();
        VersionClock.PublishNewest(ref ChangeVersions[column], version);
    }

    /// <summary>
    /// Detaches all row/column backing in one bounded chunk clone. This is deliberately a chunk
    /// copy rather than an unbounded mutation overlay: every published generation has a direct,
    /// immutable backing and candidate abort only drops its private storage reference.
    /// </summary>
    internal void EnsureWritable()
    {
        ChunkStorage observed = Volatile.Read(ref _storage);
        if (observed.OwnerIdentity == _ownershipIdentity)
            return;

        // Shared backing is immutable, so its identity is also a zero-allocation first-detach
        // gate. Independent chunks retain independent locks.
        ChunkStorage lockedStorage = observed;
        lock (lockedStorage)
        {
            observed = Volatile.Read(ref _storage);
            if (observed.OwnerIdentity == _ownershipIdentity)
                return;

            object[] columns = new object[observed.Columns.Length];
            uint[][] addVersions = new uint[observed.AddVersions.Length][];
            uint[][] writeVersions = new uint[observed.WriteVersions.Length][];
            for (int column = 0; column < columns.Length; column++)
            {
                Array sourceColumn = (Array)observed.Columns[column];
                Array candidateColumn = (Array)sourceColumn.Clone();
                columns[column] = candidateColumn;
                addVersions[column] = (uint[])observed.AddVersions[column].Clone();
                writeVersions[column] = (uint[])observed.WriteVersions[column].Clone();

            }

            var detached = new ChunkStorage(
                Interlocked.Increment(ref s_nextStorageIdentity),
                _ownershipIdentity,
                checked(observed.Version + 1),
                (Entity[])observed.Entities.Clone(),
                columns,
                (uint[])observed.ChangeVersions.Clone(),
                addVersions,
                writeVersions,
                observed.EnableMasks is null ? null : (UInt128[])observed.EnableMasks.Clone());

            Volatile.Write(ref _storage, detached);
        }
    }

    private void MoveEnableBits(int row, int lastRow)
    {
        Span<UInt128> masks = EnableMasks;
        if (masks.IsEmpty)
            return;

        UInt128 rowMask = (UInt128)1 << row;
        UInt128 lastRowMask = (UInt128)1 << lastRow;

        for (int i = 0; i < masks.Length; i++)
        {
            UInt128 mask = masks[i];
            if (row != lastRow)
            {
                bool lastEnabled = (mask & lastRowMask) != 0;
                if (lastEnabled)
                    mask |= rowMask;
                else
                    mask &= ~rowMask;
            }

            mask &= ~lastRowMask;
            masks[i] = mask;
        }
    }

    private void MoveVersions(int row, int lastRow)
    {
        ChunkStorage storage = Volatile.Read(ref _storage);
        for (int column = 0; column < storage.AddVersions.Length; column++)
        {
            uint[] addVersions = storage.AddVersions[column];
            uint[] writeVersions = storage.WriteVersions[column];
            addVersions[row] = addVersions[lastRow];
            writeVersions[row] = writeVersions[lastRow];
            addVersions[lastRow] = 0;
            writeVersions[lastRow] = 0;
        }
    }

    private void ClearVersions(int row)
    {
        ChunkStorage storage = Volatile.Read(ref _storage);
        for (int column = 0; column < storage.AddVersions.Length; column++)
        {
            storage.AddVersions[column][row] = 0;
            storage.WriteVersions[column][row] = 0;
        }
    }

    private sealed class ChunkStorage
    {
        internal ChunkStorage(
            long identity,
            long ownerIdentity,
            long version,
            Entity[] entities,
            object[] columns,
            uint[] changeVersions,
            uint[][] addVersions,
            uint[][] writeVersions,
            UInt128[]? enableMasks)
        {
            Identity = identity;
            OwnerIdentity = ownerIdentity;
            Version = version;
            Entities = entities;
            Columns = columns;
            ChangeVersions = changeVersions;
            AddVersions = addVersions;
            WriteVersions = writeVersions;
            EnableMasks = enableMasks;
        }

        internal long Identity { get; }
        internal long OwnerIdentity { get; }
        internal long Version { get; }
        internal Entity[] Entities { get; }
        internal object[] Columns { get; }
        internal uint[] ChangeVersions { get; }
        internal uint[][] AddVersions { get; }
        internal uint[][] WriteVersions { get; }
        internal UInt128[]? EnableMasks { get; }
    }
}

