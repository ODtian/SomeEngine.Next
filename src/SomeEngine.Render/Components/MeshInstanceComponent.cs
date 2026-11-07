using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Assets.Pipeline;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;

namespace SomeEngine.Render.Components;

public enum DepthCacheClass : uint
{
    Auto = 0,
    Static = 1,
    Dynamic = 2,
}

/// <summary>Instance collection stored as one value component in each world.</summary>
public readonly record struct InstanceSet(uint Value) : IComponent;

public struct MeshInstance : IComponent
{
    public required Mesh Mesh;
    public float BoundsExpansion;
    public DepthCacheClass DepthCacheClass;
    /// <summary>Optional explicit source for geometry pages not yet resident in Mesh.</summary>
    public MeshPayloadSource? PageSource;
}

