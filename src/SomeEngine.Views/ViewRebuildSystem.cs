using SomeEngine.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Core.Diagnostics;

namespace SomeEngine.Views;

/// <summary>Builds requested roots and views with changed parameters or dependencies in the World's interface phase.</summary>
public sealed class ViewRebuildSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<Entity> _views = new();
    private readonly List<ViewDepth> _changedViews = new();
    private readonly List<Computation> _computations = new();
    private readonly DependencyChanges _dependencyChanges = new();
    private QueryHandle _viewQuery;
    private ulong _frame;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _dependencyChanges.Clear();
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _frame++;
#if DEBUG
        using var rebuild = Scope.EnterRebuild();
#endif
        _dependencyChanges.Record(context.World, _frame);
        _views.Clear();
        _changedViews.Clear();
        List<Entity> views = _views;
        context.World.ExecuteQuery(_viewQuery, ref views, static (rows, ref viewEntities) =>
        {
            foreach (var row in rows.Rows)
                viewEntities.Add(row.Entity);
        });

        foreach (Entity entity in _views)
        {
            if (context.World.Has<BuildRequest>(entity) || HasChanges(context.World, entity))
            {
                int depth = 0;
                Entity ancestor = Hierarchy<ElementTree>.GetParent(context.World, entity);
                while (ancestor != Entity.Null)
                {
                    depth++;
                    ancestor = Hierarchy<ElementTree>.GetParent(context.World, ancestor);
                }
                _changedViews.Add(new ViewDepth(entity, depth));
            }
        }
        _changedViews.Sort(static (left, right) => left.Depth.CompareTo(right.Depth));

        foreach (ViewDepth view in _changedViews)
        {
            Entity entity = view.Entity;
            if (context.World.IsAlive(entity))
            {
                if (context.World.Has<BuildRequest>(entity) || HasBuildChanges(context.World, entity, recheck: true))
                    ViewTypes.Build(context.World, entity, _frame);
                if (context.World.IsAlive(entity) && context.World.HasSparse<Computations>(entity))
                    RebuildComputations(context.World, entity);
            }
        }
        _views.Clear();
        _changedViews.Clear();
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_viewQuery);

    private bool HasChanges(World world, Entity entity)
    {
        if (HasBuildChanges(world, entity))
            return true;
        if (world.HasSparse<Computations>(entity))
        {
            CollectComputations(world, entity);
            bool changed = false;
            foreach (Computation computation in _computations)
                if (!computation.Released && !computation.EvaluatesOnRead &&
                    computation.HasChanges(_dependencyChanges))
                {
                    changed = true;
                    break;
                }
            _computations.Clear();
            if (changed)
                return true;
        }
        return false;
    }

    private bool HasBuildChanges(World world, Entity entity, bool recheck = false) =>
        ViewTypes.HasParameterChanges(world, entity) || _dependencyChanges.HasChanges(world, entity, recheck);

    private void CollectComputations(World world, Entity entity)
    {
        _computations.Clear();
        _computations.AddRange(world.ReadSparse<Computations>(entity).List.Items);
    }

    private void RebuildComputations(World world, Entity entity)
    {
        for (int pass = 0; pass < 64; pass++)
        {
            CollectComputations(world, entity);
            bool changed = false;
            foreach (Computation computation in _computations)
            {
                if (!world.IsAlive(entity))
                    break;
                if (computation.Released || computation.EvaluatesOnRead ||
                    !computation.HasChanges(_dependencyChanges, recheck: true))
                    continue;
                changed = true;
                computation.Rebuild(_frame);
            }
            _computations.Clear();
            if (!changed || !world.IsAlive(entity))
                return;
        }
        DevelopmentCheck.Report($"Calculations on {entity} reached the rebuild iteration limit in frame {_frame}.");
    }

    private readonly record struct ViewDepth(Entity Entity, int Depth);
}
