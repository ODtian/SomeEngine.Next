using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

// An anchor is the first element of a public contiguous batch span. The shared Job
// structure resource retains its rows until the native references finish their work.
internal readonly struct EasyMotionRange(EasyEcs.Entity first, int count) : INativeQueryRange
{
    public void Execute() => ComponentMath.Move(MemoryMarshal.CreateSpan(ref first.Get<Position>(), count),
        MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in first.Read<Velocity>()), count));
}
internal readonly struct EasyMotionQuery(EasyEcs.World world) : INativeQueryRanges<EasyMotionRange>
{
    public int EntityCount => world.EntityCount;
    public void Execute() => EasyEcsQueries.Move(world);
    public void Build(NativeQueryRanges<EasyMotionRange> ranges)
    {
        var collect = new Collect(ranges);
        world.ForEachBatch<Position, Velocity, Collect>(ref collect);
    }
    private struct Collect(NativeQueryRanges<EasyMotionRange> ranges) : EasyEcs.IForEachBatch<Position, Velocity>
    {
        private int _chunk;
        public static int Writes => 0;
        public void Execute(EasyEcs.EntitySpan entities, Span<Position> positions, Span<Velocity> velocities)
        {
            int chunk = ++_chunk;
            for (int start = 0; start < positions.Length; start += 1024)
            {
                int count = Math.Min(1024, positions.Length - start);
                ranges.Add(new EasyMotionRange(entities[start], count), chunk, start, count);
            }
        }
    }
}
internal readonly struct FrifloMotionRange(Friflo.Engine.ECS.Chunk<Position> positions,
    Friflo.Engine.ECS.Chunk<Velocity> velocities, int start, int count) : INativeQueryRange
{
    public void Execute() => ComponentMath.Move(positions.Span.Slice(start, count), velocities.Span.Slice(start, count));
}
internal readonly struct FrifloMotionQuery(Friflo.Engine.ECS.EntityStore world, Friflo.Engine.ECS.ArchetypeQuery<Position, Velocity> query)
    : INativeQueryRanges<FrifloMotionRange>
{
    public int EntityCount => world.Count;
    public void Execute()
    {
        foreach (var (positions, velocities, _) in query.Chunks) ComponentMath.Move(positions.Span, velocities.Span);
    }
    public void Build(NativeQueryRanges<FrifloMotionRange> ranges)
    {
        int chunk = 0;
        foreach (var (positions, velocities, _) in query.Chunks)
        {
            chunk++;
            for (int start = 0; start < positions.Length; start += 1024)
            {
                int count = Math.Min(1024, positions.Length - start);
                ranges.Add(new FrifloMotionRange(positions, velocities, start, count), chunk, start, count);
            }
        }
    }
}
internal readonly struct ArchMotionRange(Arch.Core.Chunk chunk, int start, int count) : INativeQueryRange
{
    public void Execute() => ComponentMath.Move(chunk.GetSpan<Position>().Slice(start, count), chunk.GetSpan<Velocity>().Slice(start, count));
}
internal readonly struct ArchMotionQuery(Arch.Core.World world, Arch.Core.QueryDescription query) : INativeQueryRanges<ArchMotionRange>
{
    public int EntityCount => world.Size;
    public void Execute()
    {
        foreach (ref var chunk in world.Query(in query).GetChunkIterator())
            ComponentMath.Move(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count]);
    }
    public void Build(NativeQueryRanges<ArchMotionRange> ranges)
    {
        int chunkId = 0;
        foreach (ref var chunk in world.Query(in query).GetChunkIterator())
        {
            chunkId++;
            for (int start = 0; start < chunk.Count; start += 1024)
            {
                int count = Math.Min(1024, chunk.Count - start);
                ranges.Add(new ArchMotionRange(chunk, start, count), chunkId, start, count);
            }
        }
    }
}
internal readonly struct DefaultMotionRange(DefaultEcs.World world, int start, int count) : INativeQueryRange
{
    public void Execute() => ComponentMath.Move(world.GetAll<Position>().Slice(start, count), world.GetAll<Velocity>().Slice(start, count));
}
internal readonly struct DefaultMotionQuery(DefaultEcs.World world) : INativeQueryRanges<DefaultMotionRange>
{
    public int EntityCount => Math.Max(world.GetAll<Position>().Length, world.GetAll<Velocity>().Length);
    public void Execute() => ComponentMath.Move(world.GetAll<Position>(), world.GetAll<Velocity>());
    public void Build(NativeQueryRanges<DefaultMotionRange> ranges)
    {
        int length = world.GetAll<Position>().Length;
        for (int start = 0; start < length; start += 1024)
        {
            int count = Math.Min(1024, length - start);
            ranges.Add(new DefaultMotionRange(world, start, count), 1, start, count);
        }
    }
}
