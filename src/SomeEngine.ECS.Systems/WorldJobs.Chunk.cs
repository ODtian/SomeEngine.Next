using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    /// <summary>
    /// Whole-type accesses covering everything <see cref="BuildChunkAccesses{TAction}"/> can register for this query.
    /// A chunk job reserves them at submission, so later submissions that conflict with it run after it.
    /// </summary>
    internal void ReserveChunkAccesses<TAction>(Query query, SystemAccesses? additional, ref AccessBuilder<JobResourceAccess> accesses)
        where TAction : struct, IChunkAction
    {
        accesses.Add(JobResourceAccess.Read(_structure));
        foreach (ChunkAccess component in ChunkActionTypes<TAction>.Components)
        {
            if (component.Structural) continue;
            JobResource values = Resource(WorldValueKind.Table, component.Type);
            accesses.Add(component.WritesValue ? JobResourceAccess.Write(values) : JobResourceAccess.Read(values));
            if (!component.Tracked) continue;
            JobResource changes = Resource(WorldValueKind.Changes, component.Type);
            accesses.Add(component.WritesValue ? JobResourceAccess.Write(changes) : JobResourceAccess.Read(changes));
        }
        for (int type = 0; type < ComponentTypes.Count; type++)
            if (query.Description.Changed.Has(type) && !ChunkActionTypes<TAction>.TrackedWrites.Contains(type)
                && !ComponentTypes.Get(type).UsesStructureWrites)
                accesses.Add(JobResourceAccess.Read(Resource(WorldValueKind.Changes, type)));
        if (additional is not null) foreach (WorldValueAccess access in additional.Values) AddValue(ref accesses, access);
    }

    internal void BuildChunkAccesses<TAction>(Query query, QueryRanges ranges, SystemAccesses? additional,
        ref AccessBuilder<JobResourceAccess> accesses, bool wholeTypes = true) where TAction : struct, IChunkAction
    {
        foreach (ChunkAccess component in ChunkActionTypes<TAction>.Components)
        {
            if (!component.HasResources) RequireJobStorage(ComponentTypes.Get(component.Type));
            if (component.Structural) continue;
            JobResource values = Resource(WorldValueKind.Table, component.Type);
            bool whole = wholeTypes && ranges.Count != 0 && query.CoversMatchedTypeLayouts(component.Type);
            if (whole) accesses.Add(component.WritesValue ? JobResourceAccess.Write(values) : JobResourceAccess.Read(values));
            JobResource changes = component.Tracked ? Resource(WorldValueKind.Changes, component.Type) : default;
            for (int i = 0; i < ranges.Count; i++)
            {
                QueryRange range = ranges.Items[i]; int end = range.End;
                while (i + 1 < ranges.Count && ReferenceEquals(ranges.Items[i + 1].Chunk, range.Chunk)) end = ranges.Items[++i].End;
                long start = ((long)range.Chunk.Id << 32) + range.Start;
                if (!whole) accesses.Add(component.WritesValue ? JobResourceAccess.Write(values, start, end - range.Start)
                    : JobResourceAccess.Read(values, start, end - range.Start));
                if (component.Tracked) accesses.Add(component.WritesValue
                    ? JobResourceAccess.Write(changes, range.Chunk.Id, 1)
                    : JobResourceAccess.Read(changes, range.Chunk.Id, 1));
            }
        }
        for (int type = 0; type < ComponentTypes.Count; type++)
            if (query.Description.Changed.Has(type) && !ChunkActionTypes<TAction>.TrackedWrites.Contains(type)
                && !ComponentTypes.Get(type).UsesStructureWrites)
                for (int i = 0; i < ranges.Count; i++)
                {
                    QueryRange range = ranges.Items[i];
                    while (i + 1 < ranges.Count && ReferenceEquals(ranges.Items[i + 1].Chunk, range.Chunk)) i++;
                    accesses.Add(JobResourceAccess.Read(Resource(WorldValueKind.Changes, type), range.Chunk.Id, 1));
                }
        if (additional is not null) foreach (WorldValueAccess access in additional.Values) AddValue(ref accesses, access);
    }
}
