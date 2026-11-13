using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

internal readonly struct EasyTripleRange(EasyEcs.Entity first, int count) : INativeTripleRange
{
    public Span<Position> Positions => MemoryMarshal.CreateSpan(ref first.Get<Position>(), count);
    public ReadOnlySpan<Velocity> Velocities => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first.Read<Velocity>()), count);
    public ReadOnlySpan<JobOrdinal> Ordinals => MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first.Read<JobOrdinal>()), count);
}
internal readonly struct EasyTripleQuery(EasyEcs.World world) : INativeTripleQuery<EasyTripleRange>
{
    public int EntityCount => world.EntityCount;
    public void Build(NativeQueryRanges<EasyTripleRange> ranges)
    { var collect = new Collect(ranges); world.ForEachBatch<Position, Velocity, JobOrdinal, Collect>(ref collect); }
    public void Execute<TAction>(ref TAction action) where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
    { var apply = new Apply<TAction>(action); world.ForEachBatch<Position, Velocity, JobOrdinal, Apply<TAction>>(ref apply); }
    private struct Collect(NativeQueryRanges<EasyTripleRange> ranges) : EasyEcs.IForEachBatch<Position, Velocity, JobOrdinal>
    {
        private int _chunk;
        public static int Writes => 0;
        public void Execute(EasyEcs.EntitySpan entities, Span<Position> positions, Span<Velocity> velocities, Span<JobOrdinal> ordinals)
        {
            int chunk = ++_chunk;
            for (int start = 0; start < positions.Length; start += 1024)
            {
                int count = Math.Min(1024, positions.Length - start);
                ranges.Add(new(entities[start], count), chunk, start, count);
            }
        }
    }
    private struct Apply<TAction>(TAction action) : EasyEcs.IForEachBatch<Position, Velocity, JobOrdinal>
        where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
    {
        public static int Writes => 1;
        public void Execute(EasyEcs.EntitySpan entities, Span<Position> positions, Span<Velocity> velocities, Span<JobOrdinal> ordinals)
            => action.Execute(positions, velocities, ordinals);
    }
}

internal readonly struct FrifloTripleRange(Friflo.Engine.ECS.Chunk<Position> positions, Friflo.Engine.ECS.Chunk<Velocity> velocities,
    Friflo.Engine.ECS.Chunk<JobOrdinal> ordinals, int start, int count) : INativeTripleRange
{
    public Span<Position> Positions => positions.Span.Slice(start, count);
    public ReadOnlySpan<Velocity> Velocities => velocities.Span.Slice(start, count);
    public ReadOnlySpan<JobOrdinal> Ordinals => ordinals.Span.Slice(start, count);
}
internal readonly struct FrifloTripleQuery(Friflo.Engine.ECS.EntityStore world,
    Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity, JobOrdinal> query) : INativeTripleQuery<FrifloTripleRange>
{
    public int EntityCount => world.Count;
    public void Execute<TAction>(ref TAction action) where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
    { foreach (var (positions, velocities, ordinals, _) in query.Chunks) action.Execute(positions.Span, velocities.Span, ordinals.Span); }
    public void Build(NativeQueryRanges<FrifloTripleRange> ranges)
    {
        int chunk = 0;
        foreach (var (positions, velocities, ordinals, _) in query.Chunks)
        {
            chunk++;
            for (int start = 0; start < positions.Length; start += 1024)
            {
                int count = Math.Min(1024, positions.Length - start);
                ranges.Add(new(positions, velocities, ordinals, start, count), chunk, start, count);
            }
        }
    }
}

internal readonly struct ArchTripleRange(Arch.Core.Chunk chunk, int start, int count) : INativeTripleRange
{
    public Span<Position> Positions => chunk.GetSpan<Position>().Slice(start, count);
    public ReadOnlySpan<Velocity> Velocities => chunk.GetSpan<Velocity>().Slice(start, count);
    public ReadOnlySpan<JobOrdinal> Ordinals => chunk.GetSpan<JobOrdinal>().Slice(start, count);
}
internal readonly struct ArchTripleQuery(Arch.Core.World world, Arch.Core.QueryDescription query) : INativeTripleQuery<ArchTripleRange>
{
    public int EntityCount => world.Size;
    public void Execute<TAction>(ref TAction action) where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
    {
        foreach (ref var chunk in world.Query(in query).GetChunkIterator())
            action.Execute(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count], chunk.GetSpan<JobOrdinal>()[..chunk.Count]);
    }
    public void Build(NativeQueryRanges<ArchTripleRange> ranges)
    {
        int chunkId = 0;
        foreach (ref var chunk in world.Query(in query).GetChunkIterator())
        {
            chunkId++;
            for (int start = 0; start < chunk.Count; start += 1024)
            { int count = Math.Min(1024, chunk.Count - start); ranges.Add(new(chunk, start, count), chunkId, start, count); }
        }
    }
}

