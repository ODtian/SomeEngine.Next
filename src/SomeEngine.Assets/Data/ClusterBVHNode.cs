using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Assets.Data;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct ClusterBVHNode
{
    public Vector4 BoundMin; // w is padding
    public Vector4 BoundMax; // w is padding
    public Vector4 LODSphere;
    public float LODError;
    public uint ChildPointer;
    public uint ChildCount;
    public uint NodeType; // 0 = Internal, 1 = Leaf

    // Helper for encoding leaf data node counts
    public void SetLeafData(uint clusterStart, uint clusterCount)
    {
        if (clusterStart > 0xFFF)
            throw new ArgumentOutOfRangeException(nameof(clusterStart), clusterStart, "A BVH leaf cluster start must fit in 12 bits.");
        if (clusterCount is 0 or > 0xFFFFF)
            throw new ArgumentOutOfRangeException(nameof(clusterCount), clusterCount, "A BVH leaf cluster count must be in the 20-bit range 1..1048575.");

        ChildCount = (clusterCount << 12) | clusterStart;
    }

    public readonly void GetLeafData(out uint clusterStart, out uint clusterCount)
    {
        clusterStart = ChildCount & 0xFFF;
        clusterCount = ChildCount >> 12;
    }
}

