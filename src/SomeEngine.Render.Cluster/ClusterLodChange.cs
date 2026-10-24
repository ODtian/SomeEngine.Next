using System.Numerics;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

/// <summary>A reduction group's geometry changed at one submitted streaming boundary.</summary>
public readonly record struct ClusterLodChange(Mesh Mesh, uint RootNode, Vector4 Bounds,
    uint FirstPage, uint PageCount);

internal readonly record struct ClusterLeafFlagFixup(uint ClusterByteAddress, uint StreamingLeaf);
