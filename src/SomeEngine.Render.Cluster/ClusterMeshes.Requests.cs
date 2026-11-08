using SomeEngine.Job;

namespace SomeEngine.Render.Cluster;

internal sealed partial class ClusterMeshes
{
    private const int PageRequestsPerRange = 128;

    private sealed class PageRequestRange
    {
        internal readonly Dictionary<uint, uint> Pages = [];
        internal readonly List<uint> FaultPages = [];

        internal void Add(uint page, uint priority)
        {
            if (!Pages.TryGetValue(page, out uint previous) || priority > previous)
                Pages[page] = priority;
        }
    }

    internal void CollectPageDemands(ReadOnlySpan<ClusterPageDemand> demands, Dictionary<uint, uint> priorities)
    {
        if (demands.IsEmpty) return;
        lock (_gate)
        {
            ThrowIfDisposed();
            var ranges = new PageRequestRange[(demands.Length + PageRequestsPerRange - 1) / PageRequestsPerRange];
            // The coordinator retains the directory through this synchronous join.
            JobSystem.ExecuteParallel(new CollectDemandsJob(demands.ToArray(), ranges), ranges.Length,
                batchSize: JobScheduleOptions.AutomaticBatchSize);
            MergePageRequests(ranges, priorities);
            _fixups.ExpandDependencies(priorities);
        }
    }

    internal uint CollectPageRequests(ClusterPageRequest[] requests, Dictionary<uint, uint> priorities)
    {
        if (requests.Length == 0) return 0;
        lock (_gate)
        {
            ThrowIfDisposed();
            var ranges = new PageRequestRange[(requests.Length + PageRequestsPerRange - 1) / PageRequestsPerRange];
            JobSystem.ExecuteParallel(new CollectRequestsJob(this, requests, ranges), ranges.Length,
                batchSize: JobScheduleOptions.AutomaticBatchSize);
            uint knownLeaves = 0;
            foreach (PageRequestRange range in ranges)
            {
                foreach (uint page in range.FaultPages)
                    ResolvePageCore(page);
                knownLeaves += checked((uint)range.FaultPages.Count);
            }
            MergePageRequests(ranges, priorities);
            _fixups.ExpandDependencies(priorities);
            return knownLeaves;
        }
    }

    private static void MergePageRequests(PageRequestRange[] ranges, Dictionary<uint, uint> priorities)
    {
        foreach (PageRequestRange range in ranges)
        foreach ((uint page, uint priority) in range.Pages)
        {
            if (!priorities.TryGetValue(page, out uint previous) || priority > previous)
                priorities[page] = priority;
        }
    }

    private readonly struct CollectDemandsJob(
        ClusterPageDemand[] demands, PageRequestRange[] ranges) : IJobParallelFor
    {
        public void Execute(int index)
        {
            var result = new PageRequestRange();
            int start = index * PageRequestsPerRange;
            int end = Math.Min(start + PageRequestsPerRange, demands.Length);
            for (int item = start; item < end; item++)
                result.Add(demands[item].PageId, demands[item].Priority);
            ranges[index] = result;
        }
    }

    private readonly struct CollectRequestsJob(
        ClusterMeshes meshes, ClusterPageRequest[] requests, PageRequestRange[] ranges) : IJobParallelFor
    {
        public void Execute(int index)
        {
            var result = new PageRequestRange();
            int start = index * PageRequestsPerRange;
            int end = Math.Min(start + PageRequestsPerRange, requests.Length);
            for (int item = start; item < end; item++)
            {
                ClusterPageRequest request = requests[item];
                if (!meshes._bvh.TryPageForLeaf(request.LeafNodeIndex, out uint page) ||
                    !meshes._pages.TrySource(page, out _, out _)) continue;
                result.FaultPages.Add(page);
                for (uint offset = 0; offset < request.PageCount; offset++)
                    result.Add(request.FirstPageId + offset, request.Priority);
                result.Add(page, request.Priority);
            }
            ranges[index] = result;
        }
    }
}
