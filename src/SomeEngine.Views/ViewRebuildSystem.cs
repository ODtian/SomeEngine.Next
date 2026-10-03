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
    private QueryHandle _viewQuery;
    private int _iterationLimit = 8;

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
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        for (int iteration = 0; iteration < IterationLimit; iteration++)
        {
            CollectChangedViews(world);
            if (_changedViews.Count == 0)
                return;

            foreach (ViewDepth view in _changedViews)
            {
                Entity entity = view.Entity;
                if (world.IsAlive(entity))
                {
                    bool firstBuild = world.Has<BuildRequest>(entity);
                    if (firstBuild)
                        world.RemoveTag<BuildRequest>(entity);
                    if (firstBuild || HasChanges(world, entity))
                        ViewTypes.Build(world, entity);
                }
            }
        }
        CollectChangedViews(world);
        if (_changedViews.Count > 0)
            DevelopmentCheck.Fail($"View rebuilds exceeded the per-frame iteration limit of {IterationLimit}; " +
                $"view {_changedViews[0].Entity} still has changed dependencies or parameters.");
    }

    private void CollectChangedViews(World world)
    {
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

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_viewQuery);

    private static bool HasChanges(World world, Entity entity)
    {
        if (ViewTypes.HasParameterChanges(world, entity))
            return true;
        if (!world.HasBuffer<Dependency>(entity))
            return false;

        var changes = (World: world, Found: false);
        world.ExecuteBufferRead<Dependency, (World World, bool Found)>(entity, ref changes, static (buffer, ref result) =>
        {
            foreach (Dependency dependency in buffer.AsSpan())
            {
                if (result.World.ComponentDataWriteVersion(dependency.Entity, dependency.Component) != dependency.Version)
                {
                    result.Found = true;
                    return;
                }
            }
        });
        return changes.Found;
    }

    private readonly record struct ViewDepth(Entity Entity, int Depth);
}
