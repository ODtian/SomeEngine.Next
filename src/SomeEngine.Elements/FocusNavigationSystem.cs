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

/// <summary>Processes keyboard and gamepad input before game input and view rebuilds.</summary>
public sealed class FocusNavigationSystem : ISystem<ImmediateSystemContext>
{
    private QueryHandle _focusableQuery;
    private Entity _input;
    private bool _shiftPressed;
    private readonly List<Entity> _nodes = new();
    private readonly List<FocusTarget> _targets = new();
    private readonly List<FocusInput> _events = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _input = FocusInputData.GetInput(world);
        _focusableQuery = world.Query(world.QueryDefinition().All<Focusable>());
        world.EntityDestroying += OnEntityDestroying;
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _nodes.Clear();
        List<Entity> nodes = _nodes;
        world.ExecuteQuery(_focusableQuery, ref nodes, static (rows, ref values) =>
        {
            foreach (var row in rows.Rows)
                values.Add(row.Entity);
        });
        _targets.Clear();
        foreach (Entity node in _nodes)
            if (TryGetRectangle(world, node, out Rect rect))
                _targets.Add(new FocusTarget(node, rect, FindCanvas(world, node)));
        _events.Clear();
        List<FocusInput> events = _events;
        world.ExecuteBufferWrite<FocusInput, List<FocusInput>>(_input, ref events, static (buffer, ref values) =>
        {
            foreach (FocusInput input in buffer.ReadSpan())
                values.Add(input);
            buffer.Clear();
        });
        Entity focused = ReadFocus(world);
        if (focused != Entity.Null && !world.Has<Focusable>(focused))
            focused = ReturnFocus(world, focused);
        foreach (FocusInput input in _events)
        {
            if (input.Kind == FocusInputKind.Key && input.Key == Key.Shift)
            {
                _shiftPressed = input.Pressed;
                continue;
            }
            if (TextEditingSystem.HandleInput(world, focused, in input, _shiftPressed))
                continue;
            if (input.Kind == FocusInputKind.Navigate)
                focused = Navigate(world, focused, input.Direction);
            else if (input.Kind == FocusInputKind.Confirm)
                Confirm(world, focused);
            else if (input.Pressed)
            {
                switch (input.Key)
                {
                    case Key.Left: focused = Navigate(world, focused, Direction.Left); break;
                    case Key.Right: focused = Navigate(world, focused, Direction.Right); break;
                    case Key.Up: focused = Navigate(world, focused, Direction.Up); break;
                    case Key.Down: focused = Navigate(world, focused, Direction.Down); break;
                    case Key.Enter:
                    case Key.Space: Confirm(world, focused); break;
                }
            }
        }
        WriteCapture(world, focused);
    }

    private void WriteCapture(World world, Entity focused)
    {
        InputCapture capture = world.Read<InputCapture>(_input);
        if (capture.Keyboard != (focused != Entity.Null))
        {
            capture.Keyboard = focused != Entity.Null;
            world.Replace(_input, capture);
        }
    }

    private void OnEntityDestroying(World world, Entity node)
    {
        if (world.HasSparse<Focused>(node))
            ReturnFocus(world, node);
    }

    private Entity ReturnFocus(World world, Entity node)
    {
        Entity ancestor = Hierarchy<ElementTree>.GetParent(world, node);
        while (ancestor != Entity.Null && !world.Has<Focusable>(ancestor))
            ancestor = Hierarchy<ElementTree>.GetParent(world, ancestor);
        MoveFocus(world, node, ancestor);
        WriteCapture(world, ancestor);
        return ancestor;
    }

    private Entity Navigate(World world, Entity focused, Direction direction)
    {
        FocusTarget? origin = null;
        foreach (FocusTarget target in _targets)
            if (target.Node == focused)
                origin = target;

        if (origin is not { } from)
        {
            Entity first = FindFirstTarget()?.Node ?? focused;
            MoveFocus(world, focused, first);
            return first;
        }
        FocusTarget? nearest = null;
        float distance = float.PositiveInfinity;
        foreach (FocusTarget target in _targets)
        {
            if (target.Node == focused || target.Canvas != from.Canvas)
                continue;
            Vector2 delta = target.Center - from.Center;
            bool ahead = direction switch
            {
                Direction.Left => delta.X < 0,
                Direction.Right => delta.X > 0,
                Direction.Up => delta.Y < 0,
                Direction.Down => delta.Y > 0,
                _ => false,
            };
            float candidateDistance = delta.LengthSquared();
            if (ahead && (candidateDistance < distance ||
                (candidateDistance == distance && (nearest is null || target.Node.Index < nearest.Value.Node.Index))))
            {
                nearest = target;
                distance = candidateDistance;
            }
        }
        Entity next = nearest?.Node ?? focused;
        MoveFocus(world, focused, next);
        return next;
    }

    private FocusTarget? FindFirstTarget()
    {
        FocusTarget? first = null;
        foreach (FocusTarget target in _targets)
        {
            if (first is not { } previous)
            {
                first = target;
                continue;
            }
            int order = target.Center.Y.CompareTo(previous.Center.Y);
            if (order == 0)
                order = target.Center.X.CompareTo(previous.Center.X);
            if (order == 0)
                order = target.Node.Index.CompareTo(previous.Node.Index);
            if (order < 0)
                first = target;
        }
        return first;
    }

    private static void MoveFocus(World world, Entity previous, Entity next)
    {
        if (previous == next)
            return;
        if (previous != Entity.Null)
        {
            TextEditingSystem.CancelComposition(world, previous);
            world.RemoveSparse<Focused>(previous);
        }
        if (next != Entity.Null)
            world.AddSparse(next, new Focused());
    }

    private static void Confirm(World world, Entity focused)
    {
        if (focused == Entity.Null)
            return;
        Clicks clicks = world.Read<Clicks>(focused);
        clicks.Count = unchecked(clicks.Count + 1);
        world.Replace(focused, clicks);
    }

    private static Entity ReadFocus(World world)
    {
        Entity focused = Entity.Null;
        world.ExecuteSparseRead<Focused, Entity>(ref focused, static (entities, values, ref result) =>
        {
            DevelopmentCheck.Require(entities.Length <= 1, "Focus navigation requires one Focused node per World.");
            if (entities.Length > 0)
                result = entities[0];
        });
        return focused;
    }

    private static Entity FindCanvas(World world, Entity node)
    {
        while (node != Entity.Null)
        {
            if (world.Has<Canvas>(node))
                return node;
            node = Hierarchy<ElementTree>.GetParent(world, node);
        }
        return Entity.Null;
    }

    private static bool TryGetRectangle(World world, Entity node, out Rect rect)
    {
        if (world.Has<LayoutRect>(node))
        {
            rect = world.Read<LayoutRect>(node).Value;
            return true;
        }
        rect = default;
        bool hasRectangle = false;
        foreach (Entity child in Hierarchy<ElementTree>.GetChildren(world, node))
        {
            if (world.Has<Canvas>(child) || !TryGetRectangle(world, child, out Rect content))
                continue;
            if (hasRectangle)
            {
                float x = MathF.Min(rect.X, content.X);
                float y = MathF.Min(rect.Y, content.Y);
                rect = new Rect(x, y, MathF.Max(rect.X + rect.Width, content.X + content.Width) - x,
                    MathF.Max(rect.Y + rect.Height, content.Y + content.Height) - y);
            }
            else
                rect = content;
            hasRectangle = true;
        }
        return hasRectangle;
    }

    public void OnDestroy(ref ImmediateSystemContext context)
    {
        context.World.EntityDestroying -= OnEntityDestroying;
        context.World.ReleaseQuery(_focusableQuery);
    }

    private readonly record struct FocusTarget(Entity Node, Rect Rect, Entity Canvas)
    {
        internal Vector2 Center => new(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
    }
}
