using System.Numerics;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.Render.Materials;

namespace SomeEngine.Render.Components;

public static class LightDefaults
{
    public const uint LayerMask = 0xFFFFFFFFu;
}

public enum LightMobility : uint
{
    Movable,
    Static,
}

/// <summary>
/// Per-light shadow precision, movement and emitter geometry. SourceRadius uses world units;
/// SourceAngleDegrees is the directional emitter's full diameter, with null selecting the native sun size.
/// </summary>
public readonly record struct LightShadowSettings(
    float ResolutionLodBias = 0f,
    LightMobility Mobility = LightMobility.Movable,
    float SourceRadius = 0f,
    float? SourceAngleDegrees = null);

/// <summary>Directional-light semantics authored in the main world.</summary>
public readonly record struct DirectionalLight(
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    uint LayerMask = LightDefaults.LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Point-light semantics authored in the main world.</summary>
public readonly record struct PointLight(
    Vector3 Position,
    float Range,
    Vector3 Color,
    float Intensity,
    uint LayerMask = LightDefaults.LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Spot-light semantics authored in the main world.</summary>
public readonly record struct SpotLight(
    Vector3 Position,
    float Range,
    Vector3 Direction,
    float InnerConeCos,
    float OuterConeCos,
    Vector3 Color,
    float Intensity,
    uint LayerMask = LightDefaults.LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Optional semantic cookie attached to a light in the main world.</summary>
public readonly record struct LightCookie(
    Texture Texture,
    float Strength,
    Vector4 ScaleOffset,
    Matrix4x4 WorldToCookie) : IComponent;

/// <summary>Directional-light snapshot owned by the render world.</summary>
public readonly record struct RenderDirectionalLight(
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    uint LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Point-light snapshot owned by the render world.</summary>
public readonly record struct RenderPointLight(
    Vector3 Position,
    float Range,
    Vector3 Color,
    float Intensity,
    uint LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Spot-light snapshot owned by the render world.</summary>
public readonly record struct RenderSpotLight(
    Vector3 Position,
    float Range,
    Vector3 Direction,
    float InnerConeCos,
    float OuterConeCos,
    Vector3 Color,
    float Intensity,
    uint LayerMask,
    LightShadowSettings ShadowSettings = default) : IComponent;

/// <summary>Light-cookie snapshot owned by the render world.</summary>
public readonly record struct RenderLightCookie(
    Texture Texture,
    float Strength,
    Vector4 ScaleOffset,
    Matrix4x4 WorldToCookie) : IComponent;
