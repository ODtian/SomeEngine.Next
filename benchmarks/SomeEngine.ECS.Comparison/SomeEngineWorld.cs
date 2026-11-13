using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class SomeEngineWorld : ComparisonWorld
{
    private readonly World _world;
    private readonly int _creationBatchCapacity;
    private readonly Entity[] _entities;
    private readonly Entity[] _created = new Entity[Changes];
    private readonly Entity[] _lookupEntities = new Entity[Lookups];
    private Query _one, _two, _three, _filtered;

    private static class FilteredHealth
    {
        internal static readonly QueryDescription Description = new QueryDescription().With<Health>().With<Team>().Without<Sleeping>();
    }

    internal SomeEngineWorld(int count, int chunkCapacity = 0, bool connectJobs = false, bool signalInInitialLayout = false, int creationBatchCapacity = 0, string? initialWorkload = null)
        : base(count, signalInInitialLayout)
    {
        _creationBatchCapacity = creationBatchCapacity;
        int changeCapacity = count + (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Changes);
        int capacity = initialWorkload switch
        {
            "iterate-one" or "iterate-two" or "iterate-three" or "iterate-simd" or "iterate-filtered" => changeCapacity,
            _ when initialWorkload?.StartsWith("query-") == true => count + 1,
            _ when initialWorkload?.StartsWith("job-") == true && initialWorkload != "job-system-group" => changeCapacity,
            _ => count + Changes,
        };
        _world = new World(capacity, chunkCapacity: chunkCapacity,
            execution: connectJobs ? new SomeEngine.ECS.Systems.WorldJobs(GetJobScheduler()) : null);
        using var structure = _world.OpenStructureWrite();
        _entities = new Entity[count];
        if (SignalInInitialLayout)
        {
            _world.Reserve<Position, Velocity, Health, Team, Sleeping, Signal>(count / 4);
            _world.Reserve<Position, Velocity, Health, Team, Signal>(count / 4);
            _world.Reserve<Position, Velocity, Health, Signal>(count / 2);
            CreateSignalEntities();
        }
        else if (initialWorkload?.EndsWith("-random-read") == true)
        {
            CreateFeatureEntities(static row => new JobOrdinal(row), twoColumns: true);
            _randomJobColumnsReady = true;
        }
        else if (initialWorkload?.EndsWith("-random-write") == true)
        {
            CreateRandomWriteEntities();
            _randomJobColumnsReady = true;
        }
        else if (initialWorkload?.StartsWith("buffer-") == true)
            CreateFeatureEntities(static _ => new Buffer<BufferItem>(), 1, twoColumns: true);
        else if (initialWorkload?.StartsWith("changes-") == true)
            CreateFeatureEntities(static _ => new ScoreValue(), twoColumns: true);
        else if (initialWorkload is "clone-set" or "prefab-set")
            CreateCopyEntities();
        else if (initialWorkload?.StartsWith("index-") == true)
            CreateFeatureEntities(static row => new IndexedNumber(row % 16, row), twoColumns: true);
        else if (initialWorkload?.StartsWith("shared-") == true)
        {
            int groups = initialWorkload.EndsWith("unique") ? count : 16;
            CreateFeatureEntities(row => new SharedNumber(row % groups));
        }
        else
        {
            _world.Reserve<Position, Velocity, Health>(count / 2 + creationBatchCapacity);
            _world.Reserve<Position, Velocity, Health, Team, Sleeping>(count / 4);
            _world.Reserve<Position, Velocity, Health, Team>(count / 4);
            PositionInputs positions = PositionInputs.Get(count);
            _world.Create<Position, Velocity, Health>(_entities.AsSpan(0, count / 2), positions.Plain, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
            for (int row = count / 2 - 1; row >= 0; row--)
            {
                Entity entity = _entities[row];
                int ordinal = row * 2 + 1;
                _entities[ordinal] = entity;
            }
            for (int firstOrdinal = 0; firstOrdinal <= 2; firstOrdinal += 2)
            {
                for (int start = 0; start < count / 4; start += Changes)
                {
                    Span<Entity> batch = _created.AsSpan(0, Math.Min(Changes, count / 4 - start));
                    ReadOnlySpan<Position> values = (firstOrdinal == 0 ? positions.Sleeping : positions.Team).AsSpan(start, batch.Length);
                    if (firstOrdinal == 0) _world.Create<Position, Velocity, Health, Team, Sleeping>(batch, values, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
                    else _world.Create<Position, Velocity, Health, Team>(batch, values, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team());
                    for (int row = 0; row < batch.Length; row++)
                    {
                        int ordinal = (start + row) * 4 + firstOrdinal;
                        _entities[ordinal] = batch[row];
                    }
                }
            }
        }
        for (int i = 0; i < Lookups; i++) _lookupEntities[i] = _entities[Pick(i)];
    }

    private void CreateCopyEntities()
    {
        _world.Reserve<Position, Velocity, Health, SomeCopyNode, IndexedNumber, Buffer<SomeCopyItem>>(Count / 2);
        _world.Reserve<Position, Velocity, Health, SomeCopyNode, IndexedNumber, Buffer<SomeCopyItem>, Team>(Count / 4);
        _world.Reserve<Position, Velocity, Health, SomeCopyNode, IndexedNumber, Buffer<SomeCopyItem>, Team, Sleeping>(Count / 4);
        for (int row = 0; row < Count; row++)
        {
            var position = new Position { X = row };
            var velocity = new Velocity { X = 1, Y = 2 };
            var health = new Health { Value = 100, Max = 100 };
            var node = new SomeCopyNode();
            var number = new IndexedNumber();
            var buffer = new Buffer<SomeCopyItem>();
            _entities[row] = row % 4 == 0 ? _world.Create(position, velocity, health, node, number, buffer, new Team(), new Sleeping())
                : row % 2 == 0 ? _world.Create(position, velocity, health, node, number, buffer, new Team())
                : _world.Create(position, velocity, health, node, number, buffer);
        }
    }

    private void CreateFeatureEntities<T>(Func<int, T> values, int temporaryRows = 0, bool twoColumns = false) where T : struct
    {
        _world.Reserve<Position, Velocity, Health, T>(Count / 2 + temporaryRows);
        _world.Reserve<Position, Velocity, Health, T, Team>(Count / 4 + temporaryRows);
        _world.Reserve<Position, Velocity, Health, T, Team, Sleeping>(Count / 4 + temporaryRows);
        if (twoColumns)
        {
            CreateFeatureBatches(values);
            return;
        }
        for (int row = 0; row < Count; row++)
        {
            var position = new Position { X = row };
            var velocity = new Velocity { X = 1, Y = 2 };
            var health = new Health { Value = 100, Max = 100 };
            T value = values(row);
            _entities[row] = row % 4 == 0 ? _world.Create(position, velocity, health, value, new Team(), new Sleeping())
                : row % 2 == 0 ? _world.Create(position, velocity, health, value, new Team())
                : _world.Create(position, velocity, health, value);
        }
    }

    private void CreateFeatureBatches<T>(Func<int, T> values) where T : struct
    {
        PositionInputs positions = PositionInputs.Get(Count);
        var input = new T[Changes];
        for (int shape = 2; shape >= 0; shape--)
        {
            int first = shape == 2 ? 1 : shape * 2;
            int stride = shape == 2 ? 2 : 4;
            int count = Count / stride;
            ReadOnlySpan<Position> source = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<Entity> batch = _created.AsSpan(0, length);
                Span<T> items = input.AsSpan(0, length);
                for (int row = 0; row < length; row++) items[row] = values((start + row) * stride + first);
                ReadOnlySpan<Position> batchPositions = source.Slice(start, length);
                if (shape == 0) _world.Create<Position, T, Velocity, Health, Team, Sleeping>(batch, batchPositions, items,
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
                else if (shape == 1) _world.Create<Position, T, Velocity, Health, Team>(batch, batchPositions, items,
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team());
                else _world.Create<Position, T, Velocity, Health>(batch, batchPositions, items,
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
                for (int row = 0; row < length; row++) _entities[(start + row) * stride + first] = batch[row];
            }
        }
    }

    private void CreateSignalEntities()
    {
        PositionInputs positions = PositionInputs.Get(Count);
        var signals = new Signal[Changes];
        for (int shape = 0; shape < 3; shape++)
        {
            int first = shape == 2 ? 1 : shape * 2;
            int stride = shape == 2 ? 2 : 4;
            int count = Count / stride;
            ReadOnlySpan<Position> source = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<Entity> batch = _created.AsSpan(0, length);
                ReadOnlySpan<Position> values = source.Slice(start, length);
                if (shape == 0) _world.Create<Position, Signal, Velocity, Health, Team, Sleeping>(batch, values, signals.AsSpan(0, length),
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team(), new Sleeping());
                else if (shape == 1) _world.Create<Position, Signal, Velocity, Health, Team>(batch, values, new Signal(),
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 }, new Team());
                else _world.Create<Position, Signal, Velocity, Health>(batch, values, new Signal(),
                    new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
                for (int row = 0; row < length; row++) _entities[(start + row) * stride + first] = batch[row];
            }
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
                _world.Reserve<Position, Velocity, Health, Extra>(Changes / 2);
                _world.Reserve<Position, Velocity, Health, Extra, Team>(Changes / 4);
                _world.Reserve<Position, Velocity, Health, Extra, Team, Sleeping>(Changes / 4);
                break;
            case "resources": PrepareResources(); break;
            case "enableable": PrepareEnabled(); break;
            case "changes-1":
            case "changes-25": PrepareChanges(workload); break;
            case "buffer-4":
            case "buffer-32": PrepareBuffers(workload); break;
            case "iterate-one": _one = _world.Query<Health>(); break;
            case "iterate-two":
            case "iterate-simd": _two = _world.Query<Position, Velocity>(); break;
            case "iterate-three": _three = _world.Query<Position, Velocity, Health>(); break;
            case "iterate-filtered": _filtered = _world.Query(FilteredHealth.Description); break;
        }
    }

    internal override void IterateOne()
    {
        if (_one.TryGetValues<Health>(out var values)) { ComponentMath.FlipValues(in values); return; }
        IterateOneRanges();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IterateOneRanges()
    {
        new FlipHealth().Execute(_world, query: _one);
    }

    internal override void IterateTwo()
    {
        if (_two.TryGetValues<Position, Velocity>(out var positions, out var velocities)) { ComponentMath.MoveValues(in positions, in velocities); return; }
        IterateTwoRanges();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IterateTwoRanges()
    {
        new MovePositions().Execute(_world, query: _two);
    }

    internal override void IterateSimd()
    {
        if (_two.TryGetValues<Position, Velocity>(out var positions, out var velocities)) { ComponentMath.MoveSimd(positions, velocities); return; }
        IterateSimdRanges();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IterateSimdRanges()
    {
        new MoveVectors().Execute(_world, query: _two);
    }

    internal override void IterateThree()
    {
        if (_three.TryGetValues<Position, Velocity, Health>(out var positions, out var velocities, out var health))
        { ComponentMath.ReadThreeValues(in positions, in velocities, in health); return; }
        IterateThreeRanges();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IterateThreeRanges()
    {
        new ReadThreeColumns().Execute(_world, query: _three);
    }

    internal override void IterateFiltered()
    {
        if (_filtered.TryGetValues<Health>(out var values)) { ComponentMath.Flip(values); return; }
        IterateFilteredRanges();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void IterateFilteredRanges()
    {
        new FlipHealth().Execute(_world, query: _filtered);
    }

    internal override void ReadRandom()
    {
        long total = 0;
        using var positions = _world.OpenRead<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions.ReadKnown(entity).X;
        ReadTotal = total;
    }

    internal override void ReadRandomChecked()
    {
        long total = 0;
        using var positions = _world.OpenRead<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions.Read(entity).X;
        ReadTotal = total;
    }

    internal override void ReplaceRandom()
    {
        Position replacement = ReplacementPosition;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) _world.Set(entity, replacement);
    }

    internal override void ReplaceBatch()
        => _world.Set<Position>(_lookupEntities, ReplacementPosition);

    private partial struct FlipHealth : IJobChunk
    {
        public void Execute(Span<Health> values) => ComponentMath.Flip(values);
    }

    private partial struct MovePositions : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
            => ComponentMath.Move(positions, velocities);
    }

    private partial struct MoveVectors : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities)
            => ComponentMath.MoveSimd(positions, velocities);
    }

    private partial struct ReadThreeColumns : IJobChunk
    {
        public void Execute(Span<Position> positions, ReadOnlySpan<Velocity> velocities, ReadOnlySpan<Health> health)
            => ComponentMath.ReadThree(positions, velocities, health);
    }

    internal override void WriteRandom()
    {
        using var positions = _world.OpenWrite<Position>();
        positions.SetKnown(_lookupEntities, ReplacementPosition);
    }

    internal override void WriteRandomChecked()
    {
        long replacement = ReplacementBits;
        using var positions = _world.OpenWrite<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref positions.Get(entity), replacement);
    }

    internal override void WriteRandomCached()
    {
        using var positions = _world.OpenWrite<Position>();
        positions.SetKnown(_lookupEntities, ReplacementPosition);
    }

    internal override void ReadRandomCached()
    {
        long total = 0;
        using var positions = _world.OpenRead<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions.ReadKnown(entity).X;
        ReadTotal = total;
    }

    internal override void ReadRandomCachedChecked()
    {
        long total = 0;
        using var positions = _world.OpenRead<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions.Read(entity).X;
        ReadTotal = total;
    }
    internal override void WriteRandomCachedChecked()
    {
        long replacement = ReplacementBits;
        using var positions = _world.OpenWrite<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref positions.Get(entity), replacement);
    }

    internal override void ReadRandomKnown()
    {
        long total = 0;
        using var positions = _world.OpenRead<Position>();
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)positions.ReadKnown(entity).X;
        ReadTotal = total;
    }

    internal override void WriteRandomKnown()
    {
        using var positions = _world.OpenWrite<Position>();
        positions.SetKnown(_lookupEntities, ReplacementPosition);
    }

    internal override void AddRemove()
    {
        using var structure = _world.Execution is null ? default : _world.OpenStructureWrite();
        _world.Set<Extra>(_entities.AsSpan(0, Changes), new Extra { Value = 7 });
        _world.Remove<Extra>(_entities.AsSpan(0, Changes));
    }

    internal override void CreateDestroyOne()
    {
        for (int i = 0; i < Changes; i++) _created[i] = _world.Create(new Position { X = i });
        for (int i = 0; i < Changes; i++) _world.Destroy(_created[i]);
    }

    internal override void CreateDestroyThree()
    {
        for (int i = 0; i < Changes; i++)
            _created[i] = _world.Create(new Position { X = i }, new Velocity { X = 1, Y = 2 },
                new Health { Value = 100, Max = 100 });
        for (int i = 0; i < Changes; i++) _world.Destroy(_created[i]);
    }

    internal override void SpawnBatch()
    {
        _world.Create(_created, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        _world.Destroy(_created);
    }

    internal override Position ReadPosition(int index) => _world.Execution is null
        ? _world.Read<Position>(_entities[index]) : _world.ReadValue<Position>(_entities[index]);
    internal override void GrowAndReclaim(Action observe)
    {
        if (CopyScenario is { } copying) { copying.Grow(); observe(); return; }
        if (CommandScenario is { } commands) { _world.Reserve<Position>(Count); commands.Grow(); observe(); return; }
        if (HierarchyScenario is { } hierarchy) { hierarchy.Grow(); observe(); return; }
        if (RelationScenario is { } relations) { relations.Grow(); observe(); return; }
        if (QueryAppendReady) { if (QueryBindingOnly) QueryAppendCycle(); else observe(); return; }
        if (GrowResources(observe)) return;
        var created = new Entity[Count];
        switch (FeatureGrowth)
        {
            case GrowthFeature.Enabled: CreateFeatureGrowth(created, static row => new Signal { Value = row + 1 }); break;
            case GrowthFeature.Buffer: CreateFeatureGrowth(created, static _ => new Buffer<BufferItem>()); break;
            case GrowthFeature.Changes: CreateScoreGrowth(created); break;
            case GrowthFeature.Index: CreateFeatureGrowth(created, row => new IndexedNumber(row == 0 ? 16 : row % 16, Count + row)); break;
            case GrowthFeature.Shared: CreateFeatureGrowth(created, row => new SharedNumber(SharedGroupCount + row % SharedGroupCount)); break;
            default:
                int originalRows = FeatureGrowth == GrowthFeature.Group ? 0 : Count / 2;
                int temporaryRows = _creationBatchCapacity;
                _world.Reserve<Position, Velocity, Health>(originalRows + Count + temporaryRows);
                _world.Create(created, new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
                break;
        }
        _world.Add(created[0], new GrowthMarker { Value = 1 });
        if (GrowFeatureValues(created))
        {
            observe();
            return;
        }
        observe();
        _world.Destroy(created);
    }

    private void CreateScoreGrowth(Entity[] entities)
    {
        _world.Reserve<ScoreValue, Position, Velocity, Health>(Count * 3 / 2);
        var input = new ScoreValue[Changes];
        for (int start = 0; start < entities.Length; start += Changes)
        {
            int length = Math.Min(Changes, entities.Length - start);
            Span<ScoreValue> values = input.AsSpan(0, length);
            for (int row = 0; row < length; row++) values[row] = new ScoreValue { Value = start + row + 1 };
            _world.Create<ScoreValue, Position, Velocity, Health>(entities.AsSpan(start, length), values,
                new Position(), new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        }
    }

    private void CreateFeatureGrowth<T>(Entity[] entities, Func<int, T> values) where T : struct
    {
        _world.Reserve<Position, Velocity, Health, T>(Count * 3 / 2);
        for (int row = 0; row < entities.Length; row++)
            entities[row] = _world.Create(new Position(), new Velocity { X = 1, Y = 2 },
                new Health { Value = 100, Max = 100 }, values(row));
    }

    internal override Health ReadHealth(int index) => _world.Execution is null
        ? _world.Read<Health>(_entities[index]) : _world.ReadValue<Health>(_entities[index]);
    internal override int EntityCount => _world.EntityCount;
    public override void Dispose()
    {
        CopyScenario?.Dispose();
        CommandScenario?.Dispose();
        HierarchyScenario?.Dispose();
        RelationScenario?.Dispose();
        QueryJobs?.Dispose();
        _world.Dispose();
        JobScheduler?.Dispose();
    }
}
