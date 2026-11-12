using System.Runtime.InteropServices;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Entities;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct Entity(int Id, int Generation)
{
    public static Entity Null => default;
    public override int GetHashCode() => unchecked(Id + Generation * -1521134295);
}

[StructLayout(LayoutKind.Sequential)]
internal struct EntityRecord
{
    internal Chunk? Chunk;
    internal int Row;
    internal int Generation;

    internal EntityRecord(Chunk? chunk, int row, int generation)
        => (Chunk, Row, Generation) = (chunk, row, generation);
}
