using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

/// <summary>Indexed component writes borrowed from one query chunk.</summary>
public readonly ref struct ComponentWriteColumn<T> where T : struct
{
    private readonly Owners.Components _components;
    private readonly Chunk _chunk;
    private readonly int _column;
    private readonly uint _version;

    internal ComponentWriteColumn(Owners.Components components, Chunk chunk, int column, uint version) =>
        (_components, _chunk, _column, _version) = (components, chunk, column, version);

    public ref T this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)row >= (uint)_chunk.Count) throw new ArgumentOutOfRangeException(nameof(row));
            return ref _components.WriteRef<T>(_chunk.Entities[row], _chunk, row, _column, _version);
        }
    }
}
