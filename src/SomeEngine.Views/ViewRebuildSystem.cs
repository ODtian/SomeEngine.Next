using SomeEngine.ECS.Systems;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Views;

/// <summary>Builds requested root views in the current World's interface phase.</summary>
public sealed class ViewRebuildSystem : ISystem<ImmediateSystemContext>
{
    private readonly List<Entity> _roots = new();
    private QueryHandle _buildRequests;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _buildRequests = context.World.Query(context.World.QueryDefinition().All<BuildRequest>().Read<ViewType>());
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _roots.Clear();
        List<Entity> roots = _roots;
        context.World.ExecuteQuery(_buildRequests, ref roots, static (rows, ref requestedRoots) =>
        {
            foreach (var row in rows.Rows)
                requestedRoots.Add(row.Entity);
        });

        foreach (Entity root in _roots)
        {
            if (context.World.IsAlive(root))
            {
                context.World.RemoveTag<BuildRequest>(root);
                ViewTypes.Build(context.World, root);
            }
        }
        _roots.Clear();
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_buildRequests);
}
