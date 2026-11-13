using SomeEngine.ECS.Storage;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems;

public sealed partial class WorldJobs
{
    internal override void RegisterEventAccess(JobResourceAccess access)
    {
        if (JobThreads.IsActive) { _scheduler.RequireCurrentAccess(access, true); return; }
        if (s_current is not { } current || !ReferenceEquals(current.Jobs, this)) return;
        AccessBuilder<JobResourceAccess> accesses = new();
        try { accesses.Add(access); _scheduler.AppendSynchronousAccess(current.Handle, accesses); }
        finally { accesses.Clear(); }
    }
    public JobResourceAccess[] CollectAccess(SystemAccesses additional, bool structureWrite = false)
    {
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(structureWrite, ref accesses);
            foreach (WorldValueAccess value in additional.Values)
            {
                if (!structureWrite && value.Kind is (WorldValueKind.Table or WorldValueKind.Sparse))
                    RequireJobStorage(ComponentTypes.Get(value.Type));
                AddValue(ref accesses, value);
            }
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    /// <summary>Collects a typed action's permissions for its current query chunks after semantic dependencies complete.</summary>
    public JobResourceAccess[] CollectAccess<TAction>(Query query) where TAction : struct, IChunkAction
    {
        query = ChunkActionTypes<TAction>.Bind(query);
        using WorldAccess structure = _world.OpenStructure(false);
        _world.BeginRead();
        QueryRanges ranges = QueryRanges.Rent();
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            ranges.Build(query, 1024);
            AddStructureAccess(false, ref accesses);
            BuildChunkAccesses<TAction>(query, ranges, TAction.AdditionalAccesses, ref accesses, wholeTypes: false);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); ranges.Return(); _world.EndRead(); }
    }

    /// <summary>Collects actual referenced resources while the caller holds the typed source columns.</summary>
    public JobResourceAccess[] CollectResources<TAction>(Query query, in TAction action) where TAction : struct, IChunkAction
    {
        query = ChunkActionTypes<TAction>.Bind(query);
        using WorldAccess structure = _world.OpenStructure(false);
        _world.BeginRead();
        QueryRanges ranges = QueryRanges.Rent();
        AccessBuilder<JobResourceAccess> required = new(), resources = new();
        SynchronousResourceAccess source = default;
        bool acquired = !JobThreads.IsActive;
        try
        {
            ranges.Build(query, 1024);
            BuildChunkAccesses<TAction>(query, ranges, TAction.AdditionalAccesses, ref required, wholeTypes: false);
            if (acquired) source = _scheduler.AcquireSynchronousAccess(JobResourceAccess.Read(_structure), required);
            else for (int i = 0; i < required.Count; i++) _scheduler.RequireCurrentAccess(required.Get(i), required.Get(i).Mode != JobAccessMode.Read);
            SystemRunner.CollectChunkResources(_scheduler, ranges, ref resources, query.SinceVersion, action);
            return CopyAccesses(resources);
        }
        finally { if (acquired) source.Dispose(); required.Clear(); resources.Clear(); ranges.Return(); _world.EndRead(); }
    }
    /// <summary>Declares structure and the reads required by the World's event producers.</summary>
    public override JobResourceAccess[] StructureRead => CollectStructureAccess(false);

    /// <summary>Declares structure edits and their event writes.</summary>
    public override JobResourceAccess[] StructureWrite => CollectStructureAccess(true);

    /// <summary>Declares direct World.Read component access together with StructureRead.</summary>
    public JobResourceAccess Read<T>() where T : struct
    {
        CheckActive();
        ComponentType component = ComponentType<T>.Value;
        RequireJobStorage(component);
        return JobResourceAccess.Read(component.UsesStructureWrites
            ? _structure : Resource(WorldValueKind.Table, component.Id));
    }

    /// <summary>Collects the structure and component permissions consumed by Query.OpenRead.</summary>
    public override JobResourceAccess[] CollectReadAccess(Query query)
    {
        QueryDescription description = query.Description;
        QueryDefinition definition = description.Definition;
        foreach (int type in definition.AccessTypes) RequireJobStorage(ComponentTypes.Get(type));
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(false, ref accesses);
            BuildQueryValues(description, definition, [], false, ref accesses);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    public JobResourceAccess[] CollectReadAccess<TComponent, TResources>(Query query)
        where TComponent : struct where TResources : struct, IJobComponentResources<TComponent>
    {
        QueryDescription description = query.Description;
        QueryDefinition definition = description.Definition;
        foreach (int type in definition.AccessTypes)
            if (type != ComponentType<TComponent>.TypeId) RequireJobStorage(ComponentTypes.Get(type));
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(false, ref accesses);
            BuildQueryValues(description, definition, [], false, ref accesses);
            CollectComponentResources<TComponent, TResources>(query, ref accesses);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    private void CollectComponentResources<TComponent, TResources>(Query query, ref AccessBuilder<JobResourceAccess> output)
        where TComponent : struct where TResources : struct, IJobComponentResources<TComponent>
    {
        using WorldAccess structure = _world.OpenStructure(false);
        _world.BeginRead();
        QueryRanges ranges = QueryRanges.Rent();
        AccessBuilder<JobResourceAccess> reads = new();
        SynchronousResourceAccess source = default;
        bool acquired = !JobThreads.IsActive;
        try
        {
            ranges.Build(query, 1024);
            int type = ComponentType<TComponent>.TypeId;
            if (!ComponentType<TComponent>.Value.UsesStructureWrites)
                foreach (QueryRange range in ranges.Items.AsSpan(0, ranges.Count))
                    reads.Add(JobResourceAccess.Read(Resource(WorldValueKind.Table, type),
                        ((long)range.Chunk.Id << 32) + range.Start, range.End - range.Start));
            if (acquired) source = _scheduler.AcquireSynchronousAccess(JobResourceAccess.Read(_structure), reads);
            else for (int i = 0; i < reads.Count; i++) _scheduler.RequireCurrentAccess(reads.Get(i), false);
            var resources = new JobResourceAccessBuilder(_scheduler, ref output);
            foreach (QueryRange range in ranges.Items.AsSpan(0, ranges.Count))
            {
                if ((uint)type >= (uint)range.Chunk.Columns.Length || range.Chunk.Columns[type].ColumnPlusOne == 0) continue;
                ReadOnlySpan<TComponent> column = ColumnEntry.Span<TComponent>(ref range.Chunk.Columns[type], range.Chunk.Count);
                if (range.Filter is null)
                    ComponentResources.CollectRange<TComponent, TResources>(column.Slice(range.Start, range.End - range.Start), ref resources);
                else
                {
                    int next = range.Start;
                    while (next < range.End && range.Filter.Next(range.Chunk, query.SinceVersion, ref next,
                        out int start, out int count, range.End))
                        ComponentResources.CollectRange<TComponent, TResources>(column.Slice(start, count), ref resources);
                }
            }
        }
        finally { if (acquired) source.Dispose(); reads.Clear(); ranges.Return(); _world.EndRead(); }
    }

    private JobResourceAccess[] CollectStructureAccess(bool write)
    {
        AccessBuilder<JobResourceAccess> accesses = new();
        try
        {
            AddStructureAccess(write, ref accesses);
            return CopyAccesses(accesses);
        }
        finally { accesses.Clear(); }
    }

    internal void AddStructureAccess(bool write, ref AccessBuilder<JobResourceAccess> accesses)
    {
        CheckActive();
        accesses.Add(write ? JobResourceAccess.Write(_structure) : JobResourceAccess.Read(_structure));
        _world.CollectEventAccesses(ref accesses, write);
    }

    private static JobResourceAccess[] CopyAccesses(in AccessBuilder<JobResourceAccess> accesses)
    {
        var result = new JobResourceAccess[accesses.Count];
        for (int index = 0; index < result.Length; index++) result[index] = accesses.Get(index);
        return result;
    }

    private static void RequireJobStorage(ComponentType component)
    {
        if (!component.IsJobStorageAliasFree)
            throw new InvalidOperationException("Component Job access requires values with independent storage.");
    }
}
