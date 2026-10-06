using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Assets.Pipeline;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public enum DepthCacheClass : uint
{
    Dynamic = 0,
    Static = 1,
}

public struct MeshInstance : IComponent
{
    public required Mesh Mesh;
    public float BoundsExpansion;
    public DepthCacheClass DepthCacheClass;
    /// <summary>Optional explicit source for geometry pages not yet resident in Mesh.</summary>
    public MeshPayloadSource? PageSource;
}

