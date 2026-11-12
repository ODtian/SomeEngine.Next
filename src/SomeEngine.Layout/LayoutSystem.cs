using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Layout;

/// <summary>Measures each canvas after view rebuilds and writes canvas-coordinate rectangles.</summary>
public sealed class LayoutSystem(params ILayoutDataUpdate[] dataUpdates) : ISystem<ImmediateSystemContext>
{
    public LayoutSystem() : this([]) { }

    private Query _canvasQuery;
    private LayoutData _data = null!;
    private readonly List<Entity> _canvases = new();

    /// <summary>Measures independent canvas and boundary subtrees with jobs.</summary>
    public bool Parallel { get; set; } = true;

    public void OnCreate(ref ImmediateSystemContext context)
    {
        _canvasQuery = context.World.Query(new QueryDescription().WithStored<Canvas>());
        _data = new LayoutData(context.World, dataUpdates);
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _canvases.Clear();
        using (QueryScope canvases = _canvasQuery.OpenRead())
            foreach (QueryChunk chunk in canvases)
                foreach (Entity entity in chunk.Entities) _canvases.Add(entity);
        _data.BeginUpdate();
        try
        {
            foreach (Entity entity in _canvases)
            {
                Canvas canvas = context.World.ReadComponentData<Canvas>(entity);
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

    public void OnDestroy(ref ImmediateSystemContext context) { }
}
