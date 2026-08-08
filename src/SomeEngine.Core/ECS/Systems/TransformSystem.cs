using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Core.ECS.Systems;

public sealed class TransformSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<EntityId> _rootsScratch = [];
    private readonly List<EntityId> _stackScratch = [];
    private readonly List<EntityId> _dirtyRootsScratch = [];
    private readonly List<EntityId> _staleParentScratch = [];
    private readonly HashSet<EntityId> _dirtySet = [];
    private readonly HashSet<EntityId> _seenParentSet = [];
    private readonly HashSet<EntityId> _updatedSet = [];
    private readonly Dictionary<EntityId, EntityId> _knownParents = [];
    private QueryHandle _transformQuery;
    private QueryHandle _parentedTopologyQuery;
    private QueryHandle _changedFlatLeafLocalQuery;
    private QueryHandle _changedLocalQuery;
    private QueryHandle _changedParentQuery;
    private bool _forceFullUpdate = true;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _transformQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>());
        _parentedTopologyQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<Parent<DefaultHierarchyDomain>>());
        _changedFlatLeafLocalQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .ReadWrite<WorldTransform>()
                .ChunkChanged<LocalTransform>()
                .None<Parent<DefaultHierarchyDomain>>()
                .None<Children<DefaultHierarchyDomain>>());
        _changedLocalQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>()
                .Changed<LocalTransform>()
                .Any<Parent<DefaultHierarchyDomain>>()
                .Any<Children<DefaultHierarchyDomain>>());
        _changedParentQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<Parent<DefaultHierarchyDomain>>()
                .Changed<Parent<DefaultHierarchyDomain>>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        using var scope = Profiler.BeginScope("TransformSystem.OnUpdate");
        World world = context.World;
        Hierarchy.Maintain(world);

        using (Profiler.BeginScope("TransformSystem.UpdateHierarchy"))
        {
            bool forceChildren = _forceFullUpdate;
            if (!forceChildren)
            {
                using (Profiler.BeginScope("TransformSystem.UpdateFlatLeafTransforms"))
                {
                    UpdateFlatLeafTransforms(world, context.LastSystemVersion);
                }
            }

            using (Profiler.BeginScope("TransformSystem.CollectDirtyRoots"))
            {
                if (!CollectDirtyRoots(world, context.LastSystemVersion))
                    return;
            }

            using (Profiler.BeginScope("TransformSystem.UpdateDirtyRoots"))
            {
                _updatedSet.Clear();
                for (int i = 0; i < _dirtyRootsScratch.Count; i++)
                {
                    EntityId root = _dirtyRootsScratch[i];
                    if (HasDirtyAncestor(world, root))
                        continue;

                    UpdateTransformSubtree(world, root, forceChildren);
                }
            }

            _forceFullUpdate = false;
        }
    }

    private void UpdateFlatLeafTransforms(World world, uint lastSystemVersion)
    {
        world.ExecuteQuery(
            _changedFlatLeafLocalQuery,
            lastSystemVersion,
            cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                ReadOnlySpan<LocalTransform> locals = chunk.Read<LocalTransform>();
                ReadOnlySpan<uint> localVersions = chunk.ReadWriteVersions<LocalTransform>();

                int firstUnchanged = -1;
                for (int i = 0; i < locals.Length; i++)
                {
                    if ((int)(localVersions[i] - lastSystemVersion) <= 0)
                    {
                        firstUnchanged = i;
                        break;
                    }
                }

                if (firstUnchanged < 0)
                {
                    Span<WorldTransform> worlds = chunk.ReadWrite<WorldTransform>();
                    for (int i = 0; i < locals.Length; i++)
                    {
                        TransformQvvs local = locals[i].Value;
                        worlds[i].Qvvs = local;
                    }

                    continue;
                }

                for (int i = 0; i < locals.Length; i++)
                {
                    if ((int)(localVersions[i] - lastSystemVersion) <= 0)
                        continue;

                    TransformQvvs local = locals[i].Value;
                    ref WorldTransform worldTransform = ref chunk.ReadWrite<WorldTransform>(i);
                    worldTransform.Qvvs = local;
                }
            }
        });
    }

    private bool CollectDirtyRoots(World world, uint lastSystemVersion)
    {
        _dirtyRootsScratch.Clear();
        _dirtySet.Clear();

        if (_forceFullUpdate)
        {
            CollectTraversalRoots(world);
            for (int i = 0; i < _rootsScratch.Count; i++)
                AddDirtyRoot(_rootsScratch[i]);
            RefreshKnownParents(world, markChangesDirty: false);
            return _dirtyRootsScratch.Count != 0;
        }

        CollectChanged(world, lastSystemVersion);
        CollectChangedParents(world, lastSystemVersion);
        RefreshKnownParents(world, markChangesDirty: true);
        return _dirtyRootsScratch.Count != 0;
    }

    private void CollectTraversalRoots(World world)
    {
        _rootsScratch.Clear();
        world.ExecuteQuery(_transformQuery, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                ReadOnlySpan<EntityId> entities = chunk.Entities;

                for (int i = 0; i < entities.Length; i++)
                {
                    EntityId entity = entities[i];
                    if (IsTraversalRoot(world, entity))
                        _rootsScratch.Add(entity);
                }
            }
        });
    }

    private void UpdateTransformSubtree(World world, EntityId root, bool forceChildren)
    {
        _stackScratch.Clear();
        _stackScratch.Add(root);

        while (_stackScratch.Count != 0)
        {
            int index = _stackScratch.Count - 1;
            EntityId entity = _stackScratch[index];
            _stackScratch.RemoveAt(index);

            if (!_updatedSet.Add(entity))
                continue;

            bool explicitlyDirty = _dirtySet.Remove(entity);
            if (!TryUpdateEntity(world, entity, out bool worldChanged))
            {
                // Organization entities participate in the Transform workload as
                // identity/pass-through nodes. Once an affected branch reaches one,
                // traversal must continue to its transform-bearing descendants.
                PushHierarchyChildren(world, entity);
                continue;
            }

            if (forceChildren || explicitlyDirty || worldChanged)
                PushHierarchyChildren(world, entity);
        }
    }

    private void CollectChanged(World world, uint lastSystemVersion)
    {
        world.ExecuteQuery(_changedLocalQuery, lastSystemVersion, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                foreach (int row in chunk.RowIndices)
                    AddDirtyRoot(chunk.GetEntity(row));
            }
        });
    }

    private void CollectChangedParents(World world, uint lastSystemVersion)
    {
        world.ExecuteQuery(_changedParentQuery, lastSystemVersion, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                foreach (int row in chunk.RowIndices)
                    AddDirtyRoot(chunk.GetEntity(row));
            }
        });
    }

    private void RefreshKnownParents(World world, bool markChangesDirty)
    {
        _seenParentSet.Clear();
        world.ExecuteQuery(_parentedTopologyQuery, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                ReadOnlySpan<EntityId> entities = chunk.Entities;
                for (int i = 0; i < entities.Length; i++)
                {
                    EntityId entity = entities[i];
                    EntityId parent = Hierarchy.GetParent(world, entity);
                    _seenParentSet.Add(entity);
                    RefreshKnownParent(entity, parent, markChangesDirty);
                }
            }
        });

        RemoveStaleKnownParents(markChangesDirty);
    }

    private void RefreshKnownParent(
        EntityId entity,
        EntityId parent,
        bool markChangesDirty)
    {
        if (_knownParents.TryGetValue(entity, out EntityId previousParent))
        {
            UpdateKnownParent(entity, parent, previousParent, markChangesDirty);
            return;
        }

        if (parent != EntityId.Null)
            _knownParents.Add(entity, parent);
        if (markChangesDirty)
            AddDirtyRoot(entity);
    }

    private void UpdateKnownParent(
        EntityId entity,
        EntityId parent,
        EntityId previousParent,
        bool markChangesDirty)
    {
        if (markChangesDirty && previousParent != parent)
            AddDirtyRoot(entity);
        if (parent == EntityId.Null)
            _knownParents.Remove(entity);
        else
            _knownParents[entity] = parent;
    }

    private void RemoveStaleKnownParents(bool markChangesDirty)
    {
        _staleParentScratch.Clear();
        foreach (EntityId entity in _knownParents.Keys)
        {
            if (!_seenParentSet.Contains(entity))
                _staleParentScratch.Add(entity);
        }

        for (int i = 0; i < _staleParentScratch.Count; i++)
        {
            EntityId entity = _staleParentScratch[i];
            if (markChangesDirty)
                AddDirtyRoot(entity);
            _knownParents.Remove(entity);
        }
    }

    private void AddDirtyRoot(EntityId entity)
    {
        if (_dirtySet.Add(entity))
            _dirtyRootsScratch.Add(entity);
    }

    private bool HasDirtyAncestor(World world, EntityId entity)
    {
        EntityId parent = Hierarchy.GetParent(world, entity);
        while (parent != EntityId.Null && world.IsAlive(parent))
        {
            if (_dirtySet.Contains(parent))
                return true;
            parent = Hierarchy.GetParent(world, parent);
        }

        return false;
    }

    private void PushHierarchyChildren(World world, EntityId parent)
    {
        ReadOnlySpan<EntityId> children = Hierarchy.GetChildren(world, parent).Span;
        for (int i = children.Length - 1; i >= 0; i--)
        {
            EntityId child = children[i];
            if (world.IsAlive(child))
                _stackScratch.Add(child);
        }
    }

    private static bool IsTraversalRoot(World world, EntityId entity)
    {
        EntityId parent = Hierarchy.GetParent(world, entity);
        while (parent != EntityId.Null && world.IsAlive(parent))
        {
            if (IsTransformNode(world, parent))
                return false;

            parent = Hierarchy.GetParent(world, parent);
        }

        return true;
    }

    private static bool IsTransformNode(World world, EntityId entity)
        => world.IsAlive(entity) && world.Has<LocalTransform>(entity) && world.Has<WorldTransform>(entity);

    private static bool TryUpdateEntity(World world, EntityId entity, out bool changed)
    {
        changed = false;
        if (!IsTransformNode(world, entity))
            return false;

        TransformQvvs local = world.Read<LocalTransform>(entity).Value;
        TransformQvvs worldValue = local;

        EntityId parent = Hierarchy.GetParent(world, entity);
        while (parent != EntityId.Null && world.IsAlive(parent))
        {
            if (world.Has<WorldTransform>(parent))
            {
                WorldTransform parentWorld = world.Read<WorldTransform>(parent);
                worldValue = TransformQvvs.Combine(parentWorld.Qvvs, local);
                break;
            }

            parent = Hierarchy.GetParent(world, parent);
        }

        WorldTransform current = world.Read<WorldTransform>(entity);
        if (TransformEquals(current.Qvvs, worldValue))
            return true;

        WorldTransform worldTransform = current;
        worldTransform.Qvvs = worldValue;
        world.Replace(entity, worldTransform);
        changed = true;
        return true;
    }

    private static bool TransformEquals(in TransformQvvs left, in TransformQvvs right)
        => left.Position == right.Position
            && left.Rotation == right.Rotation
            && left.Stretch == right.Stretch
            && left.Scale == right.Scale;
}

