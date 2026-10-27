using SomeEngine.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>Runs requested view builds and changed calculations in the World's interface phase.</summary>
public sealed class ViewRebuildSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<ViewRead> _views = new();
    private readonly List<Dependency> _viewDependencies = new();
    private readonly Dictionary<Entity, ComputationList> _viewComputations = new();
    private readonly List<ViewDepth> _changedViews = new();
    private readonly List<Computation> _computations = new();
    private readonly DependencyChanges _dependencyChanges = new();
    private QueryHandle _viewQuery;
    private ulong _frame;
    private int _iterationLimit = 8;
    private BuildQueue _builds = null!;

    /// <summary>View Build entries, including synchronous children, during the latest rebuild phase.</summary>
    public int BuildCount => _builds is null ? 0 : _builds.BuildCount;

    /// <summary>Reactive calculation entries during the latest rebuild phase.</summary>
    public int ComputationCount => _builds is null ? 0 : _builds.ComputationCount;

    /// <summary>The maximum number of rebuild rounds in one interface frame.</summary>
    public int IterationLimit
    {
        get => _iterationLimit;
        set
        {
            DevelopmentCheck.Require(value > 0, "The view rebuild iteration limit requires a positive value.");
            _iterationLimit = value;
        }
    }

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _builds = BuildQueue.For(context.World);
        _builds.Open();
        _dependencyChanges.Clear();
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>()
            .Optional<BuildRequest>().Optional<ObjectDependencies>(QueryAccess.Read)
            .OptionalBuffer<Dependency>(QueryAccess.Read));
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _builds.BeginFrame();
        RebuildDiagnostics.BeginFrame(context.World);
        _frame++;
#if DEBUG
        using var rebuild = Scope.EnterRebuild();
#endif
        World world = context.World;
        _dependencyChanges.Record(world, _frame);
        _builds.CancelChanged(world, _dependencyChanges);
        _builds.Resume(world, _frame);
        for (int iteration = 0; iteration < IterationLimit; iteration++)
        {
            CollectChangedViews(world);
            if (_changedViews.Count == 0)
                return;

            foreach (ViewDepth view in _changedViews)
            {
                Entity entity = view.Entity;
                if (!world.IsAlive(entity))
                    continue;
                if (world.Has<BuildRequest>(entity) || HasBuildChanges(world, entity, recheck: true))
                    ViewTypes.Build(world, entity, _frame);
                if (world.IsAlive(entity) && world.HasSparse<Computations>(entity))
                    RebuildComputations(world, entity);
            }
        }
        CollectChangedViews(world);
        if (_changedViews.Count > 0)
            DevelopmentCheck.Fail($"View calculations exceeded the per-frame iteration limit of {IterationLimit}; " +
                $"view {_changedViews[0].Entity} still has changed dependencies or parameters.");
    }

    private void CollectChangedViews(World world)
    {
        _dependencyChanges.Record(world, _frame);
        _views.Clear();
        _viewDependencies.Clear();
        _viewComputations.Clear();
        _changedViews.Clear();
        var rebuild = (System: this, World: world);
        world.ExecuteQuery(_viewQuery, ref rebuild, static (query, ref state) =>
        {
            ViewRebuildSystem system = state.System;
            foreach (var chunk in query.Chunks)
            {
                var entities = chunk.Entities;
                var types = chunk.Read<ViewType>();
                bool requested = chunk.Has<BuildRequest>();
                bool hasObjects = chunk.TryRead<ObjectDependencies>(out var objects);
                bool hasDependencies = chunk.HasBuffer<Dependency>();
                for (int index = 0; index < entities.Length; index++)
                {
                    int firstDependency = system._viewDependencies.Count;
                    if (hasDependencies)
                        foreach (Dependency read in chunk.ReadBuffer<Dependency>(index).AsSpan())
                            if (read.Kind != DependencyKind.Component ||
                                system._dependencyChanges.HasComponentChanges(state.World, read.Component, recheck: false))
                                system._viewDependencies.Add(read);
                    system._views.Add(new ViewRead(entities[index], types[index], requested,
                        hasObjects ? objects[index].Reads : null, firstDependency,
                        system._viewDependencies.Count - firstDependency));
                }
            }
        });
        Dictionary<Entity, ComputationList> viewComputations = _viewComputations;
        world.ExecuteSparseRead<Computations, Dictionary<Entity, ComputationList>>(ref viewComputations,
            static (entities, values, ref computations) =>
            {
                for (int index = 0; index < entities.Length; index++)
                    computations.Add(entities[index], values[index].List);
            });

        // Object sources can run calculations; refresh them after the source borrows have ended.
        foreach (ViewRead view in _views)
        {
            Entity entity = view.Entity;
            if (view.Requested || HasChanges(world, view))
            {
                int depth = 0;
                Entity ancestor = Hierarchy<ElementTree>.GetParent(world, entity);
                while (ancestor != Entity.Null)
                {
                    depth++;
                    ancestor = Hierarchy<ElementTree>.GetParent(world, ancestor);
                }
                _changedViews.Add(new ViewDepth(entity, depth));
            }
        }
        _views.Clear();
        _viewDependencies.Clear();
        _viewComputations.Clear();
        _changedViews.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _builds.Close(context.World, _frame);
        context.World.ReleaseQuery(_viewQuery);
    }

    private bool HasChanges(World world, ViewRead view)
    {
        Entity entity = view.Entity;
        if (view.Type.RebuildRequired || _builds.HasParameterChanges(world, entity, view.Type.Id, _dependencyChanges) ||
            view.ObjectReads?.HasChanges(_frame) == true)
            return true;
        for (int index = view.FirstDependency; index < view.FirstDependency + view.DependencyCount; index++)
            if (_dependencyChanges.HasChanges(world, entity, _viewDependencies[index], recheck: false))
                return true;
        if (_viewComputations.TryGetValue(entity, out ComputationList? computations))
        {
            _computations.Clear();
            _computations.AddRange(computations.Items);
            bool changed = false;
            foreach (Computation computation in _computations)
            {
                if (!computation.Released && !computation.EvaluatesOnRead &&
                    computation.HasChanges(_dependencyChanges))
                {
                    changed = true;
                    break;
                }
            }
            _computations.Clear();
            return changed;
        }
        return false;
    }

    private bool HasBuildChanges(World world, Entity entity, bool recheck = false)
    {
        ViewType view = world.Read<ViewType>(entity);
        return view.RebuildRequired || _builds.HasParameterChanges(world, entity, view.Id, _dependencyChanges) ||
            _dependencyChanges.HasChanges(world, entity, recheck);
    }

    private void CollectComputations(World world, Entity entity)
    {
        _computations.Clear();
        _computations.AddRange(world.ReadSparse<Computations>(entity).List.Items);
    }

    private void RebuildComputations(World world, Entity entity)
    {
        CollectComputations(world, entity);
        foreach (Computation computation in _computations)
        {
            if (!world.IsAlive(entity))
                break;
            if (!computation.Released && !computation.EvaluatesOnRead &&
                computation.HasChanges(_dependencyChanges, recheck: true))
                computation.Rebuild(_frame);
        }
        _computations.Clear();
    }

    private readonly record struct ViewDepth(Entity Entity, int Depth);
    private readonly record struct ViewRead(Entity Entity, ViewType Type, bool Requested,
        ObjectDependencyList? ObjectReads, int FirstDependency, int DependencyCount);
}
