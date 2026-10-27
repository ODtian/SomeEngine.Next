using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Measures each canvas after view rebuilds and writes canvas-coordinate rectangles.</summary>
public sealed class LayoutSystem(params ILayoutDataUpdate[] dataUpdates) : ISystem<ImmediateSystemContext>
{
    public LayoutSystem() : this([]) { }

    private QueryHandle _canvasQuery;
    private QueryHandle _nodeReadQuery;
    private LayoutData _data = null!;
    private readonly List<Entity> _canvases = new();

    /// <summary>Measures independent canvas and boundary subtrees with jobs.</summary>
    public bool Parallel { get; set; } = true;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _canvasQuery = context.World.Query(context.World.QueryDefinition().Read<Canvas>());
        try
        {
            _nodeReadQuery = context.World.Query(context.World.QueryDefinition()
                .Optional<ViewType>(QueryAccess.Read).Optional<LayoutCache>(QueryAccess.Read));
            try { _data = new LayoutData(context.World, dataUpdates, _nodeReadQuery); }
            catch
            {
                context.World.ReleaseQuery(_nodeReadQuery);
                throw;
            }
        }
        catch
        {
            context.World.ReleaseQuery(_canvasQuery);
            throw;
        }
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
                _data.ReadCanvas(entity, new Size(canvas.Size.Width / canvas.Scale, canvas.Size.Height / canvas.Scale));
            }
            _data.Update(Parallel);
        }
        finally
        {
            _data.EndUpdate();
            _canvases.Clear();
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        try { context.World.ReleaseQuery(_nodeReadQuery); }
        finally { context.World.ReleaseQuery(_canvasQuery); }
    }
}
