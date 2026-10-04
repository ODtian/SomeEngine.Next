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
    private readonly List<Entity> _views = new();
    private readonly List<ViewDepth> _changedViews = new();
    private readonly List<Computation> _computations = new();
    private readonly DependencyChanges _dependencyChanges = new();
    private QueryHandle _viewQuery;
    private ulong _frame;
    private int _iterationLimit = 8;
    private BuildQueue _builds = null!;

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
        _dependencyChanges.Clear();
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        RebuildDiagnostics.BeginFrame(context.World);
        _frame++;
#if DEBUG
        using var rebuild = Scope.EnterRebuild();
#endif
        World world = context.World;
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
                if (!_builds.Contains(entity) &&
                    (world.Has<BuildRequest>(entity) || HasBuildChanges(world, entity, recheck: true)))
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
        _changedViews.Clear();
        List<Entity> views = _views;
        world.ExecuteQuery(_viewQuery, ref views, static (rows, ref viewEntities) =>
        {
            foreach (var row in rows.Rows)
                viewEntities.Add(row.Entity);
        });

        foreach (Entity entity in _views)
        {
            if (world.Has<BuildRequest>(entity) || HasChanges(world, entity))
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
        _changedViews.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        _builds.Close(context.World);
        context.World.ReleaseQuery(_viewQuery);
    }

    private bool HasChanges(World world, Entity entity)
    {
        if (!_builds.Contains(entity) && HasBuildChanges(world, entity))
            return true;
        if (world.HasSparse<Computations>(entity))
        {
            CollectComputations(world, entity);
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

    private bool HasBuildChanges(World world, Entity entity, bool recheck = false) =>
        world.Read<ViewType>(entity).RebuildRequired || ViewTypes.HasParameterChanges(world, entity) ||
        _dependencyChanges.HasChanges(world, entity, recheck);

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
}
