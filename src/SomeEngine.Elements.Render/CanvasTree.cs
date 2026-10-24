using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements.Render;

/// <summary>The nodes, tree order and inherited clipping read during one canvas extraction.</summary>
internal sealed class CanvasTree
{
    private readonly World _world;
    private readonly Entity _canvas;
    private readonly Stack<(Entity Node, Rect Clip)> _nodes = new();
    private int _drawOrder;

    internal CanvasTree(World world, Entity canvas)
    {
        _world = world;
        _canvas = canvas;
        Canvas data = world.Read<Canvas>(canvas);
        _nodes.Push((canvas, new Rect(0, 0, data.Size.Width / data.Scale, data.Size.Height / data.Scale)));
    }

    internal static Entity FindCanvas(World world, Entity node)
    {
        while (node != Entity.Null)
        {
            if (world.Has<Canvas>(node)) return node;
            if (!world.Has<Parent<ElementTree>>(node)) return Entity.Null;
            node = world.Read<Parent<ElementTree>>(node).Value;
        }
        return Entity.Null;
    }

    internal bool TryRead(out CanvasNode value)
    {
        while (_nodes.TryPop(out var entry))
        {
            Entity node = entry.Node;
            if (node != _canvas && _world.Has<Canvas>(node)) continue;
            value = new CanvasNode(node, entry.Clip, _drawOrder++);
            Rect childClip = _world.Has<ScrollOffset>(node) && _world.Has<LayoutRect>(node)
                ? Intersect(entry.Clip, _world.Read<LayoutRect>(node).Value) : entry.Clip;
            var children = Hierarchy<ElementTree>.GetChildren(_world, node);
            for (int index = children.Count - 1; index >= 0; index--)
                _nodes.Push((children[index], childClip));
            return true;
        }
        value = default;
        return false;
    }

    internal CanvasNode ReadNode(Entity node)
    {
        while (TryRead(out CanvasNode value))
            if (value.Node == node)
                return value;
        return default;
    }

    private static Rect Intersect(Rect first, Rect second)
    {
        float left = MathF.Max(first.X, second.X);
        float top = MathF.Max(first.Y, second.Y);
        float right = MathF.Min(first.X + first.Width, second.X + second.Width);
        float bottom = MathF.Min(first.Y + first.Height, second.Y + second.Height);
        return new(left, top, MathF.Max(0, right - left), MathF.Max(0, bottom - top));
    }
}

internal readonly record struct CanvasNode(Entity Node, Rect Clip, int DrawOrder);
