using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Entity = SomeEngine.ECS.Entities.Entity;

namespace SomeEngine.ECS.Comparison;

internal readonly struct EasyTrackedReads(ArchiveIdentityCheck identities, EasyEcs.Entity[] entities) : INativeTrackedReads
{
    public bool IsAlive(Entity entity) => identities.IsAlive(entity) && entities[entity.Id].IsAlive;
    public Entity ReadParent(Entity entity)
    {
        if (!IsAlive(entity) || !entities[entity.Id].Has<ReferenceParent<Entity>>()) throw new ArgumentException("Parent component is absent.");
        return entities[entity.Id].Read<ReferenceParent<Entity>>().Value;
    }
}
internal readonly struct FrifloTrackedReads(ArchiveIdentityCheck identities, Friflo.Engine.ECS.Entity[] entities) : INativeTrackedReads
{
    public bool IsAlive(Entity entity) => identities.IsAlive(entity) && !entities[entity.Id].IsNull;
    public Entity ReadParent(Entity entity)
    {
        if (!IsAlive(entity) || !entities[entity.Id].HasComponent<ReferenceParent<Entity>>()) throw new ArgumentException("Parent component is absent.");
        return entities[entity.Id].GetComponent<ReferenceParent<Entity>>().Value;
    }
}
internal readonly struct ArchTrackedReads(ArchiveIdentityCheck identities, Arch.Core.World world, Arch.Core.Entity[] entities) : INativeTrackedReads
{
    public bool IsAlive(Entity entity) => identities.IsAlive(entity) && world.IsAlive(entities[entity.Id]);
    public Entity ReadParent(Entity entity)
    {
        if (!IsAlive(entity) || !world.Has<ReferenceParent<Entity>>(entities[entity.Id])) throw new ArgumentException("Parent component is absent.");
        return world.Get<ReferenceParent<Entity>>(entities[entity.Id]).Value;
    }
}
internal readonly ref struct DefaultTrackedReads : INativeTrackedReads
{
    private readonly ArchiveIdentityCheck _identities;
    private readonly DefaultEcs.Entity[] _entities;
    private readonly DefaultEcs.Components<ReferenceParent<Entity>> _parents;
    internal DefaultTrackedReads(ArchiveIdentityCheck identities, DefaultEcs.Entity[] entities,
        DefaultEcs.Components<ReferenceParent<Entity>> parents)
    { _identities = identities; _entities = entities; _parents = parents; }
    public bool IsAlive(Entity entity) => _identities.IsAlive(entity) && _entities[entity.Id].IsAlive;
    public Entity ReadParent(Entity entity)
    {
        if (!IsAlive(entity) || !_entities[entity.Id].Has<ReferenceParent<Entity>>()) throw new ArgumentException("Parent component is absent.");
        return _parents[_entities[entity.Id]].Value;
    }
}

internal readonly struct EasyTrackedRange(EasyEcs.Entity first, int count, EasyArchiveStore store) : INativeTrackedRange
{
    public void Execute(NativeTrackedContext context, ulong version, int chunk, int start)
        => context.Apply(MemoryMarshal.CreateSpan(ref first.Get<SaveCounter>(), count),
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first.Read<Position>()), count),
            MemoryMarshal.CreateSpan(ref first.Get<SaveNativeVersions>(), count), store.TrackedReads(), version, chunk, start);
}
internal readonly struct EasyTrackedQuery(EasyEcs.World world, EasyArchiveStore store) : INativeTrackedQuery<EasyTrackedRange>
{
    public void Build(NativeQueryRanges<EasyTrackedRange> ranges, NativeTrackedContext context)
    { var collect = new Collect(ranges, context, store); world.ForEachBatch<SaveCounter, Position, SaveNativeVersions, Collect>(ref collect); }
    public void Execute(NativeTrackedContext context, ulong version)
    { var apply = new Apply(context, version, store); world.ForEachBatch<SaveCounter, Position, SaveNativeVersions, Apply>(ref apply); }
    private struct Collect(NativeQueryRanges<EasyTrackedRange> ranges, NativeTrackedContext context, EasyArchiveStore store)
        : EasyEcs.IForEachBatch<SaveCounter, Position, SaveNativeVersions>
    {
        private int _chunk;
        public static int Writes => 0;
        public void Execute(EasyEcs.EntitySpan entities, Span<SaveCounter> counters, Span<Position> positions, Span<SaveNativeVersions> versions)
        {
            if (counters.Length == 0) return;
            int chunk = ++_chunk; context.BindChunk(chunk, counters.Length);
            for (int start = 0; start < counters.Length; start += 1024)
            {
                int count = Math.Min(1024, counters.Length - start);
                ranges.Add(new(entities[start], count, store), chunk, start, count);
            }
        }
    }
    private struct Apply(NativeTrackedContext context, ulong version, EasyArchiveStore store)
        : EasyEcs.IForEachBatch<SaveCounter, Position, SaveNativeVersions>
    {
        private int _chunk;
        public static int Writes => 5;
        public void Execute(EasyEcs.EntitySpan entities, Span<SaveCounter> counters, Span<Position> positions, Span<SaveNativeVersions> versions)
        {
            if (counters.Length == 0) return;
            int chunk = ++_chunk; context.BindChunk(chunk, counters.Length);
            context.Apply(counters, positions, versions, store.TrackedReads(), version, chunk, 0);
        }
    }
    public void Dispose() { }
}

