using System.Buffers;
using System.Runtime.InteropServices;
using SomeEngine.Job;

namespace SomeEngine.Assets;

public sealed partial class WorldStream
{
    private void UpdateDemands(WorldContent[] contents, ReadOnlyMemory<WorldStreamSource> sources, JobHandle dependency)
    {
        if (_hits.Length < sources.Length)
        {
            int first = _hits.Length;
            Array.Resize(ref _hits, sources.Length);
            for (int index = first; index < _hits.Length; index++) _hits[index] = [];
        }
        var queries = new List<JobResourceAccess>
        {
            JobResourceAccess.Read(_scheduler, contents), JobResourceAccess.Read(_scheduler, _byMinX),
            JobResourceAccess.Read(_scheduler, _hits)
        };
        var merge = new List<JobResourceAccess>
        {
            JobResourceAccess.Read(_scheduler, _hits),
            JobResourceAccess.Read(_scheduler.GetResourceToken(Requests.Values)),
            JobResourceAccess.Write(_scheduler, Demands.Data), JobResourceAccess.Write(_scheduler, Demands.Changes),
            JobResourceAccess.Write(_scheduler, _nextDemands),
            JobResourceAccess.Write(_scheduler.GetResourceToken(Demands))
        };
        if (sources.Length != 0)
        {
            JobResourceAccess sourceRead = ReadSources(sources);
            queries.Add(sourceRead);
            merge.Add(sourceRead);
            for (int index = 0; index < sources.Length; index++)
            {
                queries.Add(JobResourceAccess.Write(_scheduler.GetResourceToken(_hits[index])));
                merge.Add(JobResourceAccess.Read(_scheduler.GetResourceToken(_hits[index])));
            }
            dependency = _scheduler.ScheduleParallel(new FindContent(contents, _byMinX, sources, _hits),
                sources.Length, 1, CollectionsMarshal.AsSpan(queries), dependency);
        }
        _scheduler.Schedule(new MergeDemands(Requests, Demands, sources, _hits, _nextDemands),
            CollectionsMarshal.AsSpan(merge), dependency).Complete();
    }

    private JobResourceAccess ReadSources(ReadOnlyMemory<WorldStreamSource> sources)
    {
        if (MemoryMarshal.TryGetArray(sources, out ArraySegment<WorldStreamSource> array))
            return JobResourceAccess.Read(_scheduler.GetResourceToken(array.Array!), array.Offset, array.Count);
        MemoryMarshal.TryGetMemoryManager<WorldStreamSource, MemoryManager<WorldStreamSource>>(sources,
            out var manager, out int start, out int count);
        return JobResourceAccess.Read(_scheduler.GetResourceToken(manager!), start, count);
    }

    private readonly struct FindContent(WorldContent[] contents, int[] byMinX,
        ReadOnlyMemory<WorldStreamSource> sources, List<int>[] hits) : IJobParallelFor
    {
        public void Execute(int index)
        {
            List<int> result = hits[index];
            result.Clear();
            WorldStreamSource source = sources.Span[index];
            foreach (int content in byMinX)
            {
                WorldBounds bounds = contents[content].Bounds;
                if (bounds.MinX > source.X + source.Radius) break;
                if (bounds.MaxX < source.X - source.Radius) continue;
                double dx = Math.Max(Math.Max(bounds.MinX - source.X, 0), source.X - bounds.MaxX);
                double dy = Math.Max(Math.Max(bounds.MinY - source.Y, 0), source.Y - bounds.MaxY);
                double dz = Math.Max(Math.Max(bounds.MinZ - source.Z, 0), source.Z - bounds.MaxZ);
                if (dx * dx + dy * dy + dz * dz <= source.Radius * source.Radius) result.Add(content);
            }
        }
    }

    private readonly struct MergeDemands(ContentRequests requests, ContentDemands demands,
        ReadOnlyMemory<WorldStreamSource> sources, List<int>[] hits, ContentDemand[] next) : IJob
    {
        public void Execute()
        {
            demands.ChangeCount = 0;
            Array.Clear(next);
            foreach (var (index, priority) in requests.Values) next[index] = new(true, priority);
            for (int source = 0; source < sources.Length; source++)
                foreach (int index in hits[source])
                {
                    int priority = sources.Span[source].Priority;
                    if (!next[index].Required || priority > next[index].Priority) next[index] = new(true, priority);
                }
            for (int index = 0; index < demands.Data.Length; index++)
            {
                if (demands.Data[index] != next[index]) demands.Changes[demands.ChangeCount++] = index;
                demands.Data[index] = next[index];
            }
        }
    }
}
