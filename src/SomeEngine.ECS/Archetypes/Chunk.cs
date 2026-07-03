using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Archetypes;

/// <summary>
/// Archetype 内的固定容量列存储块。约 16KB 逻辑大小。
/// Entity 以紧凑方式存储，删除时通过 swap-remove 保持无空洞。
/// </summary>
/// <remarks>
/// 设计引用：docs/DESIGN.md §4.3, §4.4
/// - entities 是特殊列（固定存在，不通过 ColumnMetas 索引）
/// - columns[i] 是类型擦除的 object（T[]），通过 Unsafe.As 还原强类型
/// </remarks>
public class Chunk
{
    /// <summary>每行的 Entity。</summary>
    public readonly Entity[] Entities;

    /// <summary>每列的类型擦除存储，实际对象为 T[]。</summary>
    public readonly object[] Columns;

    /// <summary>当前已分配的行数。</summary>
    public int Count;

    /// <summary>最大行数。</summary>
    public readonly int Capacity;

    /// <summary>Per-column change version tick.</summary>
    public readonly uint[] ChangeVersions;

    /// <summary>Per-column, per-row add version tick.</summary>
    public readonly uint[][] AddVersions;

    /// <summary>Per-column, per-row write version tick.</summary>
    public readonly uint[][] WriteVersions;

    /// <summary>每个 enableable 列对应一个 128-bit mask。</summary>
    public readonly UInt128[]? EnableMasks;

    /// <summary>行序变更版本（swap-remove 时递增）。</summary>
    public uint OrderVersion;

    /// <summary>该 Chunk 在 Archetype.Chunks 列表中的索引。由 AllocateInChunk 设置，TryRecycleChunk swap 时更新。</summary>
    public int IndexInArchetype;

    /// <summary>
    /// Chunk 级 SharedComponent 索引。parallel 到 Archetype.SharedComponentIds。
    /// null = archetype 不含 SharedComponent。
    /// </summary>
    public int[]? SharedValues;

    /// <summary>
    /// 构造 Chunk。
    /// </summary>
    /// <param name="capacity">每 chunk 的 entity 容量。</param>
    /// <param name="columnMetas">列元数据（决定每列的类型和大小）。</param>
    internal Chunk(int capacity, ColumnMetadata[] columnMetas, int enableableColumnCount = 0)
    {
        Capacity = capacity;
        Count = 0;
        Entities = new Entity[capacity];
        Columns = new object[columnMetas.Length];
        ChangeVersions = new uint[columnMetas.Length];
        AddVersions = new uint[columnMetas.Length][];
        WriteVersions = new uint[columnMetas.Length][];
        EnableMasks = enableableColumnCount > 0 ? new UInt128[enableableColumnCount] : null;

        for (int i = 0; i < columnMetas.Length; i++)
        {
            unsafe
            {
                Columns[i] = columnMetas[i].Operations.CreateArray(capacity);
            }

            AddVersions[i] = new uint[capacity];
            WriteVersions[i] = new uint[capacity];
        }
    }

    /// <summary>是否已满。</summary>
    public bool IsFull => Count >= Capacity;

    /// <summary>
    /// 分配新行。在 entities[Count] 写入 entityId，Count++，返回行号。
    /// </summary>
    /// <returns>分配的行号。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int AllocateRow(Entity entityId)
    {
        int row = Count;
        Entities[row] = entityId;
        Count++;
        return row;
    }

