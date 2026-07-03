using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS;

public partial class World
{
    internal Entity ReserveEntity()
    {
        return _entities.Reserve();
    }

    internal void SpawnReserved(Entity entity)
    {
        _entities.Spawn(entity);
    }

    internal bool ReleaseReserved(Entity entity)
    {
        return _entities.Release(entity);
    }
}

