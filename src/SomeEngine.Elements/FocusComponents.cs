using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Elements;

/// <summary>Declares that a node accepts focus navigation and confirmation.</summary>
public struct Focusable : IComponent;

/// <summary>The single focused node in a World, written by its focus system.</summary>
public struct Focused : ISparseComponent;

public enum Direction : byte { Left, Right, Up, Down }

public enum Key : byte { Left, Right, Up, Down, Enter, Space, Home, End, Shift, Backspace, Delete }

public enum FocusInputKind : byte { Key, Navigate, Confirm, Text, Composition }

/// <summary>Ordered keyboard and gamepad input on the World's InputCapture entity.</summary>
public struct FocusInput : IBufferElement
{
    public FocusInputKind Kind;
    public Key Key;
    public bool Pressed;
    public Direction Direction;
    public string? Text;
    public int CompositionCaret;
}
