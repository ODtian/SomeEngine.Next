using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Layout;
using SomeEngine.Views;
using SomeEngine.Job;

namespace SomeEngine.Elements;

/// <summary>Processes keyboard and gamepad input before game input and view rebuilds.</summary>
public sealed class FocusNavigationSystem : ISystem<ImmediateSystemContext>
{
    private Query _focusableQuery;
    private DestroyedReader _destroyed = null!;
    private Entity _input;
    private bool _shiftPressed;
    private readonly List<Entity> _nodes = new();
    private readonly List<FocusTarget> _targets = new();
    private readonly List<FocusInput> _events = new();

    public void OnCreate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        _input = FocusInputData.GetInput(world);
        _focusableQuery = world.Query(new QueryDescription().WithStored<Focusable>());
        _destroyed = world.WatchDestroyed();
    }

    public void OnUpdate(ref ImmediateSystemContext context)
    {
        World world = context.World;
        ReleaseDestroyed(world);
        _nodes.Clear();
        using (QueryScope nodes = _focusableQuery.OpenRead())
            foreach (QueryChunk chunk in nodes)
                foreach (Entity node in chunk.Entities) _nodes.Add(node);
        _targets.Clear();
        foreach (Entity node in _nodes)
            if (TryGetRectangle(world, node, out Rect rect))
                _targets.Add(new FocusTarget(node, rect, FindCanvas(world, node)));
        _events.Clear();
        {
            using DynamicBuffer<FocusInput> buffer = world.GetBuffer<FocusInput>(_input);
            foreach (FocusInput input in buffer.AsSpan()) _events.Add(input);
            buffer.Clear();
        }
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
                if (ValueEditing.Step(world, focused, input.Key))
                    continue;
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
        InputCapture capture = world.ReadComponentData<InputCapture>(_input);
        if (capture.Keyboard != (focused != Entity.Null))
        {
            capture.Keyboard = focused != Entity.Null;
            world.Set(_input, capture);
        }
    }

    private void ReleaseDestroyed(World world)
    {
        var job = new ReleaseDestroyedJob(world, this);
        if (world.Execution is WorldJobs jobs)
        {
            JobResourceAccess[] values = jobs.CollectAccess(new SystemAccesses().WriteSparse<Focused>(), structureWrite: true);
            var accesses = new JobResourceAccess[values.Length + 1];
            values.CopyTo(accesses, 0);
            accesses[^1] = _destroyed.Read;
            jobs.Scheduler.Schedule(job, accesses, dependency: _destroyed.Ready).Complete();
        }
        else job.Execute();
    }

    private readonly struct ReleaseDestroyedJob(World world, FocusNavigationSystem system) : IJob
    {
        public void Execute()
        {
            using DestroyedRead read = system._destroyed.Open();
            foreach (DestroyedEntity fact in read)
                if (world.HasSparse<Focused>(fact.Entity)) system.ReturnFocus(world, fact.Entity);
            system._destroyed.Advance(read.Through);
        }
    }

    private static Entity ReadParent(World world, Entity node)
    {
        if (world.Has<Parent<ElementTree>>(node)) return Hierarchy<ElementTree>.GetParent(world, node);
        return world.Has<Removed<Parent<ElementTree>>>(node)
            ? world.ReadComponentData<Removed<Parent<ElementTree>>>(node).Value.Value : Entity.Null;
    }

    private Entity ReturnFocus(World world, Entity node)
    {
        Entity ancestor = ReadParent(world, node);
        while (ancestor != Entity.Null && !world.Has<Focusable>(ancestor))
            ancestor = ReadParent(world, ancestor);
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
                (candidateDistance == distance && (nearest is null || target.Node.Id < nearest.Value.Node.Id))))
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
                order = target.Node.Id.CompareTo(previous.Node.Id);
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
            world.SetSparse(next, new Focused());
    }

    private static void Confirm(World world, Entity focused)
    {
        if (focused == Entity.Null)
            return;
        Clicks clicks = world.ReadComponentData<Clicks>(focused);
        clicks.Count = unchecked(clicks.Count + 1);
        world.Set(focused, clicks);
    }

    private static Entity ReadFocus(World world)
    {
        using SparseReadScope<Focused> focused = world.OpenSparseRead<Focused>();
        DevelopmentCheck.Require(focused.Count <= 1, "Focus navigation requires one Focused node per World.");
        return focused.Count == 0 ? Entity.Null : focused.Entities[0];
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
            rect = world.ReadComponentData<LayoutRect>(node).Value;
            return true;
        }
        rect = default;
        bool hasRectangle = false;
        using ChildrenRead children = Hierarchy<ElementTree>.GetChildren(world, node);
        foreach (Entity child in children)
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
        _destroyed.Dispose();

    }

    private readonly record struct FocusTarget(Entity Node, Rect Rect, Entity Canvas)
    {
        internal Vector2 Center => new(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2);
    }
}
