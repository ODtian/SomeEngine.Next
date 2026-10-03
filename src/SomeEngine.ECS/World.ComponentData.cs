using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    internal void AddComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Write);
            _sparse.Add(entity, in value);
            return;
        }

        using WorldJobAdmissionScope access = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Structural<T>(nameof(AddComponentData));
        _components.Add(entity, in value);
    }

    internal T ReadComponentData<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Read);
            return _sparse.Read<T>(entity);
        }

        using WorldJobAdmissionScope access =
            EnterJobComponent(ComponentMetadata<T>.Id, WorldStorageAccess.Read);
        return _components.Read<T>(entity);
    }
}
