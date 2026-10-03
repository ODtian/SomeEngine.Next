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
    private readonly List<ushort> _dependencyTypes = new();
    private ComponentChange[] _componentChanges = new ComponentChange[16];
    private QueryHandle _viewQuery;
    private ulong _frame;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _dependencyTypes.Clear();
        Array.Clear(_componentChanges);
        _viewQuery = context.World.Query(context.World.QueryDefinition().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        RebuildDiagnostics.BeginFrame(context.World);
        _frame++;
#if DEBUG
        using var rebuild = Scope.EnterRebuild();
#endif
        RecordComponentChanges(context.World);
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
                if (firstBuild || HasChanges(context.World, entity, recheck: true))
                    ViewTypes.Build(context.World, entity, _frame, firstBuild);
            }
        }
        _views.Clear();
        _changedViews.Clear();
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_viewQuery);

    private void RecordComponentChanges(World world)
    {
        foreach (ushort component in _dependencyTypes)
        {
            ref ComponentChange change = ref _componentChanges[component];
            uint version = world.ComponentWriteVersion(component);
            change.Changed = version != change.Version;
            change.Version = version;
        }
    }

    private bool HasComponentChanges(World world, ushort component, bool recheck)
    {
        if (component >= _componentChanges.Length)
            Array.Resize(ref _componentChanges, Math.Max(component + 1, _componentChanges.Length * 2));
        ref ComponentChange change = ref _componentChanges[component];
        if (!change.Recorded)
        {
            change.Version = world.ComponentWriteVersion(component);
            change.Changed = true;
            change.Recorded = true;
            _dependencyTypes.Add(component);
        }
        // Builds can write a type after its version was recorded at the start of the frame.
        return change.Changed || (recheck && world.ComponentWriteVersion(component) != change.Version);
    }

    private bool HasChanges(World world, Entity entity, bool recheck = false)
    {
        if (ViewTypes.HasParameterChanges(world, entity))
            return true;
        if (!world.HasBuffer<Dependency>(entity))
            return false;

        var changes = (System: this, World: world, Recheck: recheck, Found: false);
        world.ExecuteBufferRead<Dependency,
            (ViewRebuildSystem System, World World, bool Recheck, bool Found)>(
            entity, ref changes, static (buffer, ref result) =>
        {
            foreach (Dependency dependency in buffer.AsSpan())
            {
                bool changed = dependency.Kind switch
                {
                    DependencyKind.Presence => result.World.HasComponentData(dependency.Entity, dependency.Component) != (dependency.Version != 0),
                    DependencyKind.Membership => result.World.MembershipVersion(dependency.Query) != dependency.Version,
                    _ => result.System.HasComponentChanges(result.World, dependency.Component, result.Recheck) &&
                        result.World.ComponentDataWriteVersion(dependency.Entity, dependency.Component) != dependency.Version,
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

    private struct ComponentChange
    {
        internal uint Version;
        internal bool Changed;
        internal bool Recorded;
    }
}
