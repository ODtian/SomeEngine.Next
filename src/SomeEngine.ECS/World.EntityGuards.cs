using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS;

public partial class World
{
    internal void ThrowIfDead(Entity entity)
    {
        _entities.ThrowDead(entity);
    }

    internal ref EntityRecord GetRow(Entity entity)
    {
        return ref _entities.Row(entity);
    }
}