internal readonly struct DefaultTripleRange(DefaultEcs.World world, int start, int count) : INativeTripleRange
{
    public Span<Position> Positions => world.GetAll<Position>().Slice(start, count);
    public ReadOnlySpan<Velocity> Velocities => world.GetAll<Velocity>().Slice(start, count);
    public ReadOnlySpan<JobOrdinal> Ordinals => world.GetAll<JobOrdinal>().Slice(start, count);
}
internal readonly struct DefaultTripleQuery(DefaultEcs.World world) : INativeTripleQuery<DefaultTripleRange>
{
    public int EntityCount => Math.Max(world.GetAll<Position>().Length, Math.Max(world.GetAll<Velocity>().Length, world.GetAll<JobOrdinal>().Length));
    public void Execute<TAction>(ref TAction action) where TAction : struct, IColumnPass<Position, Velocity, JobOrdinal>
    {
        ReadOnlySpan<JobOrdinal> ordinals = world.GetAll<JobOrdinal>();
        action.Execute(world.GetAll<Position>()[..ordinals.Length], world.GetAll<Velocity>()[..ordinals.Length], ordinals);
    }
    public void Build(NativeQueryRanges<DefaultTripleRange> ranges)
    {
        int length = world.GetAll<JobOrdinal>().Length;
        for (int start = 0; start < length; start += 1024)
        { int count = Math.Min(1024, length - start); ranges.Add(new(world, start, count), 1, start, count); }
    }
}

internal sealed partial class EasyEcsWorld
{
    internal override int ReadJobExtra(int index) => _entities[index].Read<JobExtra>().Value;
    private void PrepareRandomQueryJobs(int mode)
    {
        if (!_randomJobColumnsReady)
            for (int i = 0; i < _entities.Length; i++)
        { _entities[i].Set(new JobOrdinal(i)); if (mode == 2) _entities[i].Set(new JobExtra()); }
        var query = new EasyTripleQuery(_world);
        var api = new EasyRandomApi(_entities);
        QueryJobs = mode == 1
            ? new NativeQueryJobs<EasyTripleRange, EasyTripleQuery, NativeReadRandom<EasyRandomApi>>(GetJobScheduler(), query, new(api), false)
            : new NativeQueryJobs<EasyTripleRange, EasyTripleQuery, NativeWriteRandom<EasyRandomApi>>(GetJobScheduler(), query, new(api), true);
    }
}
internal sealed partial class FrifloWorld
{
    internal override int ReadJobExtra(int index) => _entities[index].GetComponent<JobExtra>().Value;
    private void PrepareRandomQueryJobs(int mode)
    {
        if (!_randomJobColumnsReady)
            for (int i = 0; i < _entities.Length; i++)
        { _entities[i].AddComponent(new JobOrdinal(i)); if (mode == 2) _entities[i].AddComponent(new JobExtra()); }
        var query = new FrifloTripleQuery(_world, _world.Query<Position, Velocity, JobOrdinal>());
        var api = new FrifloRandomApi(_entities);
        QueryJobs = mode == 1
            ? new NativeQueryJobs<FrifloTripleRange, FrifloTripleQuery, NativeReadRandom<FrifloRandomApi>>(GetJobScheduler(), query, new(api), false)
            : new NativeQueryJobs<FrifloTripleRange, FrifloTripleQuery, NativeWriteRandom<FrifloRandomApi>>(GetJobScheduler(), query, new(api), true);
    }
}
internal sealed partial class ArchWorld
{
    internal override int ReadJobExtra(int index) => _world.Get<JobExtra>(_entities[index]).Value;
    private void PrepareRandomQueryJobs(int mode)
    {
        if (!_randomJobColumnsReady)
            for (int i = 0; i < _entities.Length; i++)
        { _world.Add(_entities[i], new JobOrdinal(i)); if (mode == 2) _world.Add(_entities[i], new JobExtra()); }
        var query = new ArchTripleQuery(_world, new Arch.Core.QueryDescription().WithAll<Position, Velocity, JobOrdinal>());
        var api = new ArchRandomApi(_world, _entities);
        QueryJobs = mode == 1
            ? new NativeQueryJobs<ArchTripleRange, ArchTripleQuery, NativeReadRandom<ArchRandomApi>>(GetJobScheduler(), query, new(api), false)
            : new NativeQueryJobs<ArchTripleRange, ArchTripleQuery, NativeWriteRandom<ArchRandomApi>>(GetJobScheduler(), query, new(api), true);
    }
}
internal sealed partial class DefaultEcsWorld
{
    internal override int ReadJobExtra(int index) => _entities[index].Get<JobExtra>().Value;
    private void PrepareRandomQueryJobs(int mode)
    {
        for (int i = 0; i < _entities.Length; i++)
        { _entities[i].Set(new JobOrdinal(i)); if (mode == 2) _entities[i].Set(new JobExtra()); }
        var query = new DefaultTripleQuery(_world);
        QueryJobs = mode == 1
            ? new NativeQueryJobs<DefaultTripleRange, DefaultTripleQuery, DefaultReadRandom>(GetJobScheduler(), query, new(_world, _entities), false)
            : new NativeQueryJobs<DefaultTripleRange, DefaultTripleQuery, DefaultWriteRandom>(GetJobScheduler(), query, new(_world, _entities), true);
    }
}
