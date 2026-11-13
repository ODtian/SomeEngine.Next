using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Comparison;

internal abstract class TrackedArchiveJobs(FullArchiveWorld world, Entity[] entities) : IDisposable
{
    protected readonly FullArchiveWorld ArchiveWorld = world;
    protected readonly Entity[] Entities = entities;
    protected ulong BeforeVersion;
    protected int LastSteps;
    internal int Steps { get; private set; }
    private int _growthStart;
    internal void RecordGrowth() => _growthStart = Steps;
    protected void BeginRun(bool dependent) { BeforeVersion = ArchiveWorld.Version; LastSteps = dependent ? 2 : 1; }
    protected void EndRun() => Steps += LastSteps;
    internal abstract void Run(string mode);
    protected abstract bool HasCounter(Entity entity);
    protected abstract SaveCounter ReadCounter(Entity entity);
    protected abstract void CheckChanges(int count);
    internal void Check()
    {
        if (ArchiveWorld.Version != BeforeVersion + (ulong)LastSteps)
            throw new InvalidOperationException("Tracked query batches must publish one write version each.");
        CheckValues(Entities, Steps); CheckValues(ArchiveWorld.GrowthEntities, Steps - _growthStart);
        int count = Entities.Length + ArchiveWorld.GrowthEntities.Count;
        int absent = (Entities.Length + 99) / 100 + (ArchiveWorld.GrowthEntities.Count + 99) / 100;
        CheckChanges(count - absent);
    }
    private void CheckValues(IReadOnlyList<Entity> entities, int steps)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            Entity entity = entities[i];
            if (HasCounter(entity) != (i % 100 != 0)) throw new InvalidOperationException("Tracked query primary membership differs.");
            if (i % 100 == 0) continue;
            SaveCounter value = ReadCounter(entity);
            if (value.Ordinal != i || value.Value != i + 2 + steps * Increment(i, Entities.Length))
                throw new InvalidOperationException("Tracked query result differs at " + i);
        }
    }
    internal static int Pick(int ordinal, int count) => (int)((long)ordinal * 7919 % count);
    internal static int Increment(int ordinal, int count)
    {
        int target = Pick(ordinal, count);
        return ordinal + 1 + (target % 4 == 0 ? 1 + (int)(target * 7L & 7) : 0);
    }
    public abstract void Dispose();
}

internal sealed partial class SourceFullArchiveWorld
{
    internal override TrackedArchiveJobs ConnectTrackedJobs(Scheduler scheduler) => new SourceTrackedArchiveJobs(this, scheduler);
}

internal sealed partial class SourceTrackedArchiveJobs : TrackedArchiveJobs
{
    private readonly World _world;
    private readonly Query _query;
    private readonly SystemRunner _runner;
    private readonly Apply _action;
    private JobHandle _last;
    internal SourceTrackedArchiveJobs(SourceFullArchiveWorld world, Scheduler scheduler) : base(world, world.Entities)
    {
        _world = world.World; _query = _world.Query<SaveCounter, Position>(); _runner = new(_world, scheduler);
        _action = new(_world, world.Entities, world.SecondRoot);
    }
    internal override void Run(string mode)
    {
        bool dependent = mode == "dependent"; BeginRun(dependent);
        if (mode == "execute") SystemRunner.Execute(_world, _action, _query);
        else if (mode == "parallel") _runner.ExecuteParallel(_query, _action);
        else
        {
            _last = _runner.Schedule(_query, _action);
            if (dependent) _last = _runner.Schedule(_query, _action, _last);
            _last.Complete();
        }
        EndRun();
    }
    protected override bool HasCounter(Entity entity) => _world.Has<SaveCounter>(entity);
    protected override SaveCounter ReadCounter(Entity entity) => _world.ReadValue<SaveCounter>(entity);
    protected override void CheckChanges(int count)
    {
        if (_world.Query(new QueryDescription().WithChanged<SaveCounter>(BeforeVersion)).Count != count
            || _world.Query(new QueryDescription().WithChanged<SaveCounter>(_world.Version - 1)).Count != count
            || _world.Query(new QueryDescription().WithAdded<SaveCounter>(BeforeVersion)).Count != 0)
            throw new InvalidOperationException("Source tracked query exact changes and summaries differ.");
    }
    [JobRead(typeof(Parent<SaveTree>))]
    [JobRead(typeof(SaveLink), JobStorage.Sparse)]
    [JobRead(typeof(ResourceData<ArchiveResources.Kind0>), JobStorage.Resource)]
    private readonly partial struct Apply(World world, Entity[] entities, Entity secondRoot) : IJobChunk
    {
        public void Execute(Span<SaveCounter> counters, ReadOnlySpan<Position> positions)
        {
            using var parents = world.OpenRead<Parent<SaveTree>>(); using var sparse = world.OpenSparseRead<SaveLink>();
            using var resource = world.OpenResourceRead<ResourceData<ArchiveResources.Kind0>>();
            int value = (int)resource.Value.Value;
            for (int i = 0; i < counters.Length; i++)
            {
                Entity target = entities[Pick(counters[i].Ordinal, entities.Length)];
                int extra = parents.Read(target).Value == secondRoot ? 1 : 0;
                if (sparse.Has(target)) extra += (int)(sparse.Read(target).Value & 7);
                counters[i].Value += (int)positions[i].X + value + extra;
            }
        }
    }
    public override void Dispose() => _last.Complete();
}
