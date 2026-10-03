using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Sparse
{
    internal ref byte WriteBytes(Entity entity, int component)
    {
        _entities.ThrowDead(entity);
        object?[] stores = Volatile.Read(ref _stores);
        DevelopmentCheck.Require((uint)component < (uint)stores.Length && stores[component] is ISparseSet,
            $"Entity {entity} requires sparse component {component} for byte access.");
        uint version = unchecked(_structure.Clock.Acquire() + 1);
        return ref ((ISparseSet)stores[component]!).WriteBytes(entity, version);
    }
}
