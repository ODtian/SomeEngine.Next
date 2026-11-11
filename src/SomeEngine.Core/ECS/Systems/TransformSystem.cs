using SomeEngine.Core.ECS.Components;
using System.Runtime.InteropServices;
using SomeEngine.ECS;
using SomeEngine.ECS.Hierarchy;
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
    private Query _transformQuery;
    private Query _parentedTopologyQuery;
    private QueryDescription _flatTransformFilter = null!;
    private Query _changedLocalQuery;
    private Query _changedParentQuery;
    private bool _forceFullUpdate = true;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _transformQuery = world.Query(
            new QueryDescription()
                .WithStored<LocalTransform>()
                .WithStored<WorldTransform>()
                .Optional<Parent<DefaultHierarchyDomain>>()
                .WithAny<Parent<DefaultHierarchyDomain>>()
                .WithAny<Children<DefaultHierarchyDomain>>());
        _parentedTopologyQuery = world.Query(
            new QueryDescription()
                .WithStored<Parent<DefaultHierarchyDomain>>());
        _flatTransformFilter = new QueryDescription()
            .WithChanged<LocalTransform>(0)
            .WithStored<WorldTransform>()
            .Without<Parent<DefaultHierarchyDomain>>()
            .Without<Children<DefaultHierarchyDomain>>();
        _changedLocalQuery = world.Query(
            new QueryDescription()
                .WithStored<LocalTransform>()
                .WithStored<WorldTransform>()
                .WithChanged<LocalTransform>(0)
                .WithAny<Parent<DefaultHierarchyDomain>>()
                .WithAny<Children<DefaultHierarchyDomain>>());
        _changedParentQuery = world.Query(
            new QueryDescription()
                .WithStored<Parent<DefaultHierarchyDomain>>()
                .WithChanged<Parent<DefaultHierarchyDomain>>(0));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        using var scope = SomeEngine.Diagnostics.EngineTrace.Begin(OnUpdateTrace);
        World world = context.World;
        Hierarchy<DefaultHierarchyDomain>.Maintain(world);

        using (SomeEngine.Diagnostics.EngineTrace.Begin(UpdateHierarchyTrace))
        {
            using (SomeEngine.Diagnostics.EngineTrace.Begin(UpdateFlatLeafTransformsTrace))
                new UpdateFlatTransformsJob().ScheduleParallel(world,
                    query: world.Query(_flatTransformFilter).Since(_forceFullUpdate ? 0 : context.LastSystemVersion)).Complete();

            bool hasDirtyTransforms;
            using (SomeEngine.Diagnostics.EngineTrace.Begin(CollectDirtyRootsTrace))
                hasDirtyTransforms = CollectDirtyTransforms(world, context.LastSystemVersion);

            if (hasDirtyTransforms)
            {
                using var propagation = SomeEngine.Diagnostics.EngineTrace.Begin(UpdateDirtyRootsTrace);
                ParallelTransformPropagation.Schedule(world, CollectionsMarshal.AsSpan(_dirtyTransforms), default)
                    .Complete();
            }
            _forceFullUpdate = false;
        }
    }

    private bool CollectDirtyTransforms(World world, ulong lastSystemVersion)
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
        using QueryScope scope = _transformQuery.OpenRead();
        foreach (QueryChunk chunk in scope) _dirtyTransforms.AddRange(chunk.Entities);
    }

    private void CollectChanged(World world, ulong lastSystemVersion)
    {
        using QueryScope scope = _changedLocalQuery.Since(lastSystemVersion).OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (EntityId entity in chunk.Entities) AddDirtyTransform(entity);
    }

    private void CollectChangedParents(World world, ulong lastSystemVersion)
    {
        using QueryScope scope = _changedParentQuery.Since(lastSystemVersion).OpenRead();
        foreach (QueryChunk chunk in scope)
            foreach (EntityId entity in chunk.Entities) AddDirtyTransform(entity);
    }

    private void RefreshKnownParents(World world, bool markChangesDirty)
    {
        _seenParentSet.Clear();
        using (QueryScope scope = _parentedTopologyQuery.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (EntityId entity in chunk.Entities)
                {
                    EntityId parent = Hierarchy.GetParent(world, entity);
                    _seenParentSet.Add(entity);
                    RefreshKnownParent(entity, parent, markChangesDirty);
                }
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
