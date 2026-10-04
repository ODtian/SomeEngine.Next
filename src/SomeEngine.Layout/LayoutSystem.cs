using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Layout;

/// <summary>Measures each canvas after view rebuilds and writes canvas-coordinate rectangles.</summary>
public sealed class LayoutSystem(params ILayoutDataUpdate[] dataUpdates) : ISystem<ImmediateSystemContext>
{
    public LayoutSystem() : this([]) { }

    private QueryHandle _canvasQuery;
    private LayoutData _data = null!;
    private readonly List<Entity> _canvases = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _canvasQuery = context.World.Query(context.World.QueryDefinition().Read<Canvas>());
        _data = new LayoutData(context.World, dataUpdates);
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
        _data.BeginUpdate();
        try
        {
            foreach (Entity entity in _canvases)
            {
                Canvas canvas = context.World.Read<Canvas>(entity);
                LayoutValidation.CheckCanvas(in canvas);
                _data.Update(entity, new Size(canvas.Size.Width / canvas.Scale, canvas.Size.Height / canvas.Scale));
            }
            _data.EndUpdate();
        }
        finally
        {
            _canvases.Clear();
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context) => context.World.ReleaseQuery(_canvasQuery);
}
