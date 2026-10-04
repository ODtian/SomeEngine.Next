using SomeEngine.Core.Math;
using SomeEngine.ECS;

namespace SomeEngine.Elements;

/// <summary>The caller's fill, rounded corners, and border on a layout node.</summary>
public struct Surface : IComponent
{
    public Color Fill;
    public float CornerRadius;
    public Color BorderColor;
    public float BorderWidth;
}
