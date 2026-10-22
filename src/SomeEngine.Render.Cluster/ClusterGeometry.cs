using SomeEngine.ECS;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

/// <summary>Page and BVH ranges held by the Cluster residency that produced this geometry.</summary>
public readonly record struct ClusterGeometryAllocation(
    ulong Epoch,
    uint FirstPage,
    uint PageCount,
    uint FirstBvhNode,
    uint BvhNodeCount);

/// <summary>Geometry ready for Cluster instances at the rendering preparation boundary.</summary>
public readonly record struct ClusterGeometry(
    uint RootBvh,
    ulong Revision,
    string VertexEncoding,
    Bounds? Bounds,
    ClusterGeometryAllocation Allocation);

/// <summary>Rendering-world geometry and the material displacement allowance for one instance.</summary>
public readonly record struct RenderClusterGeometry(ClusterGeometry Geometry, float BoundsExpansion) : IComponent;
