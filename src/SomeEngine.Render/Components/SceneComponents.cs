using System.Numerics;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public struct MeshMotion : IComponent
{
    public Vector3 Amplitude;
    public uint Seed;
}

public readonly record struct CameraDepthOfField(
    float FocusDistance,
    float InfinityCircleRadiusPixels,
    float MinCircleRadiusPixels,
    float MaxCircleRadiusPixels,
    float DepthBlurRadiusPixels = 0f,
    float DepthBlurExponent = 0f,
    float ShadowResolutionBiasStrength = 0f);

public struct SceneCamera : IComponent
{
    public Vector3 Position;
    public Vector3 Target;
    public Vector3 Up;
    public float VerticalFieldOfView;
    public float NearPlane;
    public float FarPlane;
    public CameraDepthOfField? DepthOfField;
    public uint PlayerId;
    public FirstPersonCamera? FirstPerson;
}
