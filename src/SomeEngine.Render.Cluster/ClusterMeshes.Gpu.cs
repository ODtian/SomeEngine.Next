using SomeEngine.Graphics;
using SomeEngine.Resources;

namespace SomeEngine.Render.Cluster;

internal readonly record struct ClusterGpuBufferSizes(
    uint PageStride, uint PageCount, uint BvhNodeCount, int BvhLevels, uint MaterialCount)
{
    internal uint PageBytes => checked(PageStride * PageCount);
}

internal readonly record struct ClusterGpuAllocation(
    uint PageOffset, ClusterGpuBufferSizes Sizes, ClusterBvhAllocation Bvh);

internal sealed partial class ClusterMeshes
{
    private readonly Dictionary<uint, ClusterGpuAllocation> _gpuGeometry = [];
    private readonly List<(ClusterGpuAllocation Allocation, QueueCompletion[] Completions)> _gpuRetirements = [];

    private uint StreamingPageHeapCapacity
    {
        get
        {
            uint geometryBytes = 0;
            foreach (ClusterGpuAllocation allocation in _gpuGeometry.Values) geometryBytes += allocation.Sizes.PageBytes;
            foreach (var retirement in _gpuRetirements) geometryBytes += retirement.Allocation.Sizes.PageBytes;
            return _heap.Capacity - geometryBytes;
        }
    }

    internal ClusterGeometry ReserveGpu(ClusterGpuBufferSizes sizes, string vertexEncoding, ulong revision, Bounds? bounds)
    {
        lock (_gate)
        {
            // Installed hierarchy references store the byte offset in 512-byte units.
            // The shared page heap supplies this alignment for both base and stride.
            if (sizes.PageCount == 0 || sizes.BvhNodeCount == 0 || sizes.BvhLevels < 1 ||
                sizes.PageStride == 0 || (sizes.PageStride & 511u) != 0)
                throw new ArgumentException("GPU Cluster storage requires nonempty, 512-byte-aligned pages and hierarchy nodes.", nameof(sizes));
            uint pageBytes = sizes.PageBytes;
            if (!_heap.TryAlloc(pageBytes, out uint offset))
                throw new InvalidOperationException("Cluster page-heap capacity is exhausted.");
            if ((offset & 511u) != 0)
            {
                _heap.Free(offset, pageBytes);
                throw new InvalidOperationException("The Cluster page heap returned an unencodable GPU page offset.");
            }
            ClusterBvhAllocation bvh;
            try { bvh = _bvh.ReserveGpu(sizes.BvhNodeCount, sizes.BvhLevels); }
            catch { _heap.Free(offset, pageBytes); throw; }
            uint root = checked((uint)(bvh.Offset / ClusterBvh.NodeBytes));
            _gpuGeometry.Add(root, new(offset, sizes, bvh));
            _residentPageRanges = BuildResidentPageRanges([], [], forceRebuild: true);
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
            _residentPageRanges = BuildResidentPageRanges([], [], forceRebuild: true);
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
