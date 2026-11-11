using Arch.Core;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class ArchWorld : ComparisonWorld
{
    private readonly Arch.Core.World _world = Arch.Core.World.Create();
    private readonly Arch.Core.Entity[] _entities;
    private readonly Arch.Core.Entity[] _created = new Arch.Core.Entity[Changes];
    private readonly Arch.Core.Entity[] _lookupEntities = new Arch.Core.Entity[Lookups];
    private readonly Arch.Core.QueryDescription _one = new Arch.Core.QueryDescription().WithAll<Health>();
    private readonly Arch.Core.QueryDescription _two = new Arch.Core.QueryDescription().WithAll<Position, Velocity>();
    private readonly Arch.Core.QueryDescription _three = new Arch.Core.QueryDescription().WithAll<Position, Velocity, Health>();
    private readonly Arch.Core.QueryDescription _filtered = new Arch.Core.QueryDescription().WithAll<Health, Team>().WithNone<Sleeping>();
    private static readonly Signature ThreeComponents = new(
        Component<Position>.ComponentType,
        Component<Velocity>.ComponentType,
        Component<Health>.ComponentType);

    internal ArchWorld(int count, bool signalInInitialLayout = false, string? initialWorkload = null) : base(count, signalInInitialLayout)
    {
        _entities = new Arch.Core.Entity[count];
        if (SignalInInitialLayout)
        {
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
                Component<Health>.ComponentType, Component<Team>.ComponentType, Component<Sleeping>.ComponentType, Component<Signal>.ComponentType), count / 4);
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
                Component<Health>.ComponentType, Component<Team>.ComponentType, Component<Signal>.ComponentType), count / 4);
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
                Component<Health>.ComponentType, Component<Signal>.ComponentType), count / 2 + Changes);
            CreateSignalEntities();
        }
        else if (!CreateInitialFeatureEntities(initialWorkload))
        {
            _world.EnsureCapacity(ThreeComponents, count / 2 + Changes);
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
                Component<Health>.ComponentType, Component<Team>.ComponentType), count / 4);
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
                Component<Health>.ComponentType, Component<Team>.ComponentType, Component<Sleeping>.ComponentType), count / 4);
            PositionInputs input = PositionInputs.Get(count);
            _world.Create(_entities.AsSpan(0, count / 2), in ThreeComponents, count / 2);
            foreach (ref var chunk in _world.Query(in _three).GetChunkIterator())
            {
                chunk.GetSpan<Velocity>()[..chunk.Count].Fill(new Velocity { X = 1, Y = 2 });
                chunk.GetSpan<Health>()[..chunk.Count].Fill(new Health { Value = 100, Max = 100 });
            }
            for (int row = count / 2 - 1; row >= 0; row--)
            {
                var entity = _entities[row];
                _entities[row * 2 + 1] = entity;
                _world.Get<Position>(entity) = input.Plain[row];
            }
            for (int i = 0; i < count; i += 2)
            {
                Position position = (i % 4 == 0 ? input.Sleeping : input.Team)[i / 4];
                var velocity = new Velocity { X = 1, Y = 2 };
                var health = new Health { Value = 100, Max = 100 };
                _entities[i] = i % 4 == 0 ? _world.Create(position, velocity, health, new Team(), new Sleeping())
                    : _world.Create(position, velocity, health, new Team());
            }
        }
        for (int i = 0; i < Lookups; i++) _lookupEntities[i] = _entities[Pick(i)];
    }

    private void CreateSignalEntities()
    {
        Signature[] signatures =
        [
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<Team>.ComponentType, Component<Sleeping>.ComponentType, Component<Signal>.ComponentType),
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<Team>.ComponentType, Component<Signal>.ComponentType),
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<Signal>.ComponentType)
        ];
        for (int shape = 0; shape < signatures.Length; shape++)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            int count = Count / stride;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<Arch.Core.Entity> batch = _created.AsSpan(0, length);
                _world.Create(batch, in signatures[shape], length);
                for (int row = 0; row < length; row++)
                {
                    int ordinal = (start + row) * stride + first;
                    _entities[ordinal] = batch[row];
                    _world.Get<Position>(batch[row]).X = ordinal;
                }
            }
        }
        foreach (ref var chunk in _world.Query(in _three).GetChunkIterator())
        {
            chunk.GetSpan<Velocity>()[..chunk.Count].Fill(new Velocity { X = 1, Y = 2 });
            chunk.GetSpan<Health>()[..chunk.Count].Fill(new Health { Value = 100, Max = 100 });
            chunk.GetSpan<Signal>()[..chunk.Count].Clear();
        }
    }

    internal override void IterateOne()
    {
        foreach (ref var chunk in _world.Query(in _one).GetChunkIterator()) ComponentMath.Flip(chunk.GetSpan<Health>()[..chunk.Count]);
    }
    internal override void IterateTwo()
    {
        foreach (ref var chunk in _world.Query(in _two).GetChunkIterator()) ComponentMath.Move(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count]);
    }
    internal override void IterateSimd()
    {
        foreach (ref var chunk in _world.Query(in _two).GetChunkIterator()) ComponentMath.MoveSimd(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count]);
    }
    internal override void IterateThree()
    {
        foreach (ref var chunk in _world.Query(in _three).GetChunkIterator()) ComponentMath.ReadThree(chunk.GetSpan<Position>()[..chunk.Count], chunk.GetSpan<Velocity>()[..chunk.Count], chunk.GetSpan<Health>()[..chunk.Count]);
    }
    internal override void IterateFiltered()
    {
        foreach (ref var chunk in _world.Query(in _filtered).GetChunkIterator()) ComponentMath.Flip(chunk.GetSpan<Health>()[..chunk.Count]);
    }
    internal override void ReadRandom()
    {
        long total = 0;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) total += (int)_world.Get<Position>(entity).X;
        ReadTotal = total;
    }
    internal override void ReplaceRandom()
    {
        Position replacement = ReplacementPosition;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) _world.Set(entity, replacement);
    }

    internal override void WriteRandom()
    {
        long replacement = ReplacementBits;
        foreach (ref readonly var entity in _lookupEntities.AsSpan()) ComponentMath.WritePosition(ref _world.Get<Position>(entity), replacement);
    }

    internal override void ReadRandomChecked()
    {
        long total = 0;
        foreach (ref readonly var entity in _lookupEntities.AsSpan())
        {
            if (!_world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
            ref Position value = ref _world.TryGetRef<Position>(entity, out bool exists);
            if (!exists) throw new ArgumentException("Component is absent.");
            total += (int)value.X;
        }
        ReadTotal = total;
    }

    internal override void WriteRandomChecked()
    {
        long replacement = ReplacementBits;
        foreach (ref readonly var entity in _lookupEntities.AsSpan())
        {
            if (!_world.IsAlive(entity)) throw new ArgumentException("Entity generation has expired.");
            ref Position value = ref _world.TryGetRef<Position>(entity, out bool exists);
            if (!exists) throw new ArgumentException("Component is absent.");
            ComponentMath.WritePosition(ref value, replacement);
        }
    }
    internal override void AddRemove()
    {
        if (_groupedValues is not null) { _groupedValues.AddRemove(Changes); return; }
        for (int i = 0; i < Changes; i++) _world.Add(_entities[i], new Extra { Value = 7 });
        for (int i = 0; i < Changes; i++) _world.Remove<Extra>(_entities[i]);
    }
    internal override void CreateDestroyOne()
    {
        for (int i = 0; i < Changes; i++) _created[i] = _world.Create(new Position { X = i });
        DestroyCreated();
    }
    internal override void CreateDestroyThree()
    {
        for (int i = 0; i < Changes; i++) _created[i] = _world.Create(new Position { X = i }, new Velocity { X = 1, Y = 2 }, new Health { Value = 100, Max = 100 });
        DestroyCreated();
    }
    internal override void SpawnBatch()
    {
        _world.Create(_created.AsSpan(), in ThreeComponents, Changes);
        for (int i = 0; i < Changes; i++)
        {
            _world.Set(_created[i], new Velocity { X = 1, Y = 2 });
            _world.Set(_created[i], new Health { Value = 100, Max = 100 });
        }
        DestroyCreated();
    }
    private void DestroyCreated()
    {
        for (int i = 0; i < Changes; i++) _world.Destroy(_created[i]);
    }
    internal override Position ReadPosition(int index) => _world.Get<Position>(_entities[index]);
    internal override void GrowAndReclaim(Action observe)
    {
        if (CopyScenario is { } copying) { copying.Grow(); observe(); return; }
        if (CommandScenario is { } commands)
        {
            _world.EnsureCapacity(new Signature(Component<Position>.ComponentType), Count);
            commands.Grow(); observe(); return;
        }
        if (HierarchyScenario is { } hierarchy) { hierarchy.Grow(); observe(); return; }
        if (RelationScenario is { } relations) { relations.Grow(); observe(); return; }
        if (QueryAppendReady) { if (QueryBindingOnly) QueryAppendCycle(); else observe(); return; }
        if (GrowResources(observe)) return;
        var created = new Arch.Core.Entity[Count];
        _world.Create(created.AsSpan(), in ThreeComponents, Count);
        foreach (var entity in created)
        {
            _world.Set(entity, new Velocity { X = 1, Y = 2 });
            _world.Set(entity, new Health { Value = 100, Max = 100 });
        }
        _world.Add(created[0], new GrowthMarker { Value = 1 });
        if (GrowFeatureValues(created)) { observe(); return; }
        observe();
        foreach (var entity in created) _world.Destroy(entity);
    }

    internal override Health ReadHealth(int index) => _world.Get<Health>(_entities[index]);
    internal override int EntityCount => _groupedValues?.EntityCount ?? _world.Size;
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
        Arch.Core.World.Destroy(_world);
        JobScheduler?.Dispose();
    }
}
