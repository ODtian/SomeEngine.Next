using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Measurements and content-relative positions for the current canvas.</summary>
internal sealed class LayoutData(World world, ILayoutDataUpdate[] dataUpdates)
{
    internal World World { get; } = world;
    private readonly List<NodeLayout> _nodes = new();
    private readonly Dictionary<Entity, int> _indices = new();

    internal void Update(Entity canvas, Size size)
    {
        try
        {
            Constraints constraints = Constraints.Fixed(size);
            foreach (Entity child in Children(canvas))
                Measure(child, in constraints, -1);

            // Parent measurement already placed its children; resolve those offsets in tree order.
            for (int index = 0; index < _nodes.Count; index++)
            {
                NodeLayout node = _nodes[index];
                if (node.ParentIndex >= 0)
                {
                    node.Position += _nodes[node.ParentIndex].Position;
                    Entity parent = _nodes[node.ParentIndex].Entity;
                    if (World.Has<ScrollOffset>(parent))
                        node.Position -= World.Read<ScrollOffset>(parent).Value;
                }
                if (World.Has<ScrollOffset>(node.Entity))
                    UpdateScroll(in node);
                _nodes[index] = node;
                WriteRect(node.Entity, new Rect(node.Position.X, node.Position.Y, node.Size.Width, node.Size.Height));
            }
            WriteRect(canvas, new Rect(0, 0, size.Width, size.Height));
            foreach (ILayoutDataUpdate update in dataUpdates)
                update.Complete(World);
        }
        finally
        {
            _nodes.Clear();
            _indices.Clear();
        }
    }

    private void WriteRect(Entity node, Rect rectangle)
    {
        if (World.Read<LayoutRect>(node).Value != rectangle)
            World.Replace(node, new LayoutRect { Value = rectangle });
    }

    internal Size Measure(Entity node, in Constraints constraints, int parentIndex)
    {
        // Flex measures a basis and then final constraints; both update the same node entry.
        if (!_indices.TryGetValue(node, out int index))
        {
            index = _nodes.Count;
            _indices.Add(node, index);
            _nodes.Add(new NodeLayout { Entity = node, ParentIndex = parentIndex });
        }
        DevelopmentCheck.Require(_nodes[index].ParentIndex == parentIndex, "Measure requires this layout's child.");
        if (!World.Has<LayoutRect>(node))
            World.Add(node, new LayoutRect());
        Box box = World.Has<Box>(node) ? World.Read<Box>(node) : default;
        LayoutValidation.CheckBox(in box);
        Size fixedSize = box.Fixed;
        Size minimum = new(
            Math.Clamp(box.Min.Width, constraints.Min.Width, constraints.Max.Width),
            Math.Clamp(box.Min.Height, constraints.Min.Height, constraints.Max.Height));
        Size maximum = new(
            Math.Clamp(box.Max.Width, minimum.Width, constraints.Max.Width),
            Math.Clamp(box.Max.Height, minimum.Height, constraints.Max.Height));
        if (!float.IsNaN(fixedSize.Width))
            minimum = minimum with { Width = Math.Clamp(fixedSize.Width, minimum.Width, maximum.Width) };
        if (!float.IsNaN(fixedSize.Height))
            minimum = minimum with { Height = Math.Clamp(fixedSize.Height, minimum.Height, maximum.Height) };
        if (!float.IsNaN(fixedSize.Width))
            maximum = maximum with { Width = minimum.Width };
        if (!float.IsNaN(fixedSize.Height))
            maximum = maximum with { Height = minimum.Height };
        var outerConstraints = new Constraints(minimum, maximum);
        Size padding = new(box.Padding.Left + box.Padding.Right, box.Padding.Top + box.Padding.Bottom);
        var contentConstraints = new Constraints(
            new Size(MathF.Max(0, minimum.Width - padding.Width), MathF.Max(0, minimum.Height - padding.Height)),
            new Size(MathF.Max(0, maximum.Width - padding.Width), MathF.Max(0, maximum.Height - padding.Height)));
        var pass = new LayoutPass(this, node, index, Children(node), new Vector2(box.Padding.Left, box.Padding.Top));
        Constraints measuredConstraints = World.Has<ScrollOffset>(node)
            ? new Constraints(default, Size.Unbounded) : contentConstraints;
        foreach (ILayoutDataUpdate update in dataUpdates)
            update.Update(World, node, in measuredConstraints);
        Size size = ViewTypes.HasMeasure(World, node)
            ? ViewTypes.Measure(World, node, ref pass, in measuredConstraints)
            : MeasureScrollContent(ref pass, in measuredConstraints);
        LayoutValidation.CheckSize(size);
        NodeLayout measured = _nodes[index];
        measured.ContentSize = size;
        size = contentConstraints.Constrain(size);
        measured.Size = outerConstraints.Constrain(new Size(size.Width + padding.Width, size.Height + padding.Height));
        _nodes[index] = measured;
        return measured.Size;
    }

    private void UpdateScroll(in NodeLayout node)
    {
        ScrollOffset previous = World.Read<ScrollOffset>(node.Entity);
        ScrollOffset offset = previous;
        Box box = World.Has<Box>(node.Entity) ? World.Read<Box>(node.Entity) : default;
        Vector2 viewport = new(MathF.Max(0, node.Size.Width - box.Padding.Left - box.Padding.Right),
            MathF.Max(0, node.Size.Height - box.Padding.Top - box.Padding.Bottom));
        offset.Max = Vector2.Max(Vector2.Zero, new Vector2(node.ContentSize.Width, node.ContentSize.Height) - viewport);
        if (World.Has<ScrollInput>(node.Entity))
        {
            ScrollInput input = World.Read<ScrollInput>(node.Entity);
            if (input.Count != offset.InputCount)
            {
                offset.Value += input.Delta;
                offset.InputCount = input.Count;
            }
        }
        offset.Value = Vector2.Clamp(offset.Value, Vector2.Zero, offset.Max);
        if (offset.Value != previous.Value || offset.Max != previous.Max || offset.InputCount != previous.InputCount)
            World.Replace(node.Entity, offset);
    }

    private static Size MeasureScrollContent(ref LayoutPass pass, in Constraints constraints)
    {
        Size extent = default;
        foreach (Entity child in pass.Children)
        {
            Size size = pass.Measure(child, in constraints);
            pass.Place(child, Vector2.Zero);
            extent = new Size(MathF.Max(extent.Width, size.Width), MathF.Max(extent.Height, size.Height));
        }
        return extent;
    }

    internal void Place(Entity child, Vector2 position, int parentIndex)
    {
        DevelopmentCheck.Require(_indices.ContainsKey(child), "Place requires a measured child.");
        int index = _indices[child];
        NodeLayout node = _nodes[index];
        DevelopmentCheck.Require(node.ParentIndex == parentIndex, "Place requires this layout's child.");
        node.Position = position;
        _nodes[index] = node;
    }

    private LayoutChildren Children(Entity node)
    {
        var children = new List<Entity>();
        CollectChildren(node, children);
        return new LayoutChildren(children);
    }

    private void CollectChildren(Entity node, List<Entity> children)
    {
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(World, node))
        {
            if (World.Has<Canvas>(child))
                continue;
            if (ViewTypes.HasMeasure(World, child) || World.Has<ScrollOffset>(child))
                children.Add(child);
            else
                CollectChildren(child, children);
        }
    }

    private struct NodeLayout
    {
        internal Entity Entity;
        internal int ParentIndex;
        internal Vector2 Position;
        internal Size Size;
        internal Size ContentSize;
    }
}
