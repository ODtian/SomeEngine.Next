using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Queries;

/// <summary>Indexed buffer reads borrowed from one query chunk.</summary>
public readonly ref struct BufferReadColumn<T> where T : struct, IBufferElement
{
    private readonly Chunk _chunk;
    private readonly int _headerColumn, _inlineColumn;

    internal BufferReadColumn(Chunk chunk, int headerColumn, int inlineColumn) =>
        (_chunk, _headerColumn, _inlineColumn) = (chunk, headerColumn, inlineColumn);

    public BufferView<T> this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)row >= (uint)_chunk.Count) throw new ArgumentOutOfRangeException(nameof(row));
            return new(_chunk, row, _headerColumn, _inlineColumn);
        }
    }
}
