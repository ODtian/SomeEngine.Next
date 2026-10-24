using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>Four hierarchy children in the UE 5.8 packed hierarchy layout.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ClusterBVHNode
{
    public const int SizeInBytes = 240;
    public const int ChildCount = 4;
    public ClusterNodeVectors LODBounds;
    public ClusterNodeVectors CentersAndErrors;
    public ClusterNodeVectors ExtentsAndReferences;
    public ClusterNodeWords PageRangesAndParts;

    public readonly ClusterHierarchySlice GetChild(int child)
    {
        Vector4 center = CentersAndErrors[child];
        Vector4 extent = ExtentsAndReferences[child];
        ClusterHierarchyWords words = PageRangesAndParts[child];
        return new ClusterHierarchySlice
        {
            LODBounds = LODBounds[child],
            BoundsCenter = new(center.X, center.Y, center.Z),
            PackedErrors = BitConverter.SingleToUInt32Bits(center.W),
            BoundsExtent = new(extent.X, extent.Y, extent.Z),
            ChildReference = BitConverter.SingleToUInt32Bits(extent.W),
            PageRangeKey = words.PageRangeKey,
            GroupPartData = words.GroupPartData,
            BoneData = words.BoneData,
        };
    }

    public void SetChild(int child, in ClusterHierarchySlice value)
    {
        LODBounds[child] = value.LODBounds;
        CentersAndErrors[child] = new(value.BoundsCenter, BitConverter.UInt32BitsToSingle(value.PackedErrors));
        ExtentsAndReferences[child] = new(value.BoundsExtent, BitConverter.UInt32BitsToSingle(value.ChildReference));
        PageRangesAndParts[child] = new(value.PageRangeKey, value.GroupPartData, value.BoneData);
    }
}

[InlineArray(4)]
public struct ClusterNodeVectors { private Vector4 _element; }
[InlineArray(4)]
public struct ClusterNodeWords { private ClusterHierarchyWords _element; }
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ClusterHierarchyWords(uint PageRangeKey, uint GroupPartData, uint BoneData);

public struct ClusterHierarchySlice
{
    public Vector4 LODBounds;
    public Vector3 BoundsCenter;
    public uint PackedErrors;
    public Vector3 BoundsExtent;
    public uint ChildReference;
    public uint PageRangeKey;
    public uint GroupPartData;
    public uint BoneData;

    public readonly bool IsEnabled => PageRangeKey != 0 || ClusterCount != 0;
    public readonly bool IsLeaf => PageRangeKey != uint.MaxValue;
    public readonly uint FirstPage => (PageRangeKey >> 5) & 0x1FFFFFFu;
    public readonly uint PageCount => PageRangeKey & 31u;
    public readonly uint FirstCluster => ChildReference & 255u;
    public readonly uint ClusterCount => GroupPartData >> 16;
    public readonly float MinLODError => (float)BitConverter.UInt16BitsToHalf((ushort)(PackedErrors >> 16));
    public readonly float MaxParentLODError => (float)BitConverter.UInt16BitsToHalf((ushort)PackedErrors);

    public void SetErrors(float minimum, float maximum)
    {
        Half min = (Half)minimum;
        if ((float)min > minimum) min = Half.BitDecrement(min);
        Half max = (Half)maximum;
        PackedErrors = ((uint)BitConverter.HalfToUInt16Bits(min) << 16) | BitConverter.HalfToUInt16Bits(max);
    }
}
