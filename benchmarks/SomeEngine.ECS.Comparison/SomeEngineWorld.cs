using SomeEngine.ECS.Components;
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
            _world.ReserveBundle<SignalSleepingTeamBody>(count / 4);
            _world.ReserveBundle<SignalTeamBody>(count / 4);
            _world.ReserveBundle<SignalBody>(count / 2);
            CreateSignalEntities();
        }
        else if (initialWorkload?.EndsWith("-random-read") == true)
        {
            CreateFeatureEntities(static (row, body) => new OrdinalBody { Body = body, Ordinal = new(row) },
                static (row, body) => new OrdinalTeamBody { Body = body, Ordinal = new(row) },
                static (row, body) => new OrdinalSleepingTeamBody { Body = body, Ordinal = new(row) }, twoColumns: true);
            _randomJobColumnsReady = true;
        }
        else if (initialWorkload?.EndsWith("-random-write") == true)
        {
            CreateRandomWriteEntities();
            _randomJobColumnsReady = true;
        }
        else if (initialWorkload?.StartsWith("buffer-") == true)
            CreateFeatureEntities(static (_, body) => new BufferBody { Body = body },
                static (_, body) => new BufferTeamBody { Body = body },
                static (_, body) => new BufferSleepingTeamBody { Body = body }, 1, twoColumns: true);
        else if (initialWorkload?.StartsWith("changes-") == true)
            CreateFeatureEntities(static (_, body) => new ScoreBody { Body = body },
                static (_, body) => new ScoreTeamBody { Body = body },
                static (_, body) => new ScoreSleepingTeamBody { Body = body }, twoColumns: true);
        else if (initialWorkload is "clone-set" or "prefab-set")
            CreateCopyEntities();
        else if (initialWorkload?.StartsWith("index-") == true)
            CreateFeatureEntities(static (row, body) => new IndexedBody { Body = body, Number = new(row % 16, row) },
                static (row, body) => new IndexedTeamBody { Body = body, Number = new(row % 16, row) },
                static (row, body) => new IndexedSleepingTeamBody { Body = body, Number = new(row % 16, row) }, twoColumns: true);
        else if (initialWorkload?.StartsWith("shared-") == true)
        {
            int groups = initialWorkload.EndsWith("unique") ? count : 16;
            CreateFeatureEntities((row, body) => new SharedBody { Body = body, Number = new(row % groups) },
                (row, body) => new SharedTeamBody { Body = body, Number = new(row % groups) },
                (row, body) => new SharedSleepingTeamBody { Body = body, Number = new(row % groups) });
        }
        else
        {
            _world.ReserveBundle<Body>(count / 2 + creationBatchCapacity);
            _world.ReserveBundle<SleepingTeamBody>(count / 4);
            _world.ReserveBundle<TeamBody>(count / 4);
            PositionInputs positions = PositionInputs.Get(count);
            var bodies = new Body[count / 2];
            for (int row = 0; row < bodies.Length; row++) bodies[row] = Body.Moving(positions.Plain[row]);
            _world.Spawn<Body>(bodies, _entities.AsSpan(0, count / 2));
            var sleepingRows = new SleepingTeamBody[Changes];
            var teamRows = new TeamBody[Changes];
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
                    if (firstOrdinal == 0)
                    {
                        for (int row = 0; row < batch.Length; row++) sleepingRows[row] = new() { Body = Body.Moving(values[row]) };
                        _world.Spawn<SleepingTeamBody>(sleepingRows.AsSpan(0, batch.Length), batch);
                    }
                    else
                    {
                        for (int row = 0; row < batch.Length; row++) teamRows[row] = new() { Body = Body.Moving(values[row]) };
                        _world.Spawn<TeamBody>(teamRows.AsSpan(0, batch.Length), batch);
                    }
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
        _world.ReserveBundle<CopyBody>(Count / 2);
        _world.ReserveBundle<CopyTeamBody>(Count / 4);
        _world.ReserveBundle<CopySleepingTeamBody>(Count / 4);
        for (int row = 0; row < Count; row++)
        {
            Body body = Body.Moving(new Position { X = row });
            _entities[row] = row % 4 == 0 ? _world.Spawn(new CopySleepingTeamBody { Body = body })
                : row % 2 == 0 ? _world.Spawn(new CopyTeamBody { Body = body })
                : _world.Spawn(new CopyBody { Body = body });
        }
    }

    private void CreateFeatureEntities<TPlain, TTeam, TSleeping>(Func<int, Body, TPlain> plain, Func<int, Body, TTeam> team,
        Func<int, Body, TSleeping> sleeping, int temporaryRows = 0, bool twoColumns = false)
        where TPlain : struct, IComponentBundle<TPlain> where TTeam : struct, IComponentBundle<TTeam>
        where TSleeping : struct, IComponentBundle<TSleeping>
    {
        _world.ReserveBundle<TPlain>(Count / 2 + temporaryRows);
        _world.ReserveBundle<TTeam>(Count / 4 + temporaryRows);
        _world.ReserveBundle<TSleeping>(Count / 4 + temporaryRows);
        if (twoColumns)
        {
            CreateFeatureBatches(plain, team, sleeping);
            return;
        }
        for (int row = 0; row < Count; row++)
        {
            Body body = Body.Moving(new Position { X = row });
            _entities[row] = row % 4 == 0 ? _world.Spawn(sleeping(row, body))
                : row % 2 == 0 ? _world.Spawn(team(row, body))
                : _world.Spawn(plain(row, body));
        }
    }

    private void CreateFeatureBatches<TPlain, TTeam, TSleeping>(Func<int, Body, TPlain> plain, Func<int, Body, TTeam> team,
        Func<int, Body, TSleeping> sleeping)
        where TPlain : struct, IComponentBundle<TPlain> where TTeam : struct, IComponentBundle<TTeam>
        where TSleeping : struct, IComponentBundle<TSleeping>
    {
        PositionInputs positions = PositionInputs.Get(Count);
        var plainRows = new TPlain[Changes];
        var teamRows = new TTeam[Changes];
        var sleepingRows = new TSleeping[Changes];
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
                for (int row = 0; row < length; row++)
                {
                    int ordinal = (start + row) * stride + first;
                    Body body = Body.Moving(source[start + row]);
                    if (shape == 0) sleepingRows[row] = sleeping(ordinal, body);
                    else if (shape == 1) teamRows[row] = team(ordinal, body);
                    else plainRows[row] = plain(ordinal, body);
                }
                if (shape == 0) _world.Spawn<TSleeping>(sleepingRows.AsSpan(0, length), batch);
                else if (shape == 1) _world.Spawn<TTeam>(teamRows.AsSpan(0, length), batch);
                else _world.Spawn<TPlain>(plainRows.AsSpan(0, length), batch);
                for (int row = 0; row < length; row++) _entities[(start + row) * stride + first] = batch[row];
            }
        }
    }

    private void CreateSignalEntities()
    {
        PositionInputs positions = PositionInputs.Get(Count);
        var plainRows = new SignalBody[Changes];
        var teamRows = new SignalTeamBody[Changes];
        var sleepingRows = new SignalSleepingTeamBody[Changes];
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
                for (int row = 0; row < length; row++)
                {
                    Body body = Body.Moving(values[row]);
                    if (shape == 0) sleepingRows[row] = new() { Body = body };
                    else if (shape == 1) teamRows[row] = new() { Body = body };
                    else plainRows[row] = new() { Body = body };
                }
                if (shape == 0) _world.Spawn<SignalSleepingTeamBody>(sleepingRows.AsSpan(0, length), batch);
                else if (shape == 1) _world.Spawn<SignalTeamBody>(teamRows.AsSpan(0, length), batch);
                else _world.Spawn<SignalBody>(plainRows.AsSpan(0, length), batch);
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
                _world.ReserveBundle<ExtraBody>(Changes / 2);
                _world.ReserveBundle<ExtraTeamBody>(Changes / 4);
                _world.ReserveBundle<ExtraSleepingTeamBody>(Changes / 4);
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
            _created[i] = _world.Spawn(Body.Moving(new Position { X = i }));
        for (int i = 0; i < Changes; i++) _world.Destroy(_created[i]);
    }

    internal override void SpawnBatch()
    {
        _world.Spawn(Body.Moving(new Position()), _created);
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
            case GrowthFeature.Enabled:
                CreateFeatureGrowth(created, static (row, body) => new SignalBody { Body = body, Signal = new() { Value = row + 1 } }); break;
            case GrowthFeature.Buffer: CreateFeatureGrowth(created, static (_, body) => new BufferBody { Body = body }); break;
            case GrowthFeature.Changes: CreateScoreGrowth(created); break;
            case GrowthFeature.Index:
                CreateFeatureGrowth(created, (row, body) => new IndexedBody { Body = body, Number = new(row == 0 ? 16 : row % 16, Count + row) }); break;
            case GrowthFeature.Shared:
                CreateFeatureGrowth(created, (row, body) => new SharedBody { Body = body, Number = new(SharedGroupCount + row % SharedGroupCount) }); break;
            default:
                int originalRows = FeatureGrowth == GrowthFeature.Group ? 0 : Count / 2;
                int temporaryRows = _creationBatchCapacity;
                _world.ReserveBundle<Body>(originalRows + Count + temporaryRows);
                _world.Spawn(Body.Moving(new Position()), created);
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
        _world.ReserveBundle<ScoreBody>(Count * 3 / 2);
        var input = new ScoreBody[Changes];
        Body body = Body.Moving(new Position());
        for (int start = 0; start < entities.Length; start += Changes)
        {
            int length = Math.Min(Changes, entities.Length - start);
            Span<ScoreBody> values = input.AsSpan(0, length);
            for (int row = 0; row < length; row++) values[row] = new() { Body = body, Score = new ScoreValue { Value = start + row + 1 } };
            _world.Spawn<ScoreBody>(values, entities.AsSpan(start, length));
        }
    }

    private void CreateFeatureGrowth<TBundle>(Entity[] entities, Func<int, Body, TBundle> rows)
        where TBundle : struct, IComponentBundle<TBundle>
    {
        _world.ReserveBundle<TBundle>(Count * 3 / 2);
        Body body = Body.Moving(new Position());
        for (int row = 0; row < entities.Length; row++) entities[row] = _world.Spawn(rows(row, body));
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
