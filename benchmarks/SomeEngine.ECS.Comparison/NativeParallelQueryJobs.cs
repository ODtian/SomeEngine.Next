namespace SomeEngine.ECS.Comparison;

internal sealed partial class SomeEngineWorld
{
    internal override void PrepareNativeQueryJobs()
        => QueryJobs = new SourceQueryJobs(_world, new SomeEngine.ECS.Systems.SystemRunner(_world, GetJobScheduler(), ((Count + 3) / 4 + 63) & ~63),
            _world.Query<Position, Velocity>());
}
internal sealed partial class EasyEcsWorld
{
    internal override void PrepareNativeQueryJobs() => QueryJobs = new EasyNativeParallelJobs(_world);
}
internal sealed class EasyNativeParallelJobs(EasyEcs.World world) : IQueryJobs
{
    private struct Move : EasyEcs.IForEach<Position, Velocity>, EasyEcs.INoStructuralChanges
    {
        public static int Writes => 1;
        public void Execute(ref Position position, ref Velocity velocity)
        { position.X += velocity.X; position.Y += velocity.Y; }
    }
    public void Run(bool scheduled, bool parallel, bool dependent)
    { var move = new Move(); world.ForEachParallel<Position, Velocity, Move>(ref move); }
    public void Dispose() { }
}

internal sealed partial class FrifloWorld
{
    internal override void PrepareNativeQueryJobs() => QueryJobs = new FrifloNativeParallelJobs(_world);
}
internal sealed class FrifloNativeParallelJobs : IQueryJobs
{
    private readonly Friflo.Engine.ECS.ParallelJobRunner _runner = new(4);
    private readonly Friflo.Engine.ECS.QueryJob<Position, Velocity> _job;
    internal FrifloNativeParallelJobs(Friflo.Engine.ECS.EntityStore world)
    {
        _job = world.Query<Position, Velocity>().ForEach(static (positions, velocities, entities)
            => ComponentMath.Move(positions.Span, velocities.Span));
        _job.JobRunner = _runner; _job.MinParallelChunkLength = 1024;
    }
    public void Run(bool scheduled, bool parallel, bool dependent) => _job.RunParallel();
    public void Dispose() => _runner.Dispose();
}

internal sealed partial class ArchWorld
{
    internal override void PrepareNativeQueryJobs() => QueryJobs = new ArchNativeParallelJobs(_world, _two);
}
internal sealed class ArchNativeParallelJobs : IQueryJobs
{
    private readonly Schedulers.JobScheduler _scheduler;
    private readonly Arch.Core.World _world;
    private readonly Arch.Core.QueryDescription _query;
    internal ArchNativeParallelJobs(Arch.Core.World world, Arch.Core.QueryDescription query)
    {
        _world = world; _query = query;
        _scheduler = new(new Schedulers.JobScheduler.Config
        { ThreadCount = 3, ThreadPrefixName = "ECS native Arch", MaxExpectedConcurrentJobs = 64, StrictAllocationMode = false });
        Arch.Core.World.SharedJobScheduler = _scheduler;
    }
    private readonly struct Move : Arch.Core.IChunkJob
    {
        public void Execute(ref Arch.Core.Chunk chunk)
            => ComponentMath.Move(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count]);
    }
    public void Run(bool scheduled, bool parallel, bool dependent)
    { var move = new Move(); _world.InlineParallelChunkQuery(in _query, in move); }
    public void Dispose() { _scheduler.Dispose(); Arch.Core.World.SharedJobScheduler = null!; }
}

internal sealed partial class DefaultEcsWorld
{
    internal override void PrepareNativeQueryJobs() => QueryJobs = new DefaultNativeParallelJobs(_world);
}
internal sealed class DefaultNativeParallelJobs(DefaultEcs.World world) : IQueryJobs, DefaultEcs.Threading.IParallelRunnable
{
    private readonly DefaultEcs.Threading.DefaultParallelRunner _runner = new(4);
    public void Run(bool scheduled, bool parallel, bool dependent) => _runner.Run(this);
    public void Run(int index, int maxIndex)
    {
        Span<Position> positions = world.GetAll<Position>(); ReadOnlySpan<Velocity> velocities = world.GetAll<Velocity>();
        int start = positions.Length * index / (maxIndex + 1), end = positions.Length * (index + 1) / (maxIndex + 1);
        ComponentMath.Move(positions[start..end], velocities[start..end]);
    }
    public void Dispose() => _runner.Dispose();
}
