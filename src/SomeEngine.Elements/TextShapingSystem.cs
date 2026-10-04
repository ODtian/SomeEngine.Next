using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Typography;

namespace SomeEngine.Elements;

/// <summary>Holds reusable shape buffers and updates text for each layout measurement's content width.</summary>
public sealed class TextShapingSystem : ISystem<ImmediateSystemContext>, ILayoutDataUpdate
{
    private readonly Dictionary<Entity, TextMeasurement> _measurements = new();
    private readonly List<Entity> _removed = new();
    private readonly HashSet<Entity> _measured = new();

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        _measured.Clear();
        foreach (var (node, measurement) in _measurements)
        {
            if (!context.World.IsAlive(node) || !context.World.Has<Text>(node))
            {
                _removed.Add(node);
                continue;
            }
            TextStyle style = context.World.Read<TextAppearance>(node).Style;
            if (measurement.HasResult && measurement.ResultInputs == ReadInputs(context.World, node) &&
                measurement.Style != style)
            {
                measurement.Style = style;
                context.World.Replace(node, new ShapedText(measurement, in style, measurement.Version));
            }
            foreach (TextShapeBuffer shape in measurement.Buffers)
                shape.Used = false;
        }
        foreach (Entity node in _removed)
        {
            _measurements[node].Dispose();
            _measurements.Remove(node);
            if (context.World.IsAlive(node) && context.World.Has<ShapedText>(node))
                context.World.Remove<ShapedText>(node);
        }
        _removed.Clear();
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
    {
        if (!world.Has<Text>(node))
            return;
        if (!_measurements.TryGetValue(node, out TextMeasurement? measurement))
        {
            measurement = new TextMeasurement();
            _measurements.Add(node, measurement);
        }
        TextInputs inputs = ReadInputs(world, node);
        float width = constraints.Max.Width;
        TextStyle style = world.Read<TextAppearance>(node).Style;
        TextShapeBuffer shape = measurement.GetBuffer(width);
        if (!shape.HasShape || shape.Inputs != inputs)
        {
            Shaper.Shape(world.Read<TextContent>(node).Value, new TextStyleData(style.Font!, style.Size, LineHeight: style.LineHeight),
                width, shape.Buffer);
            shape.Inputs = inputs;
            shape.HasShape = true;
        }
        shape.Used = true;
        measurement.Current = shape;
        measurement.Style = style;
        _measured.Add(node);
        if (!world.Has<ShapedText>(node))
            world.Add(node, new ShapedText(measurement, in style, measurement.Version));
    }

    void ILayoutDataUpdate.Complete(World world)
    {
        foreach (Entity node in _measured)
        {
            TextMeasurement measurement = _measurements[node];
            TextShapeBuffer shape = measurement.Current;
            if (measurement.HasResult && measurement.ResultWidth == shape.Width && measurement.ResultInputs == shape.Inputs)
                continue;
            measurement.ResultWidth = shape.Width;
            measurement.ResultInputs = shape.Inputs;
            measurement.HasResult = true;
            measurement.Version++;
            world.Replace(node, new ShapedText(measurement, in measurement.Style, measurement.Version));
        }
        _measured.Clear();
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
        _measured.Clear();
    }

    internal sealed class TextMeasurement : IDisposable
    {
        internal readonly List<TextShapeBuffer> Buffers = new();
        internal TextShapeBuffer Current = null!;
        internal TextStyle Style;
        internal float ResultWidth;
        internal TextInputs ResultInputs;
        internal bool HasResult;
        internal uint Version;

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
}
