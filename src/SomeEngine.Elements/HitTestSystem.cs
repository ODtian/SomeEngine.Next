using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Processes pointer input before game input and view rebuilds using the previous layout.</summary>
public sealed class HitTestSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _canvasesQuery;
    private QueryHandle _hoverQuery;
    private Entity _capture;
    private readonly List<Entity> _canvases = new();
    private readonly List<Entity> _nodes = new();
    private readonly List<HitTarget> _targets = new();
    private readonly List<PointerEvent> _events = new();
    private readonly Dictionary<Entity, Hover> _hover = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _canvasesQuery = world.Query(world.QueryDefinition().All<Canvas>().All<PointerData>());
        _hoverQuery = world.Query(world.QueryDefinition().All<Hover>());
        QueryHandle captureQuery = world.Query(world.QueryDefinition().All<InputCapture>());
        try
        {
            Collect(world, captureQuery, _nodes);
            DevelopmentCheck.Require(_nodes.Count <= 1, "HitTestSystem requires one InputCapture per World.");
            _capture = _nodes.Count == 0 ? world.CreateEntity(new InputCapture()) : _nodes[0];
            _nodes.Clear();
        }
        finally
        {
            world.ReleaseQuery(captureQuery);
        }
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        Collect(world, _canvasesQuery, _canvases);
        Collect(world, _hoverQuery, _nodes);
        _hover.Clear();
        bool capture = false;
        foreach (Entity canvas in _canvases)
        {
            _targets.Clear();
            CollectTargets(world, canvas, Entity.Null);
            Pointer.TakeEvents(world, canvas, _events);
            PointerData pointer = world.Read<PointerData>(canvas);
            pointer.PrimaryPress = RefreshPress(pointer.PrimaryPress);
            pointer.SecondaryPress = RefreshPress(pointer.SecondaryPress);
            pointer.MiddlePress = RefreshPress(pointer.MiddlePress);
            foreach (PointerEvent input in _events)
            {
                if (input.Kind == PointerEventKind.Move)
                {
                    pointer.Position = input.Position;
                    pointer.HasPosition = true;
                }
                Entity hit = pointer.HasPosition ? Hit(pointer.Position) : Entity.Null;
                capture |= hit != Entity.Null;
                ButtonPress press = pointer.GetPress(input.Button);
                if (input.Kind == PointerEventKind.Press)
                {
                    if (!press.Down)
                        pointer.SetPress(input.Button, new ButtonPress(true, hit));
                }
                else if (input.Kind == PointerEventKind.Release)
                {
                    capture |= press.Node != Entity.Null;
                    if (press.Down && hit != Entity.Null && hit == press.Node)
                        RouteClick(world, canvas, hit);
                    pointer.SetPress(input.Button, default);
                }
            }
            world.Replace(canvas, pointer);
            Entity hovered = pointer.HasPosition ? Hit(pointer.Position) : Entity.Null;
            capture |= hovered != Entity.Null || pointer.PrimaryPress.Node != Entity.Null ||
                pointer.SecondaryPress.Node != Entity.Null || pointer.MiddlePress.Node != Entity.Null;
            MergeSubtreeHover(world, canvas, hovered);
            MergeHover(pointer.PrimaryPress.Node, hovered: false, pressed: pointer.PrimaryPress.Down);
            MergeHover(pointer.SecondaryPress.Node, hovered: false, pressed: pointer.SecondaryPress.Down);
            MergeHover(pointer.MiddlePress.Node, hovered: false, pressed: pointer.MiddlePress.Down);
        }
        foreach (Entity node in _nodes)
        {
            Hover value = _hover.GetValueOrDefault(node);
            Hover previous = world.Read<Hover>(node);
            if (value.Hovered != previous.Hovered || value.Pressed != previous.Pressed || value.Within != previous.Within)
                world.Replace(node, value);
        }
        InputCapture inputCapture = world.Read<InputCapture>(_capture);
        if (inputCapture.Pointer != capture)
        {
            inputCapture.Pointer = capture;
            world.Replace(_capture, inputCapture);
        }
    }

    private void CollectTargets(World world, Entity node, Entity pressable)
    {
        if (world.Has<Pressable>(node))
        {
            pressable = node;
            if (world.Has<LayoutRect>(node))
            {
                _targets.Add(new HitTarget(node, world.Read<LayoutRect>(node).Value));
                pressable = Entity.Null;
            }
        }
        else if (pressable != Entity.Null && world.Has<LayoutRect>(node))
            _targets.Add(new HitTarget(pressable, world.Read<LayoutRect>(node).Value));
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, node))
            if (!world.Has<Canvas>(child))
                CollectTargets(world, child, pressable);
    }

    private static void RouteClick(World world, Entity canvas, Entity target)
    {
        for (Entity node = target; node != Entity.Null; node = Hierarchy<ElementTree>.GetParent(world, node))
        {
            if (world.Has<Pressable>(node))
            {
                Clicks clicks = world.Read<Clicks>(node);
                clicks.Count = unchecked(clicks.Count + 1);
                world.Replace(node, clicks);
            }
            if (node == canvas || world.Has<StopsPointer>(node))
                break;
        }
    }

    private Entity Hit(Vector2 position)
    {
        for (int index = _targets.Count - 1; index >= 0; index--)
        {
            HitTarget target = _targets[index];
            Rect rect = target.Rect;
            if (position.X >= rect.X && position.X < rect.X + rect.Width &&
                position.Y >= rect.Y && position.Y < rect.Y + rect.Height)
                return target.Node;
        }
        return Entity.Null;
    }

    private bool HasTarget(Entity node)
    {
        foreach (HitTarget target in _targets)
            if (target.Node == node)
                return true;
        return false;
    }

    private ButtonPress RefreshPress(ButtonPress press) => press.Node != Entity.Null && !HasTarget(press.Node)
        ? press with { Node = Entity.Null }
        : press;

    private void MergeSubtreeHover(World world, Entity canvas, Entity target)
    {
        for (Entity node = target; node != Entity.Null; node = Hierarchy<ElementTree>.GetParent(world, node))
        {
            if (world.Has<Hover>(node))
                MergeHover(node, hovered: node == target, pressed: false, within: true);
            if (node == canvas)
                break;
        }
    }

    private void MergeHover(Entity node, bool hovered, bool pressed, bool within = false)
    {
        if (node == Entity.Null)
            return;
        Hover value = _hover.GetValueOrDefault(node);
        value.Hovered |= hovered;
        value.Pressed |= pressed;
        value.Within |= within;
        _hover[node] = value;
    }

    private static void Collect(World world, QueryHandle query, List<Entity> entities)
    {
        entities.Clear();
        world.ExecuteQuery(query, ref entities, static (rows, ref values) =>
        {
            foreach (var row in rows.Rows)
                values.Add(row.Entity);
        });
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.ReleaseQuery(_canvasesQuery);
        context.World.ReleaseQuery(_hoverQuery);
    }

    private readonly record struct HitTarget(Entity Node, Rect Rect);
}
