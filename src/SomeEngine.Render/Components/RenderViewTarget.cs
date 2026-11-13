using System.Numerics;
using SomeEngine.ECS;

namespace SomeEngine.Render.Components;

public enum ViewTargetKind : uint
{
    Screen,
    ShadowPages,
    LayerTiles,
}

public readonly record struct RenderViewTarget(ViewTargetKind Kind, uint TargetIndex = 0) : IComponent;

public readonly record struct RenderPreviousView(Matrix4x4 ViewProjection) : IComponent;
