using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>Pointer hover and held-press state written by HitTestSystem.</summary>
public struct Hover : IComponent
{
    public bool Hovered;
    public bool Pressed;
}

/// <summary>The number of pointer clicks and focus confirmations written by the input stage.</summary>
public struct Clicks : IComponent
{
    public uint Count;
}

/// <summary>The click count consumed by this node's caller scope.</summary>
public struct ClickSeen : IComponent
{
    public uint Count;
}

/// <summary>Declares that a node participates in pointer hit testing.</summary>
public struct Pressable : IComponent;

/// <summary>World-wide input consumption, read by game input systems.</summary>
public struct InputCapture : IComponent
{
    public bool Pointer;
    public bool Keyboard;
}

public enum PointerButton : byte
{
    Primary,
    Secondary,
    Middle,
}
