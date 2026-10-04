using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS;

public partial class World
{
    internal bool HasComponentData<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
            return HasSparseComponentData<T>(entity);

        using WorldJobAdmissionScope access = EnterJobTopologyRead();
        return _components.Has<T>(entity);
    }

    internal bool HasComponentData(Entity entity, ushort component)
    {
        if (ComponentRegistry.Get(component).Storage == StoragePath.Sparse)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobAdmission(WorldJobAdmissionRequest.ForStorage(
                WorldStorageKind.Sparse, component, WorldStorageAccess.Read));
            return _entities.Alive(entity) && _sparse.HasValue(component, entity);
        }

        using WorldJobAdmissionScope access = EnterJobTopologyRead();
        return _components.Has(entity, component);
    }

    internal void AddComponentData<T>(Entity entity, in T value) where T : struct
    {
        if (default(T) is ISparseComponent)
            AddSparseComponentData(entity, in value);
        else
            AddTableComponentData(entity, in value);
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

    /// <summary>Discards data introduced by a build that is restoring its previous result.</summary>
    internal void DiscardComponentData<T>(Entity entity) where T : struct
    {
        if (default(T) is ISparseComponent)
        {
            using WorldJobAdmissionScope sparseAccess = EnterJobSparse<T>(WorldStorageAccess.Write);
            _sparse.Remove<T>(entity);
            return;
        }

        using WorldJobAdmissionScope access = EnterJobTopologyWrite();
        PublicComponentMutationGuard.Structural<T>(nameof(DiscardComponentData));
        _components.Discard<T>(entity);
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

    internal uint? ComponentDataWriteVersion(Entity entity, ushort component)
    {
        bool sparse = ComponentRegistry.Get(component).Storage == StoragePath.Sparse;
        using WorldJobAdmissionScope access = EnterJobAdmission(WorldJobAdmissionRequest.ForStorage(
            sparse ? WorldStorageKind.Sparse : WorldStorageKind.Table, component, WorldStorageAccess.Read));
        if (sparse)
            return _entities.Alive(entity) && _sparse.HasValue(component, entity)
                ? _sparse.WriteVersion(entity, component)
                : null;
        return _components.Has(entity, component) ? _components.WriteVersion(entity, component) : null;
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
            ReplaceSparseComponentData(entity, in value);
        else
            ReplaceTableComponentData(entity, in value);
    }
}
