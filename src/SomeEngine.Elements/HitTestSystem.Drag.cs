using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

public sealed partial class HitTestSystem
{
    private QueryHandle _dragQuery;
    private readonly Dictionary<Entity, Drag> _drags = new();

    private void BeginDragFrame(World world)
    {
        Collect(world, _dragQuery, _nodes);
        _drags.Clear();
        foreach (Entity node in _nodes)
        {
            Drag drag = world.Read<Drag>(node);
            drag.Active = false;
            drag.Delta = Vector2.Zero;
            _drags.Add(node, drag);
        }
    }

    private void MoveDrag(World world, ref PointerData pointer, Vector2 delta)
    {
        ButtonPress press = pointer.PrimaryPress;
        if (!press.Down || delta == Vector2.Zero || !_drags.TryGetValue(press.DragNode, out Drag drag))
            return;
        if (!press.Dragged)
        {
            drag.Total = Vector2.Zero;
            drag.Count = unchecked(drag.Count + 1);
            pointer.PrimaryPress = press with { Dragged = true };
        }
        drag.Delta += delta;
        drag.Total += delta;
        _drags[press.DragNode] = drag;
        ValueEditing.Drag(world, press.DragNode, in drag);
        AddScroll(world, press.DragNode, -delta);
    }

    private void KeepDragActive(ButtonPress press)
    {
        if (press.Down && press.Dragged && _drags.TryGetValue(press.DragNode, out Drag drag))
        {
            drag.Active = true;
            _drags[press.DragNode] = drag;
        }
    }

    private Entity FindDragTarget(World world, Entity hit)
    {
        for (Entity node = hit; node != Entity.Null; node = Hierarchy<ElementTree>.GetParent(world, node))
        {
            if (_drags.ContainsKey(node))
                return node;
            if (world.Has<Canvas>(node))
                break;
        }
        return Entity.Null;
    }

    private void CommitDrags(World world)
    {
        foreach (var (node, drag) in _drags)
        {
            Drag previous = world.Read<Drag>(node);
            if (drag.Active != previous.Active || drag.Delta != previous.Delta ||
                drag.Total != previous.Total || drag.Count != previous.Count)
                world.Replace(node, drag);
        }
    }
}
