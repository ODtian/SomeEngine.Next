namespace SomeEngine.ECS.Comparison;

internal sealed partial class DefaultEcsWorld : ComparisonWorld
{
    private readonly DefaultEcs.World _world = new();
    private readonly DefaultEcs.Entity[] _entities;
    private readonly DefaultEcs.Entity[] _created = new DefaultEcs.Entity[Changes];
    private readonly DefaultEcs.Entity[] _lookupEntities = new DefaultEcs.Entity[Lookups];
    private DefaultEcs.EntitySet? _filtered;

    internal DefaultEcsWorld(int count) : base(count)
    {
        _entities = new DefaultEcs.Entity[count];
        for (int i = 0; i < count; i++)
        {
            var entity = _entities[i] = _world.CreateEntity();
            entity.Set(new Position { X = i });
            entity.Set(new Velocity { X = 1, Y = 2 });
            entity.Set(new Health { Value = 100, Max = 100 });
            if (i % 2 == 0) entity.Set(new Team());
            if (i % 4 == 0) entity.Set(new Sleeping());
        }
        for (int i = 0; i < Lookups; i++) _lookupEntities[i] = _entities[Pick(i)];
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
            case "resources": PrepareResources(); break;
            case "enableable": PrepareEnabled(); break;
            case "changes-1":
            case "changes-25": PrepareChanges(workload); break;
            case "buffer-4":
            case "buffer-32": PrepareBuffers(workload); break;
            case "iterate-filtered": _filtered ??= _world.GetEntities().With<Health>().With<Team>().Without<Sleeping>().AsSet(); break;
        }
    }

    internal override void IterateOne() => ComponentMath.Flip(_world.GetAll<Health>());
    internal override void IterateTwo() => ComponentMath.Move(_world.GetAll<Position>(), _world.GetAll<Velocity>());
    internal override void IterateSimd() => ComponentMath.MoveSimd(_world.GetAll<Position>(), _world.GetAll<Velocity>());
    internal override void IterateThree()
        => ComponentMath.ReadThree(_world.GetAll<Position>(), _world.GetAll<Velocity>(), _world.GetAll<Health>());
    internal override void IterateFiltered()
    {
        var health = _world.GetComponents<Health>();
        foreach (ref readonly var entity in _filtered!.GetEntities()) health[entity].Max ^= 1;
    }
    internal override void ReadRandom()
    {
        long total = 0;
        var positions = _world.GetComponents<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions[entity].X;
        ReadTotal = total;
    }
    internal override void ReplaceRandom()
    {
        Position replacement = ReplacementPosition;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) entity.Set(replacement);
    }

    internal override void WriteRandom()
    {
        long replacement = ReplacementBits;
        var positions = _world.GetComponents<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref positions[entity], replacement);
    }

    internal override void ReadRandomChecked()
    {
        long total = 0;
        var positions = _world.GetComponents<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan())
        {
            if (!entity.IsAlive || !entity.Has<Position>()) throw new ArgumentException("Entity component is absent.");
            total += (int)positions[entity].X;
        }
        ReadTotal = total;
    }

    internal override void WriteRandomChecked()
    {
        long replacement = ReplacementBits;
        var positions = _world.GetComponents<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan())
        {
            if (!entity.IsAlive || !entity.Has<Position>()) throw new ArgumentException("Entity component is absent.");
            ComponentMath.WritePosition(ref positions[entity], replacement);
        }
    }
    internal override void AddRemove()
    {
        if (_groupedValues is not null) { _groupedValues.AddRemove(Changes); return; }
        for (int i = 0; i < Changes; i++) _entities[i].Set(new Extra { Value = 7 });
        for (int i = 0; i < Changes; i++) _entities[i].Remove<Extra>();
    }
    internal override void CreateDestroyOne()
    {
        for (int i = 0; i < Changes; i++)
        {
            _created[i] = _world.CreateEntity();
            _created[i].Set(new Position { X = i });
        }
        DestroyCreated();
    }
    internal override void CreateDestroyThree()
    {
        for (int i = 0; i < Changes; i++)
        {
            var entity = _created[i] = _world.CreateEntity();
            entity.Set(new Position { X = i });
            entity.Set(new Velocity { X = 1, Y = 2 });
            entity.Set(new Health { Value = 100, Max = 100 });
        }
        DestroyCreated();
    }
    internal override void SpawnBatch()
    {
        for (int i = 0; i < Changes; i++)
        {
            var entity = _created[i] = _world.CreateEntity();
            entity.Set(new Position());
            entity.Set(new Velocity { X = 1, Y = 2 });
            entity.Set(new Health { Value = 100, Max = 100 });
        }
        DestroyCreated();
    }
    private void DestroyCreated()
    {
        for (int i = 0; i < Changes; i++) _created[i].Dispose();
    }
    internal override Position ReadPosition(int index) => _entities[index].Get<Position>();
    internal override void GrowAndReclaim(Action observe)
    {
        if (CopyScenario is { } copying) { copying.Grow(); observe(); return; }
        if (CommandScenario is { } commands) { commands.Grow(); observe(); return; }
        if (HierarchyScenario is { } hierarchy) { hierarchy.Grow(); observe(); return; }
        if (RelationScenario is { } relations) { relations.Grow(); observe(); return; }
        if (QueryAppendReady) { if (QueryBindingOnly) QueryAppendCycle(); else observe(); return; }
        if (GrowResources(observe)) return;
        var created = new DefaultEcs.Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            var entity = created[i] = _world.CreateEntity();
            entity.Set(new Position());
            entity.Set(new Velocity { X = 1, Y = 2 });
            entity.Set(new Health { Value = 100, Max = 100 });
        }
        created[0].Set(new GrowthMarker { Value = 1 });
        if (GrowFeatureValues(created)) { observe(); return; }
        observe();
        foreach (var entity in created) entity.Dispose();
    }

    internal override Health ReadHealth(int index) => _entities[index].Get<Health>();
    internal override int EntityCount => _groupedValues?.EntityCount ?? _world.Count();
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
        _queryAppend?.Dispose();
        _filtered?.Dispose();
        _enabledSignals?.Dispose();
        _disabledSignals?.Dispose();
        _changedScores?.Dispose();
        _addedScores?.Dispose();
        _removedScores?.Dispose();
        _sharedGroups?.Dispose();
        _numberIndex?.Dispose();
        _world.Dispose();
        JobScheduler?.Dispose();
    }
}
