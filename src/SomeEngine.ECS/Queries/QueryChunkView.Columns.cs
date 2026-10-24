using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Queries;

public readonly ref partial struct QueryChunkView
{
    /// <summary>Binds component read/write access once; indexed writes publish each accessed row's version.</summary>
    public ComponentWriteColumn<T> ReadWriteColumn<T>() where T : struct
    {
        int column = QueryAccessGuards.RequireAccess<T>(_match, read: true, write: true);
        return new(_world.Components, _chunk, column, CurrentSystemVersion);
    }

    /// <summary>Binds buffer read access once; row selection remains available through RowIndices.</summary>
    public BufferReadColumn<T> ReadBufferColumn<T>() where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(_match, read: true, write: false, out int headerColumn, out int inlineColumn);
        return new(_chunk, headerColumn, inlineColumn);
    }
}
