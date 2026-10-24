using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Queries;

/// <summary>Member-row traversal with writable component and buffer columns bound together per chunk.</summary>
public ref struct ComponentBufferRowEnumerator<TComponent, TBuffer>
    where TComponent : struct
    where TBuffer : struct, IBufferElement
{
    private QueryRowEnumerator<NoSharedFilter> _rows;
    private readonly Owners.Components _components;
    private readonly Owners.Buffers _buffers;
    private int _componentColumn, _headerColumn, _inlineColumn;

    internal ComponentBufferRowEnumerator(World world, QueryRowEnumerator<NoSharedFilter> rows)
    {
        _rows = rows;
        _components = world.Components;
        _buffers = world.Buffers;
        _componentColumn = _headerColumn = _inlineColumn = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext(out QueryRow row, out ComponentWriteReference<TComponent> component, out DynamicBuffer<TBuffer> buffer)
    {
        Chunk? previous = _rows.CurrentChunk;
        if (!_rows.MoveNext(out row))
        {
            component = default;
            buffer = default;
            return false;
        }
        Chunk chunk = _rows.CurrentChunk!;
        if (!ReferenceEquals(previous, chunk)) BindColumns(_rows.CurrentMatch);

        component = new(ref _components.WriteRef<TComponent>(
            row.Entity, chunk, row.RowIndex, _componentColumn, row.CurrentSystemVersion));
        buffer = new(_buffers, chunk, row.RowIndex, _headerColumn, _inlineColumn, row.CurrentSystemVersion);
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void BindColumns(QueryArchetypeMatch match)
    {
        _componentColumn = QueryAccessGuards.RequireAccess<TComponent>(match, read: true, write: true);
        QueryAccessGuards.RequireBufferAccess<TBuffer>(match, read: true, write: true, out _headerColumn, out _inlineColumn);
    }
}
