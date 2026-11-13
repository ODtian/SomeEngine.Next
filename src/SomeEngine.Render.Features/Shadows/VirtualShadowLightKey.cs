using SomeEngine.ECS.Entities;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Render.Features.Shadows;

/// <summary>Identifies a light projection independently of its index in the current frame.</summary>
public readonly record struct VirtualShadowLightKey(
    VirtualShadowLightKind Kind,
    Entity Light,
    uint Face,
    int ClipmapLevel);
