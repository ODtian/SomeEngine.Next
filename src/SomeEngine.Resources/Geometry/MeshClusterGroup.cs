using System.Collections.Immutable;
using SomeEngine.Serialization;

namespace SomeEngine.Resources.Geometry;

/// <summary>The page dependencies and fixup addresses of one reduction group.</summary>
[BinarySerializable]
public sealed partial record MeshClusterGroup
{
    public uint FirstPage { get; init; }
    public uint PageCount { get; init; }
    public ImmutableArray<MeshClusterReference> Parents { get; init; } = [];
    public ImmutableArray<MeshHierarchyPart> HierarchyParts { get; init; } = [];
}

[BinarySerializable]
public sealed partial record MeshClusterReference
{
    public uint PageIndex { get; init; }
    public uint ClusterIndex { get; init; }
}

[BinarySerializable]
public sealed partial record MeshHierarchyPart
{
    public uint NodeIndex { get; init; }
    public uint ChildIndex { get; init; }
    public uint PageIndex { get; init; }
    public uint FirstCluster { get; init; }
}
