using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

public readonly record struct JobOrdinal(int Value) : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent;
public struct JobExtra : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent { public int Value; }

internal static class RandomJobInputs
{
    internal static int Pick(int ordinal, int count) => (int)((long)ordinal * 7_919 % count);
}
internal interface IRandomJobApi
{
    int Count { get; }
    float ReadVelocityX(int index);
    void IncrementExtra(int index);
}
internal struct NativeReadRandom<TApi>(TApi api) : IColumnPass<Position, Velocity, JobOrdinal>
    where TApi : struct, IRandomJobApi
{
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
    {
        for (int row = 0; row < positions.Length; row++)
        {
            positions[row].X += velocities[row].X + api.ReadVelocityX(RandomJobInputs.Pick(ordinals[row].Value, api.Count));
            positions[row].Y += velocities[row].Y;
        }
    }
}
internal struct NativeWriteRandom<TApi>(TApi api) : IColumnPass<Position, Velocity, JobOrdinal>
    where TApi : struct, IRandomJobApi
{
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
    {
        for (int row = 0; row < positions.Length; row++)
        {
            positions[row].X += velocities[row].X; positions[row].Y += velocities[row].Y;
            api.IncrementExtra(RandomJobInputs.Pick(ordinals[row].Value, api.Count));
        }
    }
}

internal sealed partial class SomeEngineWorld
{
    internal override int ReadJobExtra(int index) => _world.ReadValue<JobExtra>(_entities[index]).Value;
    private void PrepareRandomQueryJobs(int mode)
    {
        using var structure = _world.OpenStructureWrite();
        if (!_randomJobColumnsReady)
            for (int i = 0; i < _entities.Length; i++)
            { _world.Add(_entities[i], new JobOrdinal(i)); if (mode == 2) _world.Add(_entities[i], new JobExtra()); }
        if (mode == 1) { using var binding = _world.OpenRead<Velocity>(); }
        else { using var binding = _world.OpenWrite<JobExtra>(); }
        Query query = _world.Query<Position, Velocity, JobOrdinal>();
        var runner = new SystemRunner(_world, GetJobScheduler());
        QueryJobs = mode == 1
            ? new SourceRandomJobs<ReadRandomJob>(_world, runner, query, new(_world, _entities))
            : new SourceRandomJobs<WriteRandomJob>(_world, runner, query, new(_world, _entities));
    }
    [JobRead(typeof(Velocity))]
    private readonly partial struct ReadRandomJob(World world, Entity[] entities) : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
        {
            using ReadLookup<Velocity> lookup = world.OpenRead<Velocity>();
            for (int row = 0; row < positions.Length; row++)
            {
                positions[row].X += velocities[row].X + lookup.Read(entities[RandomJobInputs.Pick(ordinals[row].Value, entities.Length)]).X;
                positions[row].Y += velocities[row].Y;
            }
        }
    }
    [JobWrite(typeof(JobExtra))]
    private readonly partial struct WriteRandomJob(World world, Entity[] entities) : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
        {
            using WriteLookup<JobExtra> lookup = world.OpenWrite<JobExtra>();
            for (int row = 0; row < positions.Length; row++)
            {
                positions[row].X += velocities[row].X; positions[row].Y += velocities[row].Y;
                lookup.Get(entities[RandomJobInputs.Pick(ordinals[row].Value, entities.Length)]).Value++;
            }
        }
    }
    private sealed class SourceRandomJobs<TAction>(World world, SystemRunner runner, Query query, TAction action) : IQueryJobs
        where TAction : struct, IChunkAction
    {
        public void Run(bool scheduled, bool parallel, bool dependent)
        {
            if (scheduled)
            {
                JobHandle first = runner.Schedule(query, action, parallel: parallel);
                (dependent ? runner.Schedule(query, action, first, parallel) : first).Complete();
            }
            else if (parallel) runner.ExecuteParallel(query, action);
            else SystemRunner.Execute(world, action, query);
        }
        public void Dispose() { }
    }
}

internal readonly struct EasyRandomApi(EasyEcs.Entity[] entities) : IRandomJobApi
{
    public int Count => entities.Length;
    public float ReadVelocityX(int index) => entities[index].Read<Velocity>().X;
    public void IncrementExtra(int index) => entities[index].Get<JobExtra>().Value++;
}
internal readonly struct FrifloRandomApi(Friflo.Engine.ECS.Entity[] entities) : IRandomJobApi
{
    public int Count => entities.Length;
    public float ReadVelocityX(int index) => entities[index].GetComponent<Velocity>().X;
    public void IncrementExtra(int index) => entities[index].GetComponent<JobExtra>().Value++;
}
internal readonly struct ArchRandomApi(Arch.Core.World world, Arch.Core.Entity[] entities) : IRandomJobApi
{
    public int Count => entities.Length;
    public float ReadVelocityX(int index)
    {
        Arch.Core.Entity entity = entities[index];
        if (!world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
        ref Velocity value = ref world.TryGetRef<Velocity>(entity, out bool exists);
        if (!exists) throw new ArgumentException("Component is absent.");
        return value.X;
    }
    public void IncrementExtra(int index)
    {
        Arch.Core.Entity entity = entities[index];
        if (!world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
        ref JobExtra value = ref world.TryGetRef<JobExtra>(entity, out bool exists);
        if (!exists) throw new ArgumentException("Component is absent.");
        value.Value++;
    }
}
internal readonly struct DefaultReadRandom(DefaultEcs.World world, DefaultEcs.Entity[] entities) : IColumnPass<Position, Velocity, JobOrdinal>
{
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
    {
        var lookup = world.GetComponents<Velocity>();
        for (int row = 0; row < positions.Length; row++)
        {
            DefaultEcs.Entity entity = entities[RandomJobInputs.Pick(ordinals[row].Value, entities.Length)];
            if (!entity.IsAlive || !entity.Has<Velocity>()) throw new ArgumentException("Entity component is absent.");
            positions[row].X += velocities[row].X + lookup[entity].X;
            positions[row].Y += velocities[row].Y;
        }
    }
}
internal readonly struct DefaultWriteRandom(DefaultEcs.World world, DefaultEcs.Entity[] entities) : IColumnPass<Position, Velocity, JobOrdinal>
{
    public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<JobOrdinal> ordinals)
    {
        var lookup = world.GetComponents<JobExtra>();
        for (int row = 0; row < positions.Length; row++)
        {
            positions[row].X += velocities[row].X; positions[row].Y += velocities[row].Y;
            DefaultEcs.Entity entity = entities[RandomJobInputs.Pick(ordinals[row].Value, entities.Length)];
            if (!entity.IsAlive || !entity.Has<JobExtra>()) throw new ArgumentException("Entity component is absent.");
            lookup[entity].Value++;
        }
    }
}
