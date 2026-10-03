using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Views;

namespace SomeEngine.Layout;

/// <summary>Measurements and content-relative positions for the current canvas.</summary>
internal sealed class LayoutData(World world)
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
                    node.Position += _nodes[node.ParentIndex].Position;
                _nodes[index] = node;
                World.Replace(node.Entity, new LayoutRect
                {
                    Value = new Rect(node.Position.X, node.Position.Y, node.Size.Width, node.Size.Height),
                });
            }
            World.Replace(canvas, new LayoutRect { Value = new Rect(0, 0, size.Width, size.Height) });
        }
        finally
        {
            _nodes.Clear();
            _indices.Clear();
        }
    }

    internal Size Measure(Entity node, in Constraints constraints, int parentIndex)
    {
        DevelopmentCheck.Require(!_indices.ContainsKey(node), "Measure each layout child once in its parent's measurement.");
        int index = _nodes.Count;
        _indices.Add(node, index);
        _nodes.Add(new NodeLayout { Entity = node, ParentIndex = parentIndex });
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
        Size size = ViewTypes.Measure(World, node, ref pass, in contentConstraints);
        LayoutValidation.CheckSize(size);
        NodeLayout measured = _nodes[index];
        size = contentConstraints.Constrain(size);
        measured.Size = outerConstraints.Constrain(new Size(size.Width + padding.Width, size.Height + padding.Height));
        _nodes[index] = measured;
        return measured.Size;
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
            if (ViewTypes.HasMeasure(World, child))
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
    }
}
