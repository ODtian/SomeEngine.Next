using Arch.Core;
using System.Runtime.InteropServices;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class EasyEcsWorld
{
    private void CreateBufferEntities(int temporaryRows)
    {
        _world.EnsureCapacity<Position, Velocity, Health, EasyEcs.DynamicBuffer<BufferItem>>(Count / 2 + temporaryRows);
        _world.EnsureCapacity<Position, Velocity, Health, EasyEcs.DynamicBuffer<BufferItem>, Team>(Count / 4 + temporaryRows);
        _world.EnsureCapacity<Position, Velocity, Health, EasyEcs.DynamicBuffer<BufferItem>, Team, Sleeping>(Count / 4 + temporaryRows);
        PositionInputs positions = PositionInputs.Get(Count);
        for (int shape = 0; shape < 3; shape++)
        {
            int first = shape == 2 ? 1 : shape * 2;
            int stride = shape == 2 ? 2 : 4;
            int count = Count / stride;
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<EasyEcs.Entity> batch = _created.AsSpan(0, length);
                if (shape == 0) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new EasyEcs.DynamicBuffer<BufferItem>(), new Team(), new Sleeping());
                else if (shape == 1) _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new EasyEcs.DynamicBuffer<BufferItem>(), new Team());
                else _world.CreateEntities(batch, new Position(), new Velocity { X = 1, Y = 2 },
                    new Health { Value = 100, Max = 100 }, new EasyEcs.DynamicBuffer<BufferItem>());
                input.Slice(start, length).CopyTo(MemoryMarshal.CreateSpan(ref batch[0].Get<Position>(), length));
                for (int row = 0; row < length; row++) _entities[(start + row) * stride + first] = batch[row];
            }
        }
    }
}

internal sealed partial class FrifloWorld
{
    private void CreateBufferEntities(int temporaryRows)
    {
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, ReferenceBuffer>(), default).EnsureCapacity(Count / 2 + temporaryRows);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, ReferenceBuffer, Team>(), default).EnsureCapacity(Count / 4 + temporaryRows);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, ReferenceBuffer, Team, Sleeping>(), default).EnsureCapacity(Count / 4 + temporaryRows);
        PositionInputs positions = PositionInputs.Get(Count);
        for (int shape = 0; shape < 3; shape++)
        {
            int first = shape == 2 ? 1 : shape * 2;
            int stride = shape == 2 ? 2 : 4;
            int count = Count / stride;
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            var batch = _world.Batch(autoReturn: false).Add(new Position()).Add(new Velocity { X = 1, Y = 2 })
                .Add(new Health { Value = 100, Max = 100 }).Add(new ReferenceBuffer());
            if (shape < 2) batch.Add(new Team());
            if (shape == 0) batch.Add(new Sleeping());
            ref Position position = ref batch.Get<Position>();
            for (int row = 0; row < count; row++)
            {
                position = input[row];
                _entities[row * stride + first] = batch.CreateEntity();
            }
            batch.Return();
        }
    }
}

internal sealed partial class ArchWorld
{
    private void CreateBufferEntities(int temporaryRows)
    {
        PositionInputs positions = PositionInputs.Get(Count);
        for (int shape = 0; shape < 3; shape++)
        {
            int first = shape == 2 ? 1 : shape * 2;
            int stride = shape == 2 ? 2 : 4;
            int count = Count / stride;
            Signature signature = shape switch
            {
                0 => new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                    Component<ReferenceBuffer>.ComponentType, Component<Team>.ComponentType, Component<Sleeping>.ComponentType),
                1 => new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                    Component<ReferenceBuffer>.ComponentType, Component<Team>.ComponentType),
                _ => new(Component<Position>.ComponentType, Component<Velocity>.ComponentType, Component<Health>.ComponentType,
                    Component<ReferenceBuffer>.ComponentType),
            };
            _world.EnsureCapacity(signature, count + temporaryRows);
            ReadOnlySpan<Position> input = shape == 0 ? positions.Sleeping : shape == 1 ? positions.Team : positions.Plain;
            for (int start = 0; start < count; start += Changes)
            {
                int length = Math.Min(Changes, count - start);
                Span<Arch.Core.Entity> batch = _created.AsSpan(0, length);
                _world.Create(batch, in signature, length);
                for (int row = 0; row < length; row++)
                {
                    _world.Get<Position>(batch[row]) = input[start + row];
                    _entities[(start + row) * stride + first] = batch[row];
                }
            }
        }
        var query = new Arch.Core.QueryDescription().WithAll<Position, Velocity, Health, ReferenceBuffer>();
        foreach (ref var chunk in _world.Query(in query).GetChunkIterator())
        {
            chunk.GetSpan<Velocity>()[..chunk.Count].Fill(new Velocity { X = 1, Y = 2 });
            chunk.GetSpan<Health>()[..chunk.Count].Fill(new Health { Value = 100, Max = 100 });
            chunk.GetSpan<ReferenceBuffer>()[..chunk.Count].Clear();
        }
    }
}
