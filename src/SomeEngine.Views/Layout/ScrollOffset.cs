using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Layout;

/// <summary>Content displacement and its current range, written by the layout stage.</summary>
public struct ScrollOffset : IComponent
{
    public Vector2 Value;
    public Vector2 Max;
}

/// <summary>Frame displacement and input count supplied by the pointer input stage.</summary>
public struct ScrollInput : IComponent
{
    public Vector2 Delta;
    public uint Count;
}

/// <summary>The scroll input count already applied by the layout stage.</summary>
internal struct ScrollSeen : IComponent
{
    internal uint Count;
}
