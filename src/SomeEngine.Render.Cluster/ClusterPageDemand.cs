using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ClusterPagePriority(uint PageId, uint Priority);

internal readonly record struct ClusterStreamingDemand(ulong Bytes, ulong Capacity, bool IsComplete);

/// <summary>A page fault, visible-page priority, view result or submitted-frame count in the render feedback queue.</summary>
public readonly record struct ClusterPageDemand(uint PageId, uint Priority)
{
    public ulong Epoch { get; init; }
    public ClusterPageDemandKind Kind { get; init; }
    public uint LeafNodeIndex { get; init; }
    public uint PageCount { get; init; }
    public uint Count { get; init; }
    public bool Complete { get; init; }
}

public enum ClusterPageDemandKind : byte { Demand, Fault, ViewEnd, FrameEnd }
