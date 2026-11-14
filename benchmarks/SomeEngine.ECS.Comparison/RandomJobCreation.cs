using Arch.Core;
using static Friflo.Engine.ECS.EntityStoreExtensions;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class SomeEngineWorld
{
    private bool _randomJobColumnsReady;

    private void CreateRandomWriteEntities()
    {
        _world.ReserveBundle<RandomWriteBody>(Count / 2);
        _world.ReserveBundle<RandomWriteTeamBody>(Count / 4);
        _world.ReserveBundle<RandomWriteSleepingTeamBody>(Count / 4);
        PositionInputs positions = PositionInputs.Get(Count);
        var plainRows = new RandomWriteBody[Changes];
        var teamRows = new RandomWriteTeamBody[Changes];
        for (int shape = 2; shape >= 0; shape--)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            int count = Count / stride;
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<SomeEngine.ECS.Entities.Entity> batch = _created.AsSpan(0, length);
                if (shape == 0)
                {
                    for (int row = 0; row < length; row++)
                        batch[row] = _world.Spawn(new RandomWriteSleepingTeamBody
                        { Body = Body.Moving(input[start + row]), Ordinal = new((start + row) * stride + first) });
                }
                else if (shape == 1)
                {
                    for (int row = 0; row < length; row++)
                        teamRows[row] = new() { Body = Body.Moving(input[start + row]), Ordinal = new((start + row) * stride + first) };
                    _world.Spawn<RandomWriteTeamBody>(teamRows.AsSpan(0, length), batch);
                }
                else
                {
                    for (int row = 0; row < length; row++)
                        plainRows[row] = new() { Body = Body.Moving(input[start + row]), Ordinal = new((start + row) * stride + first) };
                    _world.Spawn<RandomWriteBody>(plainRows.AsSpan(0, length), batch);
                }
                for (int row = 0; row < length; row++) _entities[(start + row) * stride + first] = batch[row];
            }
        }
    }
}

internal sealed partial class EasyEcsWorld
{
    private bool _randomJobColumnsReady;

    private void CreateRandomWriteEntities()
    {
        _world.EnsureCapacity<Position, Velocity, Health, JobOrdinal, JobExtra>(Count / 2);
        _world.EnsureCapacity<Position, Velocity, Health, JobOrdinal, JobExtra, Team>(Count / 4);
        _world.EnsureCapacity<Position, Velocity, Health, JobOrdinal, JobExtra, Team, Sleeping>(Count / 4);
        PositionInputs positions = PositionInputs.Get(Count);
        for (int shape = 2; shape >= 0; shape--)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            int count = Count / stride;
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<EasyEcs.Entity> batch = _created.AsSpan(0, length);
                if (shape == 0) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new JobOrdinal(), new JobExtra(), new Team(), new Sleeping());
                else if (shape == 1) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new JobOrdinal(), new JobExtra(), new Team());
                else _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new JobOrdinal(), new JobExtra());
                input.Slice(start, length).CopyTo(System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref batch[0].Get<Position>(), length));
                Span<JobOrdinal> values = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref batch[0].Get<JobOrdinal>(), length);
                for (int row = 0; row < length; row++)
                {
                    int ordinal = (start + row) * stride + first;
                    values[row] = new(ordinal);
                    _entities[ordinal] = batch[row];
                }
            }
        }
    }
}

internal sealed partial class FrifloWorld
{
    private bool _randomJobColumnsReady;

    private void CreateRandomWriteEntities()
    {
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, JobOrdinal, JobExtra>(), default).EnsureCapacity(Count / 2);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, JobOrdinal, JobExtra, Team>(), default).EnsureCapacity(Count / 4);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, JobOrdinal, JobExtra, Team, Sleeping>(), default).EnsureCapacity(Count / 4);
        for (int shape = 2; shape >= 0; shape--)
        {
            int stride = shape == 2 ? 2 : 4, first = shape == 2 ? 1 : shape * 2;
            var batch = _world.Batch(autoReturn: false).Add(new Position()).Add(new Velocity { X = 1, Y = 2 })
                .Add(new Health { Value = 100, Max = 100 }).Add(new JobOrdinal()).Add(new JobExtra());
            if (shape < 2) batch.Add(new Team());
            if (shape == 0) batch.Add(new Sleeping());
            ref Position position = ref batch.Get<Position>();
            ref JobOrdinal ordinal = ref batch.Get<JobOrdinal>();
            for (int row = 0; row < Count / stride; row++)
            {
                int index = row * stride + first;
                position.X = index; ordinal = new(index);
                _entities[index] = batch.CreateEntity();
            }
            batch.Return();
        }
    }
}

internal sealed partial class ArchWorld
{
    private bool _randomJobColumnsReady;

    private void CreateRandomWriteEntities()
    {
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType), Count / 2);
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType, Component<Team>.ComponentType), Count / 4);
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType, Component<Team>.ComponentType,
            Component<Sleeping>.ComponentType), Count / 4);
        Signature[] signatures =
        [
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType, Component<Team>.ComponentType, Component<Sleeping>.ComponentType),
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType, Component<Team>.ComponentType),
            new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                Component<JobOrdinal>.ComponentType, Component<JobExtra>.ComponentType)
        ];
        for (int shape = 2; shape >= 0; shape--)
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
                    _world.Get<Position>(batch[row]) = new Position { X = ordinal };
                    _world.Get<JobOrdinal>(batch[row]) = new(ordinal);
                    _entities[ordinal] = batch[row];
                }
            }
        }
        foreach (ref var chunk in _world.Query(in _three).GetChunkIterator())
        {
            chunk.GetSpan<Velocity>()[..chunk.Count].Fill(new Velocity { X = 1, Y = 2 });
            chunk.GetSpan<Health>()[..chunk.Count].Fill(new Health { Value = 100, Max = 100 });
            chunk.GetSpan<JobExtra>()[..chunk.Count].Clear();
        }
    }
}
