namespace SomeEngine.ECS.Queries;

public readonly ref partial struct QueryRow
{
    public TValue ReadField<TComponent, TValue, TAccess>()
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: false);
        return TAccess.Read(in _chunk.GetComponentReadOnlyRef<TComponent>(column, _row));
    }

    public void WriteField<TComponent, TValue, TAccess>(in TValue value)
        where TComponent : struct, IComponent
        where TValue : unmanaged
        where TAccess : struct, IFieldAccess<TComponent, TValue>
    {
        int column = QueryAccessGuards.RequireFieldAccess<TComponent>(_match, write: true);
        TAccess.Write(ref _world.Components.WriteRef<TComponent>(
            Entity, _chunk, _row, column, CurrentSystemVersion), in value);
    }
}
