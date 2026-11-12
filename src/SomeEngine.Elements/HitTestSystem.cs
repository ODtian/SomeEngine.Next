using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;

namespace SomeEngine.Elements;

/// <summary>Processes pointer input before game input and view rebuilds using the previous layout.</summary>
public sealed partial class HitTestSystem : ISystem<ImmediateSystemContext>
{
    private Query _canvasesQuery;
    private Query _hoverQuery;
    private Entity _capture;
    private readonly List<Entity> _canvases = new();
    private readonly List<Entity> _nodes = new();
    private readonly List<HitTarget> _targets = new();
    private readonly List<PointerEvent> _events = new();
    private readonly Dictionary<Entity, Hover> _hover = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _canvasesQuery = world.Query(new QueryDescription().WithStored<Canvas>().WithStored<PointerData>());
        _hoverQuery = world.Query(new QueryDescription().WithStored<Hover>());
        _dragQuery = world.Query(new QueryDescription().WithStored<Drag>());
        _scrollQuery = world.Query(new QueryDescription().WithStored<ScrollInput>());
        Query captureQuery = world.Query(new QueryDescription().WithStored<InputCapture>());
        try
        {
            Collect(world, captureQuery, _nodes);
            DevelopmentCheck.Require(_nodes.Count <= 1, "HitTestSystem requires one InputCapture per World.");
            _capture = _nodes.Count == 0 ? world.Create(new InputCapture()) : _nodes[0];
            _nodes.Clear();
        }
        finally
        {

        }
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        BeginDragFrame(world);
        BeginScrollFrame(world);
        Collect(world, _canvasesQuery, _canvases);
        Collect(world, _hoverQuery, _nodes);
        _hover.Clear();
        bool capture = false;
        foreach (Entity canvas in _canvases)
        {
            _targets.Clear();
            CollectTargets(world, canvas, Entity.Null, null);
            Pointer.TakeEvents(world, canvas, _events);
            PointerData pointer = world.ReadComponentData<PointerData>(canvas);
            pointer.PrimaryPress = RefreshPress(pointer.PrimaryPress);
            pointer.SecondaryPress = RefreshPress(pointer.SecondaryPress);
            pointer.MiddlePress = RefreshPress(pointer.MiddlePress);
            foreach (PointerEvent input in _events)
            {
                if (input.Kind == PointerEventKind.Move)
                {
                    if (pointer.HasPosition)
                        MoveDrag(world, ref pointer, input.Position - pointer.Position);
                    pointer.Position = input.Position;
                    pointer.HasPosition = true;
                }
                Entity hit = pointer.HasPosition ? Hit(pointer.Position) : Entity.Null;
                capture |= hit != Entity.Null;
                ButtonPress press = pointer.GetPress(input.Button);
                if (input.Kind == PointerEventKind.Press)
                {
                    if (!press.Down)
                    {
                        Entity drag = input.Button == PointerButton.Primary ? FindDragTarget(world, hit) : Entity.Null;
                        pointer.SetPress(input.Button, new ButtonPress(true, hit, drag));
                    }
                }
                else if (input.Kind == PointerEventKind.Release)
                {
                    capture |= press.Node != Entity.Null;
                    if (press.Down && !press.Dragged && hit != Entity.Null && hit == press.Node)
                        RouteClick(world, canvas, hit);
                    pointer.SetPress(input.Button, default);
                }
                else if (input.Kind == PointerEventKind.Wheel)
                    ScrollWheel(world, hit, input.Delta);
            }
            KeepDragActive(pointer.PrimaryPress);
            world.Set(canvas, pointer);
            Entity hovered = pointer.HasPosition ? Hit(pointer.Position) : Entity.Null;
            capture |= hovered != Entity.Null || pointer.PrimaryPress.Node != Entity.Null ||
                pointer.SecondaryPress.Node != Entity.Null || pointer.MiddlePress.Node != Entity.Null;
            MergeSubtreeHover(world, canvas, hovered);
            MergeHover(pointer.PrimaryPress.Node, hovered: false, pressed: pointer.PrimaryPress.Down);
            MergeHover(pointer.SecondaryPress.Node, hovered: false, pressed: pointer.SecondaryPress.Down);
            MergeHover(pointer.MiddlePress.Node, hovered: false, pressed: pointer.MiddlePress.Down);
        }
        CommitDrags(world);
        CommitScrollInputs(world);
        foreach (Entity node in _nodes)
        {
            Hover value = _hover.GetValueOrDefault(node);
            Hover previous = world.ReadComponentData<Hover>(node);
            if (value.Hovered != previous.Hovered || value.Pressed != previous.Pressed || value.Within != previous.Within)
                world.Set(node, value);
        }
        InputCapture inputCapture = world.ReadComponentData<InputCapture>(_capture);
        if (inputCapture.Pointer != capture)
        {
            inputCapture.Pointer = capture;
            world.Set(_capture, inputCapture);
        }
    }

    private void CollectTargets(World world, Entity node, Entity pressable, Rect? clip)
    {
        if (world.Has<Pressable>(node))
        {
            pressable = node;
            if (world.Has<LayoutRect>(node))
            {
                AddTarget(node, world.ReadComponentData<LayoutRect>(node).Value, clip);
                pressable = Entity.Null;
            }
        }
        else if (pressable != Entity.Null && world.Has<LayoutRect>(node))
            AddTarget(pressable, world.ReadComponentData<LayoutRect>(node).Value, clip);
        if (world.Has<ScrollOffset>(node) && world.Has<LayoutRect>(node))
        {
            Rect viewport = world.ReadComponentData<LayoutRect>(node).Value;
            clip = clip is { } bounds ? Intersect(viewport, bounds) : viewport;
        }
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, node);
        foreach (Entity child in children)
            if (!world.Has<Canvas>(child))
                CollectTargets(world, child, pressable, clip);
    }

    private void AddTarget(Entity node, Rect rect, Rect? clip) =>
        _targets.Add(new HitTarget(node, clip is { } bounds ? Intersect(rect, bounds) : rect));

    private static Rect Intersect(Rect rect, Rect clip)
    {
        float left = MathF.Max(rect.X, clip.X);
        float top = MathF.Max(rect.Y, clip.Y);
        return new Rect(left, top, MathF.Max(0, MathF.Min(rect.X + rect.Width, clip.X + clip.Width) - left),
            MathF.Max(0, MathF.Min(rect.Y + rect.Height, clip.Y + clip.Height) - top));
    }

    private static void RouteClick(World world, Entity canvas, Entity target)
    {
        for (Entity node = target; node != Entity.Null; node = Hierarchy<ElementTree>.GetParent(world, node))
        {
            if (world.Has<Pressable>(node))
            {
                Clicks clicks = world.ReadComponentData<Clicks>(node);
                clicks.Count = unchecked(clicks.Count + 1);
                world.Set(node, clicks);
                if (world.Has<Selected>(node))
                {
                    Selected selected = world.ReadComponentData<Selected>(node);
                    selected.Value = !selected.Value;
                    world.Set(node, selected);
                }
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

    private ButtonPress RefreshPress(ButtonPress press)
    {
        if (press.Node != Entity.Null && !HasTarget(press.Node))
            return press with { Node = Entity.Null, DragNode = Entity.Null };
        if (press.DragNode != Entity.Null && !HasTarget(press.DragNode))
            return press with { DragNode = Entity.Null };
        return press;
    }

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

    private static void Collect(World world, Query query, List<Entity> entities)
    {
        entities.Clear();
        using QueryScope range = query.OpenRead();
        foreach (QueryChunk chunk in range)
            foreach (Entity entity in chunk.Entities) entities.Add(entity);
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {




    }

    private readonly record struct HitTarget(Entity Node, Rect Rect);
}
