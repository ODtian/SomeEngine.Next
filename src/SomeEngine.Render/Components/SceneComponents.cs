using System.Numerics;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

/// <summary>Mesh identity in a saved Game world, resolved during explicit loading.</summary>
public struct MeshSource : IComponent
{
    public AssetId<Mesh> Mesh;
    public float BoundsExpansion;
}

public readonly record struct MaterialSource(AssetId<Material> Material) : IBufferElement;

public struct MeshMotion : IComponent
{
    public Vector3 Amplitude;
    public uint Seed;
}

public struct SceneCamera : IComponent
{
    public Vector3 Position;
    public Vector3 Target;
    public Vector3 Up;
    public float VerticalFieldOfView;
    public float NearPlane;
    public float FarPlane;
}
