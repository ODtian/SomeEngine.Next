using System.Numerics;
using SomeEngine.Core.Diagnostics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Layout;

namespace SomeEngine.Elements;

/// <summary>Queues canvas-coordinate pointer input for the next hit-test stage.</summary>
public static class Pointer
{
    public static void Move(World world, Entity canvas, Vector2 position)
    {
        DevelopmentCheck.Require(float.IsFinite(position.X) && float.IsFinite(position.Y),
            "Pointer.Move requires a finite canvas position.");
        Enqueue(world, canvas, new PointerEvent { Kind = PointerEventKind.Move, Position = position });
    }

    public static void Press(World world, Entity canvas, PointerButton button)
    {
        CheckButton(button);
        Enqueue(world, canvas, new PointerEvent { Kind = PointerEventKind.Press, Button = button });
    }

    public static void Release(World world, Entity canvas, PointerButton button)
    {
        CheckButton(button);
        Enqueue(world, canvas, new PointerEvent { Kind = PointerEventKind.Release, Button = button });
    }

    public static void Wheel(World world, Entity canvas, Vector2 delta)
    {
        DevelopmentCheck.Require(float.IsFinite(delta.X) && float.IsFinite(delta.Y),
            "Pointer.Wheel requires a finite wheel delta.");
        Enqueue(world, canvas, new PointerEvent { Kind = PointerEventKind.Wheel, Delta = delta });
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void CheckButton(PointerButton button) =>
        DevelopmentCheck.Require(button <= PointerButton.Middle, "Pointer requires a supported button.");

    private static void Enqueue(World world, Entity canvas, PointerEvent input)
    {
        DevelopmentCheck.Require(world is not null && world.IsAlive(canvas) && world.Has<Canvas>(canvas),
            "Pointer input requires a live Canvas in the supplied World.");
        if (!world.Has<PointerData>(canvas))
        {
            world.Add(canvas, new PointerData());
            world.AddBuffer<PointerEvent>(canvas);
        }
        world.ExecuteBufferWrite<PointerEvent, PointerEvent>(canvas, ref input,
            static (events, ref value) => events.Add(value));
    }

    internal static void TakeEvents(World world, Entity canvas, List<PointerEvent> events)
    {
        events.Clear();
        world.ExecuteBufferWrite<PointerEvent, List<PointerEvent>>(canvas, ref events, static (buffer, ref values) =>
        {
            foreach (PointerEvent input in buffer.ReadSpan())
                values.Add(input);
            buffer.Clear();
        });
    }
}

internal enum PointerEventKind : byte { Move, Press, Release, Wheel }

internal struct PointerEvent : IBufferElement
{
    internal PointerEventKind Kind;
    internal PointerButton Button;
    internal Vector2 Position;
    internal Vector2 Delta;
}

internal struct PointerData : IComponent
{
    internal Vector2 Position;
    internal bool HasPosition;
    internal ButtonPress PrimaryPress;
    internal ButtonPress SecondaryPress;
    internal ButtonPress MiddlePress;

    internal readonly ButtonPress GetPress(PointerButton button) => button switch
    {
        PointerButton.Primary => PrimaryPress,
        PointerButton.Secondary => SecondaryPress,
        PointerButton.Middle => MiddlePress,
        _ => default,
    };

    internal void SetPress(PointerButton button, ButtonPress press)
    {
        switch (button)
        {
            case PointerButton.Primary: PrimaryPress = press; break;
            case PointerButton.Secondary: SecondaryPress = press; break;
            case PointerButton.Middle: MiddlePress = press; break;
        }
    }
}

internal readonly record struct ButtonPress(bool Down, Entity Node, Entity DragNode, bool Dragged = false);
