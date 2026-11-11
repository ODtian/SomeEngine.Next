using EasyEcs;

namespace SomeEngine.ECS.Comparison;

internal static class EasyEcsQueries
{
    [ForEach] internal static void Flip(Span<Health> health) => ComponentMath.Flip(health);
    [ForEach] internal static void Move(Span<Position> positions, ReadOnlySpan<Velocity> velocities) => ComponentMath.Move(positions, velocities);
    [ForEach] internal static void MoveSimd(Span<Position> positions, ReadOnlySpan<Velocity> velocities) => ComponentMath.MoveSimd(positions, velocities);
    [ForEach] internal static void ReadThree(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health) => ComponentMath.ReadThree(positions, velocities, health);
    [ForEach, With<Team>, Without<Sleeping>] internal static void FlipFiltered(Span<Health> health) => ComponentMath.Flip(health);
}

internal sealed partial class EasyEcsWorld : ComparisonWorld
{
    private readonly EasyEcs.World _world;
    private readonly EasyEcs.Entity[] _entities;
    private readonly EasyEcs.Entity[] _created = new EasyEcs.Entity[Changes];
    private readonly EasyEcs.Entity[] _lookupEntities = new EasyEcs.Entity[Lookups];

    internal EasyEcsWorld(int count, bool nativeParallel = false, bool signalInInitialLayout = false, string? initialWorkload = null)
        : base(count, signalInInitialLayout)
    {
        var options = nativeParallel ? new WorldOptions { InitialEntityCapacity = count + Changes, MaxDegreeOfParallelism = 4 }
            : new WorldOptions { InitialEntityCapacity = count + Changes };
        _world = new EasyEcs.World(options);
        _entities = new EasyEcs.Entity[count];
        if (SignalInInitialLayout)
        {
            _world.EnsureCapacity<Position, Velocity, Health, Team, Sleeping, Signal>(count / 4);
            _world.EnsureCapacity<Position, Velocity, Health, Team, Signal>(count / 4);
            _world.EnsureCapacity<Position, Velocity, Health, Signal>(count / 2 + Changes);
            CreateSignalEntities();
        }
        else if (!CreateInitialFeatureEntities(initialWorkload))
        {
            _world.EnsureCapacity<Position, Velocity, Health>(count / 2 + Changes);
            _world.EnsureCapacity<Position, Velocity, Health, Team>(count / 4);
            _world.EnsureCapacity<Position, Velocity, Health, Team, Sleeping>(count / 4);
            PositionInputs positions = PositionInputs.Get(count);
            _world.CreateEntities(_entities.AsSpan(0, count / 2), new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
            for (int row = count / 2 - 1; row >= 0; row--)
            {
                EasyEcs.Entity entity = _entities[row];
                int ordinal = row * 2 + 1;
                _entities[ordinal] = entity;
                entity.Get<Position>() = positions.Plain[row];
            }
            for (int firstOrdinal = 0; firstOrdinal <= 2; firstOrdinal += 2)
            {
                for (int start = 0; start < count / 4; start += Changes)
                {
                    Span<EasyEcs.Entity> batch = _created.AsSpan(0, Math.Min(Changes, count / 4 - start));
                    if (firstOrdinal == 0) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
                    else _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team());
                    for (int row = 0; row < batch.Length; row++)
                    {
                        int ordinal = (start + row) * 4 + firstOrdinal;
                        EasyEcs.Entity entity = _entities[ordinal] = batch[row];
                        entity.Get<Position>() = (firstOrdinal == 0 ? positions.Sleeping : positions.Team)[start + row];
                    }
                }
            }
        }
        for (int i = 0; i < Lookups; i++) _lookupEntities[i] = _entities[Pick(i)];
    }

    private void CreateSignalEntities()
    {
        PositionInputs positions = PositionInputs.Get(Count);
        for (int shape = 0; shape < 3; shape++)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            int count = Count / stride;
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<EasyEcs.Entity> batch = _created.AsSpan(0, length);
                if (shape == 0) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new Team(), new Sleeping(), new Signal());
                else if (shape == 1) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new Team(), new Signal());
                else _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new Signal());
                input.Slice(start, length).CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref batch[0].Get<Position>(), length));
                for (int row = 0; row < length; row++)
                {
                    int ordinal = (start + row) * stride + first;
                    _entities[ordinal] = batch[row];
                }
            }
        }
    }

    internal override void IterateOne() => EasyEcsQueries.Flip(_world);
    internal override void IterateTwo() => EasyEcsQueries.Move(_world);
    internal override void IterateSimd() => EasyEcsQueries.MoveSimd(_world);
    internal override void IterateThree() => EasyEcsQueries.ReadThree(_world);
    internal override void IterateFiltered() => EasyEcsQueries.FlipFiltered(_world);

    internal override void ReadRandom()
    {
        long total = 0;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)entity.Read<Position>().X;
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
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref entity.Get<Position>(), replacement);
    }

    internal override void AddRemove()
    {
        for (int i = 0; i < Changes; i++) _entities[i].Set(new Extra { Value = 7 });
        for (int i = 0; i < Changes; i++) _entities[i].Remove<Extra>();
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
        _world.CreateEntities(_created, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        DestroyCreated();
    }

    private void DestroyCreated()
    {
        for (int i = 0; i < Changes; i++) _created[i].Destroy();
    }

    internal override Position ReadPosition(int index) => _entities[index].Read<Position>();
    internal override void GrowAndReclaim(Action observe)
    {
        if (CopyScenario is { } copying) { copying.Grow(); observe(); return; }
        if (CommandScenario is { } commands) { _world.EnsureCapacity<Position>(Count); commands.Grow(); observe(); return; }
        if (HierarchyScenario is { } hierarchy) { hierarchy.Grow(); observe(); return; }
        if (RelationScenario is { } relations) { relations.Grow(); observe(); return; }
        if (QueryAppendReady) { if (QueryBindingOnly) QueryAppendCycle(); else observe(); return; }
        if (GrowResources(observe)) return;
        var created = new EasyEcs.Entity[Count];
        _world.CreateEntities(created, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        created[0].Set(new GrowthMarker { Value = 1 });
        if (GrowFeatureValues(created))
        {
            observe();
            return;
        }
        observe();
        foreach (var entity in created) entity.Destroy();
    }

    internal override Health ReadHealth(int index) => _entities[index].Read<Health>();
    internal override int EntityCount => _world.EntityCount;
    public override void Dispose()
    {
        CopyScenario?.Dispose();
        CommandScenario?.Dispose();
        HierarchyScenario?.Dispose();
        RelationScenario?.Dispose();
        QueryJobs?.Dispose();
        _sparseValues?.Dispose();
        ReleaseComparisonResources();
        _world.Dispose();
        JobScheduler?.Dispose();
    }
}
