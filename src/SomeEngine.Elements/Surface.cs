using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The fill, rounded corners, border, and shadow on a layout node.</summary>
public record struct Surface : IComponent
{
    public Color Fill;
    public float CornerRadius;
    public Color BorderColor;
    public float BorderWidth;
    public DropShadow Shadow;
}
