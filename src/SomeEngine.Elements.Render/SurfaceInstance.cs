using SomeEngine.Core.Math;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Elements.Render;

/// <summary>One surface in canvas coordinates, with a version that changes with its drawing data.</summary>
public readonly record struct SurfaceInstance(
    Entity Node,
    Rect Rectangle,
    Color Fill,
    float CornerRadius,
    Color BorderColor,
    float BorderWidth,
    Rect ClipRectangle,
    int DrawOrder,
    uint Version) : IBufferElement
{
    public DropShadow Shadow { get; init; }
}
