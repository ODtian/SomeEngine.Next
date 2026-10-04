using SomeEngine.ECS;
using SomeEngine.Layout;

namespace SomeEngine.Elements.Render;

/// <summary>The extracted target, pixel size, and pixels per layout unit of a canvas.</summary>
public readonly record struct RenderCanvas(CanvasTarget Target, Size Size, float Scale) : IComponent;
