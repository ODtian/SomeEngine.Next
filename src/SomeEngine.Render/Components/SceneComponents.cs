using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

/// <summary>Mesh identity in a saved Game world, resolved during explicit loading.</summary>
public struct MeshSource : IComponent
{
    public AssetId<MeshAsset> Mesh;
    public float BoundsExpansion;
    public DepthCacheClass DepthCacheClass;
    public FirstPersonMeshData FirstPerson;
}

/// <summary>Skeleton identity for a skinned mesh node in a saved Game world.</summary>
public struct SkeletonSource : IComponent
{
    public AssetId<SkeletonAsset> Skeleton;
}

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
