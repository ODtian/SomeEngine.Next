using SomeEngine.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views;

/// <summary>Builds requested roots and views with changed parameters or dependencies in the World's interface phase.</summary>
public sealed class ViewRebuildSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<Entity> _views = new();
    private readonly List<ViewDepth> _changedViews = new();
    private QueryHandle _viewQuery;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
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
                bool firstBuild = context.World.Has<BuildRequest>(entity);
                if (firstBuild)
                    context.World.RemoveTag<BuildRequest>(entity);
                if (firstBuild || HasChanges(context.World, entity))
                    ViewTypes.Build(context.World, entity);
            }
        }
        _views.Clear();
        _changedViews.Clear();
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
                bool changed = dependency.Kind switch
                {
                    DependencyKind.Presence => result.World.HasComponentData(dependency.Entity, dependency.Component) != (dependency.Version != 0),
                    DependencyKind.Membership => result.World.MembershipVersion(dependency.Query) != dependency.Version,
                    _ => result.World.ComponentDataWriteVersion(dependency.Entity, dependency.Component) != dependency.Version,
                };
                if (changed)
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
