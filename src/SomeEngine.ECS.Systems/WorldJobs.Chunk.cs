using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    internal override void RequireValueRange(in WorldValueAccess value, Chunk chunk, int start, int count)
    {
        RequireSharedJobValue(value);
        if (HasStructureWrite()) return;
        if (value.Structural || ComponentTypes.Get(value.Type).IsTag) { Require(JobResourceAccess.Read(_structure), false); return; }
        bool single = value.Write && !HasRangeWrite;
        JobResource resource = Resource(value.Kind, value.Type);
        long first = value.Kind == WorldValueKind.Changes ? chunk.Id : ((long)chunk.Id << 32) + start;
        long length = value.Kind == WorldValueKind.Changes ? 1 : count;
        Require(value.Write ? JobResourceAccess.Write(resource, first, length) : JobResourceAccess.Read(resource, first, length), single);
        if (value.Write && value.Tracked && value.Kind == WorldValueKind.Table)
            Require(JobResourceAccess.Write(Resource(WorldValueKind.Changes, value.Type), chunk.Id, 1), single);
    }

    private void EnterReadQueryRanges(Query query)
    {
        Require(JobResourceAccess.Read(_structure), false);
        QueryRanges ranges = QueryRanges.Rent();
        AccessBuilder<JobResourceAccess> accesses = new();
        _world.BeginRead();
        try
        {
            ranges.Build(query, 1024);
            foreach (int type in query.Description.Definition.AccessTypes)
            {
                ComponentType component = ComponentTypes.Get(type);
                if (component.UsesStructureWrites || component.IsTag) continue;
                JobResource resource = Resource(WorldValueKind.Table, type);
                foreach (QueryRange range in ranges.Items.AsSpan(0, ranges.Count))
                {
                    accesses.Add(JobResourceAccess.Read(resource, ((long)range.Chunk.Id << 32) + range.Start, range.End - range.Start));
                    if (query.Description.Changed.Has(type))
                        accesses.Add(JobResourceAccess.Read(Resource(WorldValueKind.Changes, type), range.Chunk.Id, 1));
                }
            }
            Enter(false, accesses);
        }
        finally { accesses.Clear(); ranges.Return(); _world.EndRead(); }
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
