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

    internal uint ComponentDataWriteVersion<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Read);
            return _sparse.WriteVersion<T>(entity);
        }

        using WorldJobAdmissionScope access =
            EnterJobComponent(ComponentMetadata<T>.Id, WorldStorageAccess.Read);
        return _components.WriteVersion<T>(entity);
    }

    /// <summary>Stores an equal value while preserving component write versions.</summary>
    internal void OverwriteComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Write);
            _sparse.Overwrite(entity, in value);
            return;
        }

        using WorldJobAdmissionScope access =
            EnterJobComponent(ComponentMetadata<T>.Id, WorldStorageAccess.Write);
        PublicComponentMutationGuard.Value<T>(nameof(OverwriteComponentData));
        _components.Overwrite(entity, in value);
    }

    internal void ReplaceComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Write);
            _sparse.Replace(entity, in value);
            return;
        }

        using WorldJobAdmissionScope access = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Value<T>(nameof(ReplaceComponentData));
        _components.Replace(entity, in value);
    }
}
