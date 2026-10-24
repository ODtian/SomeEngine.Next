using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Job;
using SomeEngine.Typography;

namespace SomeEngine.Elements;

/// <summary>Holds reusable shape buffers and updates text for each layout measurement's content width.</summary>
public sealed class TextShapingSystem : ISystem<ImmediateSystemContext>, ILayoutDataUpdate
{
    private readonly Dictionary<Entity, TextMeasurement> _measurements = new();
    private readonly List<Entity> _removed = new();
    private readonly List<ShapeWork> _shapes = new();

    /// <summary>Shapes changed text buffers with chunk jobs. Set false for sequential execution.</summary>
    public bool Parallel { get; set; } = true;

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        foreach (var (node, measurement) in _measurements)
        {
            measurement.Updated = false;
            if (!context.World.IsAlive(node) || !context.World.Has<Text>(node))
            {
                _removed.Add(node);
                continue;
            }
            TextStyle style = context.World.Read<TextAppearance>(node).Style;
            TextInputs inputs = ReadInputs(context.World, node);
            if (measurement.HasResult && measurement.ResultInputs == inputs &&
                measurement.Style != style)
            {
                measurement.Style = style;
                context.World.Replace(node, new ShapedText(measurement, in style, measurement.Version));
            }
            foreach (TextShapeBuffer shape in measurement.Buffers)
            {
                if (shape.Used && shape.HasShape && shape.Inputs != inputs)
                    _shapes.Add(new(shape, context.World.Read<TextContent>(node).Value, style, inputs));
                shape.Used = false;
            }
        }
        foreach (Entity node in _removed)
        {
            _measurements[node].Dispose();
            _measurements.Remove(node);
            if (context.World.IsAlive(node) && context.World.Has<ShapedText>(node))
                context.World.Remove<ShapedText>(node);
        }
        _removed.Clear();
        try { ChunkJobs.Run(new ShapeJob(_shapes), _shapes.Count, Parallel); }
        finally { _shapes.Clear(); }
    }

    bool ILayoutDataUpdate.HasChanges(World world, Entity node)
    {
        if (!world.Has<Text>(node))
            return false;
        return !_measurements.TryGetValue(node, out TextMeasurement? measurement) ||
            !measurement.HasResult || measurement.ResultInputs != ReadInputs(world, node);
    }

    private static TextInputs ReadInputs(World world, Entity node)
    {
        TextStyle style = world.Read<TextAppearance>(node).Style;
        return new(world.WriteVersion<TextContent>(node), style.Font, style.Size, style.LineHeight);
    }

    void ILayoutDataUpdate.Update(World world, Entity node, in Constraints constraints)
        => ReadContent(world, node)?.Update(in constraints);

    bool ILayoutDataUpdate.SupportsParallel => true;

    ILayoutContent? ILayoutDataUpdate.ReadContent(World world, Entity node) => ReadContent(world, node);

    private TextMeasurement? ReadContent(World world, Entity node)
    {
        if (!world.Has<Text>(node))
            return null;
        if (!_measurements.TryGetValue(node, out TextMeasurement? measurement))
        {
            measurement = new TextMeasurement();
            _measurements.Add(node, measurement);
        }
        TextStyle style = world.Read<TextAppearance>(node).Style;
        measurement.Inputs = ReadInputs(world, node);
        measurement.Text = world.Read<TextContent>(node).Value;
        measurement.Style = style;
        if (!world.Has<ShapedText>(node))
            world.Add(node, new ShapedText(measurement, in style, measurement.Version));
        return measurement;
    }

    void ILayoutDataUpdate.Complete(World world)
    {
        foreach (var (node, measurement) in _measurements)
        {
            if (!measurement.Updated) continue;
            measurement.Updated = false;
            TextShapeBuffer shape = measurement.Current;
            if (measurement.HasResult && measurement.ResultWidth == shape.Width && measurement.ResultInputs == shape.Inputs)
                continue;
            measurement.ResultWidth = shape.Width;
            measurement.ResultInputs = shape.Inputs;
            measurement.HasResult = true;
            measurement.Version++;
            world.Replace(node, new ShapedText(measurement, in measurement.Style, measurement.Version));
        }
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        foreach (var (node, measurement) in _measurements)
        {
            if (context.World.IsAlive(node) && context.World.Has<ShapedText>(node))
                context.World.Remove<ShapedText>(node);
            measurement.Dispose();
        }
        _measurements.Clear();
    }

    internal sealed class TextMeasurement : IDisposable, ILayoutContent
    {
        internal readonly List<TextShapeBuffer> Buffers = new();
        internal TextShapeBuffer Current = null!;
        internal TextStyle Style;
        internal float ResultWidth;
        internal TextInputs ResultInputs;
        internal bool HasResult;
        internal uint Version;
        internal TextInputs Inputs;
        internal string Text = "";
        internal bool Updated;

        public void Update(in Constraints constraints)
        {
            TextShapeBuffer shape = GetBuffer(constraints.Max.Width);
            if (!shape.HasShape || shape.Inputs != Inputs)
            {
                Shaper.Shape(Text, new TextStyleData(Style.Font!, Style.Size, LineHeight: Style.LineHeight),
                    shape.Width, shape.Buffer);
                shape.Inputs = Inputs;
                shape.HasShape = true;
            }
            shape.Used = true;
            Current = shape;
            Updated = true;
        }

        internal TextShapeBuffer GetBuffer(float width)
        {
            foreach (TextShapeBuffer shape in Buffers)
                if (shape.Width == width)
                    return shape;
            foreach (TextShapeBuffer shape in Buffers)
            {
                if (shape.Used)
                    continue;
                shape.Width = width;
                shape.HasShape = false;
                return shape;
            }
            var added = new TextShapeBuffer { Width = width };
            Buffers.Add(added);
            return added;
        }

        public void Dispose()
        {
            foreach (TextShapeBuffer shape in Buffers)
                shape.Buffer.Dispose();
            Buffers.Clear();
        }
    }

    internal sealed class TextShapeBuffer
    {
        internal readonly ShapeBuffer Buffer = new();
        internal float Width;
        internal TextInputs Inputs;
        internal bool HasShape;
        internal bool Used;
    }

    internal readonly record struct TextInputs(uint Content, Font? Font, float Size, float LineHeight);

    private readonly record struct ShapeWork(TextShapeBuffer Shape, string Text, TextStyle Style, TextInputs Inputs);

    private readonly struct ShapeJob(List<ShapeWork> shapes) : IJobChunk
    {
        public void Execute(JobChunk chunk)
        {
            for (int index = chunk.Start; index < chunk.Start + chunk.Length; index++)
            {
                ShapeWork work = shapes[index];
                Shaper.Shape(work.Text, new TextStyleData(work.Style.Font!, work.Style.Size,
                    LineHeight: work.Style.LineHeight), work.Shape.Width, work.Shape.Buffer);
                work.Shape.Inputs = work.Inputs;
                work.Shape.HasShape = true;
            }
        }
    }
}
