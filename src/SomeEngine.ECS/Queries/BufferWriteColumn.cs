using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Queries;

/// <summary>Indexed writable buffers borrowed from one query chunk.</summary>
public readonly ref struct BufferWriteColumn<T> where T : struct, IBufferElement
{
    private readonly Owners.Buffers _buffers;
    private readonly Chunk _chunk;
    private readonly int _headerColumn, _inlineColumn;
    private readonly uint _version;

    internal BufferWriteColumn(Owners.Buffers buffers, Chunk chunk, int headerColumn, int inlineColumn, uint version) =>
        (_buffers, _chunk, _headerColumn, _inlineColumn, _version) = (buffers, chunk, headerColumn, inlineColumn, version);

    public DynamicBuffer<T> this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)row >= (uint)_chunk.Count) throw new ArgumentOutOfRangeException(nameof(row));
            return new(_buffers, _chunk, row, _headerColumn, _inlineColumn, _version);
        }
    }
}
