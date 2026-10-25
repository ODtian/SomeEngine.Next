using SomeEngine.Core.ECS.Components;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Core.ECS.Systems;

public sealed class TransformSystem : ISystem<ImmediateSystemContext>
{
    private static readonly SomeEngine.Diagnostics.TraceSite OnUpdateTrace = new("ecs", "TransformSystem.OnUpdate");
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateHierarchyTrace = new("ecs", "TransformSystem.UpdateHierarchy");
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateFlatLeafTransformsTrace = new("ecs", "TransformSystem.UpdateFlatLeafTransforms");
    private static readonly SomeEngine.Diagnostics.TraceSite CollectDirtyRootsTrace = new("ecs", "TransformSystem.CollectDirtyRoots");
    private static readonly SomeEngine.Diagnostics.TraceSite UpdateDirtyRootsTrace = new("ecs", "TransformSystem.UpdateDirtyRoots");
    private readonly List<EntityId> _dirtyTransforms = [];
    private readonly List<EntityId> _staleParentScratch = [];
    private readonly HashSet<EntityId> _seenParentSet = [];
    private readonly Dictionary<EntityId, EntityId> _knownParents = [];
    private QueryHandle _transformQuery;
    private QueryHandle _parentedTopologyQuery;
    private QueryDefinition _flatTransformFilter = null!;
    private QueryHandle _changedLocalQuery;
    private QueryHandle _changedParentQuery;
    private bool _forceFullUpdate = true;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _transformQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<LocalTransform>()
                .Read<WorldTransform>()
                .Optional<Parent<DefaultHierarchyDomain>>(QueryAccess.Read)
                .Any<Parent<DefaultHierarchyDomain>>()
                .Any<Children<DefaultHierarchyDomain>>());
        _parentedTopologyQuery = world.Query(
            new QueryDefinitionBuilder()
                .Read<Parent<DefaultHierarchyDomain>>());
        _flatTransformFilter = new QueryDefinitionBuilder()
            .Changed<LocalTransform>()
            .None<Parent<DefaultHierarchyDomain>>()
            .None<Children<DefaultHierarchyDomain>>()
            .Build();
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
        using var scope = SomeEngine.Diagnostics.EngineTrace.Begin(OnUpdateTrace);
        World world = context.World;
        HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance =
            HierarchyMaintenanceSystem<DefaultHierarchyDomain>.ScheduleDependency(world);
        maintenance.Handle.Complete();

        using (SomeEngine.Diagnostics.EngineTrace.Begin(UpdateHierarchyTrace))
        {
            using (SomeEngine.Diagnostics.EngineTrace.Begin(UpdateFlatLeafTransformsTrace))
                new UpdateFlatTransformsJob().ScheduleParallel(world,
                    new JobEntityScheduleOptions(lastSystemVersion: _forceFullUpdate ? 0 : context.LastSystemVersion,
                        filter: _flatTransformFilter)).Complete();

            bool hasDirtyTransforms;
            using (SomeEngine.Diagnostics.EngineTrace.Begin(CollectDirtyRootsTrace))
                hasDirtyTransforms = CollectDirtyTransforms(world, context.LastSystemVersion);

            if (hasDirtyTransforms)
            {
                using var propagation = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateDirtyRootsTrace);
                ParallelTransformPropagation.Schedule(world, CollectionsMarshal.AsSpan(_dirtyTransforms), maintenance)
                    .Handle.Complete();
            }
            _forceFullUpdate = false;
        }
    }

    private bool CollectDirtyTransforms(World world, uint lastSystemVersion)
    {
        _dirtyTransforms.Clear();

        if (_forceFullUpdate)
        {
            CollectHierarchyTransforms(world);
            RefreshKnownParents(world, markChangesDirty: false);
            return _dirtyTransforms.Count != 0;
        }

        CollectChanged(world, lastSystemVersion);
        CollectChangedParents(world, lastSystemVersion);
        RefreshKnownParents(world, markChangesDirty: true);
        return _dirtyTransforms.Count != 0;
    }

    private void CollectHierarchyTransforms(World world)
    {
        world.ExecuteQuery(_transformQuery, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
                _dirtyTransforms.AddRange(chunk.Entities);
        });
    }

    private void CollectChanged(World world, uint lastSystemVersion)
    {
        world.ExecuteQuery(_changedLocalQuery, lastSystemVersion, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                foreach (int row in chunk.RowIndices)
                    AddDirtyTransform(chunk.GetEntity(row));
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
                    AddDirtyTransform(chunk.GetEntity(row));
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

        RemoveStaleKnownParents(world, markChangesDirty);
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
            AddDirtyTransform(entity);
    }

    private void UpdateKnownParent(
        EntityId entity,
        EntityId parent,
        EntityId previousParent,
        bool markChangesDirty)
    {
        if (markChangesDirty && previousParent != parent)
            AddDirtyTransform(entity);
        if (parent == EntityId.Null)
            _knownParents.Remove(entity);
        else
            _knownParents[entity] = parent;
    }

    private void RemoveStaleKnownParents(World world, bool markChangesDirty)
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
            if (markChangesDirty && world.IsAlive(entity))
                AddDirtyTransform(entity);
            _knownParents.Remove(entity);
        }
    }

    private void AddDirtyTransform(EntityId entity) => _dirtyTransforms.Add(entity);
}