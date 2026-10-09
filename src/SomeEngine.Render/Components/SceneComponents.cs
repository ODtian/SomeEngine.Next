using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using System.Runtime.InteropServices;

namespace SomeEngine.Render.Components;

/// <summary>Mesh identity in a saved Game world, resolved during explicit loading.</summary>
public struct MeshSource : IComponent
{
    public AssetId<MeshAsset> Mesh;
    public float BoundsExpansion;
    public DepthCacheClass DepthCacheClass;
    public FirstPersonMeshData FirstPerson;
}

public struct MeshMotion : IComponent
{
    public Vector3 Amplitude;
    public uint Seed;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
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
