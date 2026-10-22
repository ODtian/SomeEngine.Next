using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterGpuBufferSizes(
    uint PageStride, uint PageCount, uint BvhNodeCount, int BvhLevels)
{
    internal uint PageBytes => checked(PageStride * PageCount);
}

internal readonly record struct ClusterGpuAllocation(
    uint PageOffset, ClusterGpuBufferSizes Sizes, ClusterBvhAllocation Bvh);

internal sealed partial class ClusterMeshes
{
    private readonly Dictionary<uint, ClusterGpuAllocation> _gpuGeometry = [];
    private readonly List<(ClusterGpuAllocation Allocation, QueueCompletion[] Completions)> _gpuRetirements = [];

    internal ClusterGeometry ReserveGpu(ClusterGpuBufferSizes sizes, string vertexEncoding, ulong revision, Bounds? bounds)
    {
        lock (_gate)
        {
            uint pageBytes = sizes.PageBytes;
            if (!_heap.TryAlloc(pageBytes, out uint offset))
                throw new InvalidOperationException("Cluster page-heap capacity is exhausted.");
            ClusterBvhAllocation bvh;
            try { bvh = _bvh.ReserveGpu(sizes.BvhNodeCount, sizes.BvhLevels); }
            catch { _heap.Free(offset, pageBytes); throw; }
            uint root = checked((uint)(bvh.Offset / ClusterBvh.NodeBytes));
            _gpuGeometry.Add(root, new(offset, sizes, bvh));
            _residentPageRanges = BuildResidentPageRanges([], []);
            AdvanceRevision();
            return new(root, revision, vertexEncoding, bounds,
                new(EpochId.Value, uint.MaxValue, sizes.PageCount, root, sizes.BvhNodeCount));
        }
    }

    internal ClusterGpuAllocation GetGpu(ClusterGeometry geometry)
    {
        lock (_gate) return _gpuGeometry[geometry.RootBvh];
    }

    internal void ReleaseGpu(ClusterGeometry geometry, QueueCompletion[] completions)
    {
        lock (_gate)
        {
            ClusterGpuAllocation allocation = _gpuGeometry[geometry.RootBvh];
            _gpuGeometry.Remove(geometry.RootBvh);
            _bvh.RemoveGpu(allocation.Bvh);
            _gpuRetirements.Add((allocation, completions));
            _residentPageRanges = BuildResidentPageRanges([], []);
            AdvanceRevision();
        }
    }

    private void RetireGpu(IGraphicsBackend backend)
    {
        for (int index = _gpuRetirements.Count - 1; index >= 0; index--)
        {
            var retirement = _gpuRetirements[index];
            if (retirement.Completions.Any(completion => !backend.IsComplete(completion))) continue;
            _heap.Free(retirement.Allocation.PageOffset, retirement.Allocation.Sizes.PageBytes);
            _bvh.ReleaseGpu(retirement.Allocation.Bvh);
            _gpuRetirements.RemoveAt(index);
        }
    }
}
