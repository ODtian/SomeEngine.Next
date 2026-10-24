using System.Runtime.InteropServices;

namespace SomeEngine.Render.Cluster;

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ClusterPageDemand(uint PageId, uint Priority);

internal readonly record struct ClusterStreamingDemand(ulong Bytes, ulong Capacity, bool IsComplete);
