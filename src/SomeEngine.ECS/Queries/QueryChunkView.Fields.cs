using SomeEngine.Core.Diagnostics;

namespace SomeEngine.ECS.Queries;

public readonly ref partial struct QueryChunkView
{
    /// <summary>Acquires typed field reads once for the entire selected column.</summary>
    public FieldReadView<TComponent, TValue, TAccess> ReadFields<TComponent, TValue, TAccess>()
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        DevelopmentCheck.Require(!_match.HasRowFilter,
            "Field column access requires a query with whole-chunk selection.");
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: false);
        return new(_chunk.ComponentRows<TComponent>(column)[.._chunk.Count]);
    }

    /// <summary>Acquires typed field writes and records this execution's version for the column.</summary>
    public FieldWriteView<TComponent, TValue, TAccess> WriteFields<TComponent, TValue, TAccess>()
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        DevelopmentCheck.Require(!_match.HasRowFilter,
            "Field column access requires a query with whole-chunk selection.");
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: true);
        return new(_world.Components.WriteChunk<TComponent>(_chunk, column, CurrentSystemVersion));
    }

    public TValue ReadField<TComponent, TValue, TAccess>(int row)
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        DevelopmentCheck.Require((uint)row < (uint)_chunk.Count,
            "Field access requires a row within the current query chunk.");
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: false);
        return TAccess.Read(in _chunk.GetComponentReadOnlyRef<TComponent>(column, row));
    }

    public void WriteField<TComponent, TValue, TAccess>(int row, in TValue value)
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        DevelopmentCheck.Require((uint)row < (uint)_chunk.Count,
            "Field access requires a row within the current query chunk.");
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: true);
        TAccess.Write(ref _world.Components.WriteRef<TComponent>(
            _chunk.Entities[row], _chunk, row, column, CurrentSystemVersion), in value);
    }
}
