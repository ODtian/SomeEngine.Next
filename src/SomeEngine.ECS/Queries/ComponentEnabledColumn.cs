using System.Runtime.CompilerServices;
using SomeEngine.ECS.Archetypes;

namespace SomeEngine.ECS.Queries;

/// <summary>Current component enable bits borrowed from one query chunk.</summary>
public readonly ref struct ComponentEnabledColumn
{
    private readonly Chunk _chunk;
    private readonly int _mask;

    internal ComponentEnabledColumn(Chunk chunk, int mask) => (_chunk, _mask) = (chunk, mask);

    public bool this[int row]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)row >= (uint)_chunk.Count) throw new ArgumentOutOfRangeException(nameof(row));
            return _chunk.IsEnabled(_mask, row);
        }
    }
}
