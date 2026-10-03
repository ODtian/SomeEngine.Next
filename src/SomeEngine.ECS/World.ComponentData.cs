using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    internal bool HasComponentData<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Read);
            return _sparse.Has<T>(entity);
        }
        using WorldJobAdmissionScope access = EnterJobTopologyRead();
        return _components.Has<T>(entity);
    }

    internal bool HasComponentData(Entity entity, ushort component)
    {
        bool sparse = ComponentRegistry.Get(component).Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope access = sparse
            ? EnterJobAdmission(WorldJobAdmissionRequest.ForStorage(WorldStorageKind.Sparse, component, WorldStorageAccess.Read))
            : EnterJobTopologyRead();
        if (!_entities.Store.IsAlive(entity))
            return false;
        return sparse
            ? _sparse.HasValue(component, entity)
            : _entities.Store.GetRecordReadOnly(entity).Archetype!.HasComponent(component);
    }

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

    internal ref readonly T ReadComponentReference<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Read);
            return ref _sparse.ReadRef<T>(entity);
        }

        using WorldJobAdmissionScope access =
            EnterJobComponent(ComponentMetadata<T>.Id, WorldStorageAccess.Read);
        return ref _components.ReadRef<T>(entity);
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

    internal uint ComponentDataWriteVersion(Entity entity, ushort component)
    {
        bool sparse = ComponentRegistry.Get(component).Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope access = EnterJobAdmission(WorldJobAdmissionRequest.ForStorage(
            sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table, component, WorldStorageAccess.Read));
        return sparse ? _sparse.WriteVersion(entity, component) : _components.WriteVersion(entity, component);
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
