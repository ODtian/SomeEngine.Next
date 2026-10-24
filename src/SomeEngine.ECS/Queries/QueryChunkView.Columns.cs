using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

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

    /// <summary>Binds buffer write access once; each returned buffer retains the current query write version.</summary>
    public BufferWriteColumn<T> WriteBufferColumn<T>() where T : struct, IBufferElement
    {
        QueryAccessGuards.RequireBufferAccess<T>(_match, read: false, write: true, out int headerColumn, out int inlineColumn);
        return new(_world.Buffers, _chunk, headerColumn, inlineColumn, CurrentSystemVersion);
    }

    /// <summary>Binds component read access and its enable-mask index once, then reads the current bits.</summary>
    public ComponentEnabledColumn ReadEnabledColumn<T>() where T : struct, IEnableableComponent
    {
        QueryAccessGuards.RequireAccess<T>(_match, read: true, write: false);
        return new(_chunk, _match.Archetype.EnableMask(ComponentMetadata<T>.Id));
    }
}
