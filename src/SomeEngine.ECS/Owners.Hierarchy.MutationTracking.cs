using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

internal sealed partial class Hierarchy
{
    internal void BeginEdit()
    {
        _editDepth++;
    }

    internal void EndEdit()
    {
        if (_editDepth <= 0)
            throw new InvalidOperationException("Hierarchy edit scope is unbalanced.");

        _editDepth--;
    }

    internal void TrackParent<T>(Entity entity)
        where T : struct
    {
        if (IsEditing)
            return;

        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            return;
        }

        if (TryResolve(
                ComponentMetadata<T>.HierarchyRegistration,
                ComponentMetadata<T>.Id,
                out var store,
                out bool source) && source)
        {
            store.CaptureBeforeMutation(entity);
        }
    }

    internal void TrackParent(Entity entity, int componentId)
    {
        if (IsEditing)
            return;

        ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
        if (!info.IsRelationshipSource && !info.IsRelationshipTarget)
            return;

        if (TryResolve(componentId, out var store, out bool source))
        {
            if (source)
                store.CaptureBeforeMutation(entity);
            else
                store.RequireChildrenNormalization(entity);
        }
    }

    internal void RequireScan<T>()
        where T : struct
    {
        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            return;
        }

        if (TryResolve(
                ComponentMetadata<T>.HierarchyRegistration,
                ComponentMetadata<T>.Id,
                out var store,
                out bool source) && source)
        {
            store.RequireScan();
        }
    }

    internal void RequireScan(int componentId)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
        if (!info.IsRelationshipSource && !info.IsRelationshipTarget)
            return;

        if (TryResolve(componentId, out var store, out bool source) && source)
            store.RequireScan();
    }

    internal void RequireScan(ReadOnlySpan<int> componentIds)
    {
        for (int i = 0; i < componentIds.Length; i++)
            RequireScan(componentIds[i]);
    }

    internal void RequireScan(Archetype archetype)
    {
        ReadOnlySpan<int> componentIds = archetype.TableComponentIds;
        for (int i = 0; i < componentIds.Length; i++)
            RequireScan(componentIds[i]);
    }

    internal void ValidateDeferredWrites()
    {
        foreach (IHierarchyDomainStore store in _domains.Values)
            store.ValidateDeferredWrites();
    }

    internal void RollbackDeferredWrites()
    {
        foreach (IHierarchyDomainStore store in _domains.Values)
            store.RollbackDeferredWrites();
    }

    internal void CommitDeferredWrites()
    {
        foreach (IHierarchyDomainStore store in _domains.Values)
            store.CommitDeferredWrites();
    }

    internal void OnEntityDestroying(Entity entity)
    {
        if (_domains.Count == 0 || !Alive(entity))
            return;

        _destroyingEntities.Add(entity);
        BeginEdit();
        try
        {
            lock (_registrationLock)
            {
                foreach (IHierarchyDomainStore store in _domains.Values)
                    store.OnEntityDestroying(entity);
            }
        }
        finally
        {
            EndEdit();
            _destroyingEntities.Remove(entity);
        }
    }

    internal void BeginTerminalDestroy(ReadOnlySpan<Entity> entities)
    {
        if (_terminalDestroyEntities is not null)
            throw new InvalidOperationException("Nested hierarchy terminal-destroy scopes are not supported.");

        var terminalEntities = new HashSet<Entity>(entities.Length);
        for (int i = 0; i < entities.Length; i++)
            terminalEntities.Add(entities[i]);
        _terminalDestroyEntities = terminalEntities;
        var prepared = new List<IHierarchyDomainStore>(_domains.Count);
        try
        {
            // Build each domain's canonical/applied direct-child plan once. DestroySubtree then
            // consumes parent-local arrays instead of rescanning every Parent column for every
            // entity in the subtree.
            lock (_registrationLock)
            {
                foreach (IHierarchyDomainStore store in _domains.Values)
                {
                    store.BeginTerminalDestroy(entities);
                    prepared.Add(store);
                }
            }
        }
        catch
        {
            for (int i = 0; i < prepared.Count; i++)
                prepared[i].EndTerminalDestroy();
            _terminalDestroyEntities = null;
            throw;
        }
    }

    internal void EndTerminalDestroy()
    {
        lock (_registrationLock)
        {
            foreach (IHierarchyDomainStore store in _domains.Values)
                store.EndTerminalDestroy();
        }
        _terminalDestroyEntities = null;
    }

    internal bool IsTerminallyDestroying(Entity entity) =>
        _destroyingEntities.Contains(entity) ||
        _terminalDestroyEntities?.Contains(entity) == true;

    internal void Reset()
    {
        _editDepth = 0;
        _destroyingEntities.Clear();
        _terminalDestroyEntities = null;
        foreach (IHierarchyDomainStore store in _domains.Values)
            store.Reset();
    }
}
