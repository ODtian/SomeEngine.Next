using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Layout;

namespace SomeEngine.Elements.Render;

/// <summary>The extracted target, size, scale and world placement of a canvas.</summary>
public readonly record struct RenderCanvas(CanvasTarget Target, Size Size, float Scale) : IComponent
{
    public Matrix4x4 Transform { get; init; } = Matrix4x4.Identity;
    public bool DepthTest { get; init; }
}
