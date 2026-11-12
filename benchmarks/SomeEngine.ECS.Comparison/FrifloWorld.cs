using Friflo.Engine.ECS;
using static Friflo.Engine.ECS.EntityStoreExtensions;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class FrifloWorld : ComparisonWorld
{
    private readonly EntityStore _world = new();
    private readonly Friflo.Engine.ECS.Entity[] _entities;
    private readonly Friflo.Engine.ECS.Entity[] _created = new Friflo.Engine.ECS.Entity[Changes];
    private readonly Friflo.Engine.ECS.Entity[] _lookupEntities = new Friflo.Engine.ECS.Entity[Lookups];
    private ArchetypeQuery<Health>? _one, _filtered;
    private ArchetypeQuery<Position, Velocity>? _two;
    private ArchetypeQuery<Position, Velocity, Health>? _three;

    internal FrifloWorld(int count, bool signalInInitialLayout = false, string? initialWorkload = null) : base(count, signalInInitialLayout)
    {
        _world.EnsureCapacity(count + Changes);
        _entities = new Friflo.Engine.ECS.Entity[count];
        if (SignalInInitialLayout)
        {
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Team, Sleeping, Signal>(), default).EnsureCapacity(count / 4);
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Team, Signal>(), default).EnsureCapacity(count / 4);
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Signal>(), default).EnsureCapacity(count / 2 + Changes);
            CreateSignalEntities();
        }
        else if (!CreateInitialFeatureEntities(initialWorkload))
        {
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health>(), default).EnsureCapacity(count / 2 + Changes);
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Team>(), default).EnsureCapacity(count / 4);
            _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Team, Sleeping>(), default).EnsureCapacity(count / 4);
            PositionInputs positions = PositionInputs.Get(count);
            var batch = _world.Batch(autoReturn: false)
                .Add(new Position()).Add(new Velocity { X = 1, Y = 2 }).Add(new Health { Value = 100, Max = 100 });
            for (int i = 1; i < count; i += 2)
            {
                var entity = _entities[i] = batch.CreateEntity();
                entity.GetComponent<Position>() = positions.Plain[i / 2];
            }
            batch.Return();
            for (int i = 0; i < count; i += 2)
            {
                Position position = (i % 4 == 0 ? positions.Sleeping : positions.Team)[i / 4];
                var velocity = new Velocity { X = 1, Y = 2 };
                var health = new Health { Value = 100, Max = 100 };
                _entities[i] = i % 4 == 0 ? _world.CreateEntity(position, velocity, health, new Team(), new Sleeping())
                    : _world.CreateEntity(position, velocity, health, new Team());
            }
        }
        for (int i = 0; i < Lookups; i++) _lookupEntities[i] = _entities[Pick(i)];
    }

    private void CreateSignalEntities()
    {
        for (int shape = 0; shape < 3; shape++)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            var batch = _world.Batch(autoReturn: false).Add(new Position()).Add(new Velocity { X = 1, Y = 2 })
                .Add(new Health { Value = 100, Max = 100 }).Add(new Signal());
            if (shape != 2) batch.Add(new Team());
            if (shape == 0) batch.Add(new Sleeping());
            ref Position position = ref batch.Get<Position>();
            for (int row = 0; row < Count / stride; row++)
            {
                int ordinal = row * stride + first;
                position.X = ordinal;
                _entities[ordinal] = batch.CreateEntity();
            }
            batch.Return();
        }
    }

    internal override void Prepare(string workload)
    {
        if (workload.StartsWith("relation-")) { PrepareRelations(workload); return; }
        if (workload.StartsWith("hierarchy-")) { PrepareHierarchy(workload); return; }
        if (workload.StartsWith("query-")) { PrepareQueryAppend(workload); return; }
        if (workload.StartsWith("group-")) { PrepareGroups(workload); return; }
        if (workload.StartsWith("sparse-")) { PrepareSparse(workload); return; }
        if (workload.StartsWith("index-")) { PrepareIndex(workload); return; }
        if (workload.StartsWith("shared-")) { PrepareShared(workload); return; }
        switch (workload)
        {
            case "add-remove":
                _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Extra>(), default).EnsureCapacity(Changes / 2);
                _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Extra, Team>(), default).EnsureCapacity(Changes / 4);
                _world.GetArchetype(ComponentTypes.Get<Position, Velocity, Health, Extra, Team, Sleeping>(), default).EnsureCapacity(Changes / 4);
                break;
            case "resources": PrepareResources(); break;
            case "enableable": PrepareEnabled(); break;
            case "changes-1":
            case "changes-25": PrepareChanges(workload); break;
            case "buffer-4":
            case "buffer-32": PrepareBuffers(workload); break;
            case "iterate-one": _one ??= _world.Query<Health>(); break;
            case "iterate-two":
            case "iterate-simd": _two ??= _world.Query<Position, Velocity>(); break;
            case "iterate-three": _three ??= _world.Query<Position, Velocity, Health>(); break;
            case "iterate-filtered": _filtered ??= _world.Query<Health>().AllComponents(ComponentTypes.Get<Team>()).WithoutAnyComponents(ComponentTypes.Get<Sleeping>()); break;
        }
    }

    internal override void IterateOne()
    {
        foreach (var (health, _) in _one!.Chunks) ComponentMath.Flip(health.Span);
    }
    internal override void IterateTwo()
    {
        foreach (var (position, velocity, _) in _two!.Chunks) ComponentMath.Move(position.Span, velocity.Span);
    }
    internal override void IterateSimd()
    {
        foreach (var (position, velocity, _) in _two!.Chunks) ComponentMath.MoveSimd(position.Span, velocity.Span);
    }
    internal override void IterateThree()
    {
        foreach (var (position, velocity, health, _) in _three!.Chunks) ComponentMath.ReadThree(position.Span, velocity.Span, health.Span);
    }
    internal override void IterateFiltered()
    {
        foreach (var (health, _) in _filtered!.Chunks) ComponentMath.Flip(health.Span);
    }

    internal override void ReadRandom()
    {
        long total = 0;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)entity.GetComponent<Position>().X;
        ReadTotal = total;
    }
    internal override void ReplaceRandom()
    {
        Position replacement = ReplacementPosition;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) entity.AddComponent(replacement);
    }

    internal override void ReplaceBatch() => ReplaceRandom();

    internal override void WriteRandom()
    {
        long replacement = ReplacementBits;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref entity.GetComponent<Position>(), replacement);
    }
    internal override void AddRemove()
    {
        if (_groupedValues is not null) { _groupedValues.AddRemove(Changes); return; }
        for (int i = 0; i < Changes; i++) _entities[i].AddComponent(new Extra { Value = 7 });
        for (int i = 0; i < Changes; i++) _entities[i].RemoveComponent<Extra>();
    }
    internal override void CreateDestroyOne()
    {
        for (int i = 0; i < Changes; i++) _created[i] = _world.CreateEntity(new Position { X = i });
        DestroyCreated();
    }
    internal override void CreateDestroyThree()
    {
        for (int i = 0; i < Changes; i++) _created[i] = _world.CreateEntity(new Position { X = i }, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        DestroyCreated();
    }
    internal override void SpawnBatch()
    {
        var batch = _world.Batch(autoReturn: false)
            .Add(new Position()).Add(new Velocity { X = 1, Y = 2 }).Add(new Health { Value = 100, Max = 100 });
        for (int i = 0; i < Changes; i++) _created[i] = batch.CreateEntity();
        batch.Return();
        DestroyCreated();
    }
    private void DestroyCreated()
    {
        for (int i = 0; i < Changes; i++) _created[i].DeleteEntity();
    }
    internal override Position ReadPosition(int index) => _entities[index].GetComponent<Position>();
    internal override void GrowAndReclaim(Action observe)
    {
        if (CopyScenario is { } copying) { copying.Grow(); observe(); return; }
        if (CommandScenario is { } commands)
        {
            _world.GetArchetype(ComponentTypes.Get<Position>(), default).EnsureCapacity(Count);
            commands.Grow(); observe(); return;
        }
        if (HierarchyScenario is { } hierarchy) { hierarchy.Grow(); observe(); return; }
        if (RelationScenario is { } relations) { relations.Grow(); observe(); return; }
        if (QueryAppendReady) { if (QueryBindingOnly) QueryAppendCycle(); else observe(); return; }
        if (GrowResources(observe)) return;
        var created = new Friflo.Engine.ECS.Entity[Count];
        var batch = _world.Batch(autoReturn: false)
            .Add(new Position()).Add(new Velocity { X = 1, Y = 2 }).Add(new Health { Value = 100, Max = 100 });
        for (int i = 0; i < Count; i++) created[i] = batch.CreateEntity();
        batch.Return();
        created[0].AddComponent(new GrowthMarker { Value = 1 });
        if (GrowFeatureValues(created)) { observe(); return; }
        observe();
        foreach (var entity in created) entity.DeleteEntity();
    }

    internal override Health ReadHealth(int index) => _entities[index].GetComponent<Health>();
    internal override int EntityCount => _groupedValues?.EntityCount ?? _world.Count;
    public override void Dispose()
    {
        CopyScenario?.Dispose();
        CommandScenario?.Dispose();
        HierarchyScenario?.Dispose();
        RelationScenario?.Dispose();
        QueryJobs?.Dispose();
        _groupedValues?.Dispose();
        _sparseValues?.Dispose();
        ReleaseBuffers();
        ReleaseComparisonResources();
        JobScheduler?.Dispose();
    }
}
