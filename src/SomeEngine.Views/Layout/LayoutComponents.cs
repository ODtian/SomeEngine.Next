using System.Numerics;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Layout;

/// <summary>Caller-written outer size limits and inner spacing.</summary>
public struct Box : IComponent
{
    public Thickness Padding;
    public Size Min;
    private Size? _max;
    private Size? _fixed;

    public Size Max { readonly get => _max ?? Size.Unbounded; set => _max = value; }
    public Size Fixed { readonly get => _fixed ?? Size.Auto; set => _fixed = value; }
}

/// <summary>The rectangle in canvas coordinates, written by the layout system.</summary>
public struct LayoutRect : IComponent
{
    public Rect Value;
}

/// <summary>The last measurement and its layout input version, written by the layout system.</summary>
public struct LayoutCache : IComponent
{
    public Constraints Constraints;
    public Size Size;
    public uint InputVersion;
}

/// <summary>Application-written target, target size, and pixels per canvas unit.</summary>
public struct Canvas : IComponent
{
    public CanvasTarget Target;
    public Size Size;
    public bool DepthTest;
    private float? _scale;
    public float Scale { readonly get => _scale ?? 1; set => _scale = value; }
}

public enum CanvasTargetKind : byte
{
    Screen,
    RenderTexture,
    WorldSpace,
}

/// <summary>The screen, texture entity, or world entity used to present a canvas.</summary>
public readonly record struct CanvasTarget(CanvasTargetKind Kind, Entity Entity)
{
    public static CanvasTarget Screen => default;
    public static CanvasTarget RenderTexture(Entity entity) => new(CanvasTargetKind.RenderTexture, entity);
    public static CanvasTarget WorldSpace(Entity entity) => new(CanvasTargetKind.WorldSpace, entity);
}

/// <summary>Caller-written position relative to an Absolute parent's content origin.</summary>
public struct AbsolutePosition : IComponent, IEquatable<AbsolutePosition>
{
    public Vector2 Value;
    public readonly bool Equals(AbsolutePosition other) => Value.Equals(other.Value);
}
