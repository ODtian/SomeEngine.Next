using SomeEngine.ECS;

namespace SomeEngine.Render.Cluster;

/// <summary>Model-bound range: mesh-local BVH nodes followed by draw clusters in page order.</summary>
/// <remarks>A zero node count selects the mesh's stored geometry bounds.</remarks>
public readonly record struct RenderDeformBounds(uint First, uint NodeCount) : IComponent;
