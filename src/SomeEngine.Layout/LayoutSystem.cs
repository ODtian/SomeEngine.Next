using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Layout;

/// <summary>Measures each canvas after view rebuilds and writes canvas-coordinate rectangles.</summary>
public sealed class LayoutSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _canvasQuery;
    private LayoutData _data = null!;
    private readonly List<Entity> _canvases = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _canvasQuery = context.World.Query(context.World.QueryDefinition().Read<Canvas>());
        _data = new LayoutData(context.World);
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _canvases.Clear();
        List<Entity> canvases = _canvases;
        context.World.ExecuteQuery(_canvasQuery, ref canvases, static (rows, ref entities) =>
        {
            foreach (var row in rows.Rows)
                entities.Add(row.Entity);
        });
        foreach (Entity entity in _canvases)
        {
            Canvas canvas = context.World.Read<Canvas>(entity);
            LayoutValidation.CheckCanvas(in canvas);
            if (!context.World.Has<LayoutRect>(entity))
                context.World.Add(entity, new LayoutRect());
            _data.Update(entity, new Size(canvas.Size.Width / canvas.Scale, canvas.Size.Height / canvas.Scale));
        }
        _canvases.Clear();
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_canvasQuery);
}