internal readonly struct FrifloTrackedRange(Friflo.Engine.ECS.Chunk<SaveCounter> counters, Friflo.Engine.ECS.Chunk<Position> positions,
    Friflo.Engine.ECS.Chunk<SaveNativeVersions> versions, int first, int count, FrifloArchiveStore store) : INativeTrackedRange
{
    public void Execute(NativeTrackedContext context, ulong version, int chunk, int start)
        => context.Apply(counters.Span.Slice(first, count), positions.Span.Slice(first, count), versions.Span.Slice(first, count),
            store.TrackedReads(), version, chunk, start);
}
internal readonly struct FrifloTrackedQuery(Friflo.Engine.ECS.ArchetypeQuery<SaveCounter, Position, SaveNativeVersions> query,
    FrifloArchiveStore store) : INativeTrackedQuery<FrifloTrackedRange>
{
    public void Build(NativeQueryRanges<FrifloTrackedRange> ranges, NativeTrackedContext context)
    {
        int chunk = 0;
        foreach (var (counters, positions, versions, _) in query.Chunks)
        {
            if (counters.Length == 0) continue;
            context.BindChunk(++chunk, counters.Length);
            for (int start = 0; start < counters.Length; start += 1024)
            { int count = Math.Min(1024, counters.Length - start); ranges.Add(new(counters, positions, versions, start, count, store), chunk, start, count); }
        }
    }
    public void Execute(NativeTrackedContext context, ulong version)
    {
        int chunk = 0;
        foreach (var (counters, positions, versions, _) in query.Chunks)
        {
            if (counters.Length == 0) continue;
            context.BindChunk(++chunk, counters.Length);
            context.Apply(counters.Span, positions.Span, versions.Span, store.TrackedReads(), version, chunk, 0);
        }
    }
    public void Dispose() { }
}

internal readonly struct ArchTrackedRange(Arch.Core.Chunk chunk, int first, int count, ArchArchiveStore store) : INativeTrackedRange
{
    public void Execute(NativeTrackedContext context, ulong version, int chunkId, int start)
        => context.Apply(chunk.GetSpan<SaveCounter>().Slice(first, count), chunk.GetSpan<Position>().Slice(first, count),
            chunk.GetSpan<SaveNativeVersions>().Slice(first, count), store.TrackedReads(), version, chunkId, start);
}
internal readonly struct ArchTrackedQuery(Arch.Core.World world, ArchArchiveStore store) : INativeTrackedQuery<ArchTrackedRange>
{
    private readonly Arch.Core.QueryDescription _query = new Arch.Core.QueryDescription().WithAll<SaveCounter, Position, SaveNativeVersions>();
    public void Build(NativeQueryRanges<ArchTrackedRange> ranges, NativeTrackedContext context)
    {
        int chunkId = 0;
        foreach (ref var chunk in world.Query(in _query).GetChunkIterator())
        {
            if (chunk.Count == 0) continue;
            context.BindChunk(++chunkId, chunk.Count);
            for (int start = 0; start < chunk.Count; start += 1024)
            { int count = Math.Min(1024, chunk.Count - start); ranges.Add(new(chunk, start, count, store), chunkId, start, count); }
        }
    }
    public void Execute(NativeTrackedContext context, ulong version)
    {
        int chunkId = 0;
        foreach (ref var chunk in world.Query(in _query).GetChunkIterator())
        {
            if (chunk.Count == 0) continue;
            context.BindChunk(++chunkId, chunk.Count);
            context.Apply(chunk.GetSpan<SaveCounter>()[..chunk.Count], chunk.GetSpan<Position>()[..chunk.Count],
                chunk.GetSpan<SaveNativeVersions>()[..chunk.Count], store.TrackedReads(), version, chunkId, 0);
        }
    }
    public void Dispose() { }
}

internal readonly struct DefaultTrackedRange(DefaultEcs.World world, DefaultEcs.EntitySet query, int first, int count,
    DefaultArchiveStore store) : INativeTrackedRange
{
    public void Execute(NativeTrackedContext context, ulong version, int chunk, int start)
    {
        var counters = world.GetComponents<SaveCounter>(); var positions = world.GetComponents<Position>();
        var versions = world.GetComponents<SaveNativeVersions>(); var read = store.TrackedReads(); int value = context.ReadResource();
        foreach (ref readonly var entity in query.GetEntities().Slice(first, count))
        {
            ref SaveCounter counter = ref counters[entity];
            counter.Value += (int)positions[entity].X + value + context.ReadExtra(counter.Ordinal, ref read);
            versions[entity].Written = version;
        }
        context.StampWords(chunk, start, count, version);
    }
}
internal readonly struct DefaultTrackedQuery(DefaultEcs.World world, DefaultEcs.EntitySet query, DefaultArchiveStore store)
    : INativeTrackedQuery<DefaultTrackedRange>
{
    public void Build(NativeQueryRanges<DefaultTrackedRange> ranges, NativeTrackedContext context)
    {
        int length = query.GetEntities().Length; context.BindChunk(1, length);
        for (int start = 0; start < length; start += 1024)
        { int count = Math.Min(1024, length - start); ranges.Add(new(world, query, start, count, store), 1, start, count); }
    }
    public void Execute(NativeTrackedContext context, ulong version)
    {
        int length = query.GetEntities().Length; context.BindChunk(1, length);
        new DefaultTrackedRange(world, query, 0, length, store).Execute(context, version, 1, 0);
    }
    public void Dispose() => query.Dispose();
}
