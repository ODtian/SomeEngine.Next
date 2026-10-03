using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Sparse
{
    internal ref byte WriteBytes(Entity entity, int component)
    {
        _entities.ThrowDead(entity);
        object?[] stores = Volatile.Read(ref _stores);
        if ((uint)component >= (uint)stores.Length || stores[component] is not ISparseSet sparseSet)
            throw new InvalidOperationException($"Entity {entity} does not have this sparse component.");

        return ref sparseSet.WriteBytes(entity);
    }
}