    /// <summary>
    /// Swap-remove 指定行。将最后一行的数据覆盖到被删除行，Count--。
    /// </summary>
    /// <param name="row">要删除的行号。</param>
    /// <param name="columnMetas">列元数据（提供 SwapRemove 函数指针）。</param>
    /// <returns>被移动到 row 位置的 Entity。如果 row == lastRow 返回 Entity.Null。</returns>
    internal Entity RemoveRow(int row, ColumnMetadata[] columnMetas)
    {
        int lastRow = Count - 1;
        MoveEnableBits(row, lastRow);

        if (row != lastRow)
        {
            // swap entity ids
            Entities[row] = Entities[lastRow];
            Entities[lastRow] = default;

            // swap each column
            for (int i = 0; i < Columns.Length; i++)
            {
                unsafe
                {
                    columnMetas[i].Operations.SwapRemove(Columns[i], row, lastRow);
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
            for (int i = 0; i < Columns.Length; i++)
            {
                unsafe
                {
                    columnMetas[i].Operations.SwapRemove(Columns[i], lastRow, lastRow);
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
        if (EnableMasks is null)
            throw new InvalidOperationException("Chunk does not have enable masks.");

        UInt128 rowMask = (UInt128)1 << row;
        if (enabled)
            EnableMasks[maskIndex] |= rowMask;
        else
            EnableMasks[maskIndex] &= ~rowMask;
    }

    /// <summary>
    /// 读取 enableable 组件在指定 row 的状态。
    /// </summary>
    internal bool IsEnabled(int maskIndex, int row)
    {
        if (EnableMasks is null)
            throw new InvalidOperationException("Chunk does not have enable masks.");

        UInt128 rowMask = (UInt128)1 << row;
        return (EnableMasks[maskIndex] & rowMask) != 0;
    }

    /// <summary>
    /// 写入组件值。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteComponent<T>(int columnIndex, int row, in T value) where T : struct
    {
        Unsafe.As<T[]>(Columns[columnIndex])[row] = value;
    }

    /// <summary>
    /// 读取组件值。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T ReadComponent<T>(int columnIndex, int row) where T : struct
    {
        return Unsafe.As<T[]>(Columns[columnIndex])[row];
    }

    /// <summary>
    /// 获取组件的 ref 引用，支持原地修改。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T GetComponentRef<T>(int columnIndex, int row) where T : struct
    {
        return ref Unsafe.As<T[]>(Columns[columnIndex])[row];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void CopyVersions(int sourceColumn, int sourceRow, int targetColumn, int targetRow, Chunk target)
    {
        target.AddVersions[targetColumn][targetRow] = AddVersions[sourceColumn][sourceRow];
        target.WriteVersions[targetColumn][targetRow] = WriteVersions[sourceColumn][sourceRow];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAdd(int column, int row, uint version)
    {
        AddVersions[column][row] = version;
        WriteVersions[column][row] = version;
        ChangeVersions[column] = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkAddRange(int column, int startRow, int count, uint version)
    {
        AddVersions[column].AsSpan(startRow, count).Fill(version);
        WriteVersions[column].AsSpan(startRow, count).Fill(version);
        ChangeVersions[column] = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWrite(int column, int row, uint version)
    {
        WriteVersions[column][row] = version;
        ChangeVersions[column] = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkWriteRange(int column, int startRow, int count, uint version)
    {
        WriteVersions[column].AsSpan(startRow, count).Fill(version);
        ChangeVersions[column] = version;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkChunk(int column, uint version)
    {
        ChangeVersions[column] = version;
    }

    private void MoveEnableBits(int row, int lastRow)
    {
        if (EnableMasks is null)
            return;

        UInt128 rowMask = (UInt128)1 << row;
        UInt128 lastRowMask = (UInt128)1 << lastRow;

        for (int i = 0; i < EnableMasks.Length; i++)
        {
            UInt128 mask = EnableMasks[i];
            if (row != lastRow)
            {
                bool lastEnabled = (mask & lastRowMask) != 0;
                if (lastEnabled)
                    mask |= rowMask;
                else
                    mask &= ~rowMask;
            }

            mask &= ~lastRowMask;
            EnableMasks[i] = mask;
        }
    }

    private void MoveVersions(int row, int lastRow)
    {
        for (int column = 0; column < AddVersions.Length; column++)
        {
            AddVersions[column][row] = AddVersions[column][lastRow];
            WriteVersions[column][row] = WriteVersions[column][lastRow];
            AddVersions[column][lastRow] = 0;
            WriteVersions[column][lastRow] = 0;
        }
    }

    private void ClearVersions(int row)
    {
        for (int column = 0; column < AddVersions.Length; column++)
        {
            AddVersions[column][row] = 0;
            WriteVersions[column][row] = 0;
        }
    }
}

