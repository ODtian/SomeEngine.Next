using SomeEngine.ECS;

namespace SomeEngine.Render.Cluster;

/// <summary>Model-bound range: four child-slice records per mesh-local BVH node, followed by draw clusters in page order.</summary>
/// <remarks>NodeCount counts physical 240-byte packed nodes, not child slices. Each bounds record is three float4 values.
/// Slice (node, child) is at First + node * 4 + child; draw cluster k is at First + NodeCount * 4 + k.
/// A zero node count selects the mesh's stored geometry bounds.
/// Producers write this component when current geometry changes, including changes within the same range.</remarks>
public readonly record struct RenderDeformBounds(uint First, uint NodeCount) : IComponent;
