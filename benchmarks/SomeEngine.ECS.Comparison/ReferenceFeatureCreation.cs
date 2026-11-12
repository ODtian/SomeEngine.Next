using Arch.Core;
using static Friflo.Engine.ECS.EntityStoreExtensions;

namespace SomeEngine.ECS.Comparison;

internal sealed partial class EasyEcsWorld
{
    private bool CreateInitialFeatureEntities(string? workload)
    {
        if (workload?.EndsWith("-random-read") == true)
        {
            CreateFeatureEntities(static row => new JobOrdinal(row));
            _randomJobColumnsReady = true;
        }
        else if (workload?.EndsWith("-random-write") == true)
        {
            CreateRandomWriteEntities();
            _randomJobColumnsReady = true;
        }
        else if (workload?.StartsWith("buffer-") == true)
            CreateBufferEntities(1);
        else if (workload?.StartsWith("changes-") == true)
        {
            CreateFeatureEntities(static row => new EasyScore { Added = (ulong)(row + 1) });
            _scoreVersion = (ulong)Count;
        }
        else if (workload is "clone-set" or "prefab-set")
            CreateFeatureEntities(static _ => new EasyCopyNode());
        else if (workload?.StartsWith("index-") == true)
            CreateFeatureEntities(static row => new IndexedNumber(row % 16, row));
        else if (workload?.StartsWith("shared-") == true)
        {
            int groups = workload.EndsWith("unique") ? Count : 16;
            CreateFeatureEntities(row => new SharedNumberId(row % groups));
        }
        else return false;
        return true;
    }

    private void CreateFeatureEntities<T>(Func<int, T> values, int temporaryRows = 0) where T : struct, EasyEcs.IComponent
    {
        _world.EnsureCapacity<Position, Velocity, Health, T>(Count / 2 + temporaryRows);
        _world.EnsureCapacity<Position, Velocity, Health, T, Team>(Count / 4 + temporaryRows);
        _world.EnsureCapacity<Position, Velocity, Health, T, Team, Sleeping>(Count / 4 + temporaryRows);
        for (int row = 0; row < Count; row++)
        {
            var position = new Position { X = row }; var velocity = new Velocity { X = 1, Y = 2 };
            var health = new Health { Value = 100, Max = 100 }; T value = values(row);
            _entities[row] = row % 4 == 0 ? _world.CreateEntity(position, velocity, health, value, new Team(), new Sleeping())
                : row % 2 == 0 ? _world.CreateEntity(position, velocity, health, value, new Team())
                : _world.CreateEntity(position, velocity, health, value);
        }
    }
}

internal sealed partial class FrifloWorld
{
    private bool CreateInitialFeatureEntities(string? workload)
    {
        if (workload?.EndsWith("-random-read") == true)
        {
            CreateFeatureEntities(static row => new JobOrdinal(row));
            _randomJobColumnsReady = true;
        }
        else if (workload?.EndsWith("-random-write") == true)
        {
            CreateRandomWriteEntities();
            _randomJobColumnsReady = true;
        }
        else if (workload?.StartsWith("buffer-") == true)
            CreateBufferEntities(1);
        else if (workload?.StartsWith("changes-") == true)
        {
            CreateFeatureEntities(static row => new ScoreHistory { Added = (ulong)(row + 1), Written = (ulong)(row + 1) });
            _scoreVersion = (ulong)Count;
        }
        else if (workload is "clone-set" or "prefab-set")
            CreateFeatureEntities(static _ => new CopyGraphNode<Friflo.Engine.ECS.RawEntity>());
        else if (workload?.StartsWith("index-") == true)
            CreateFeatureEntities(static row => ReferenceIndexedNumber.Project(new IndexedNumber(row % 16, row)));
        else if (workload?.StartsWith("shared-") == true)
        {
            int groups = workload.EndsWith("unique") ? Count : 16;
            CreateFeatureEntities(row => new SharedNumberId(row % groups));
        }
        else return false;
        return true;
    }

    private void CreateFeatureEntities<T>(Func<int, T> values, int temporaryRows = 0) where T : struct, Friflo.Engine.ECS.IComponent
    {
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, T>(), default).EnsureCapacity(Count / 2 + temporaryRows);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, T, Team>(), default).EnsureCapacity(Count / 4 + temporaryRows);
        _world.GetArchetype(Friflo.Engine.ECS.ComponentTypes.Get<Position, Velocity, Health, T, Team, Sleeping>(), default).EnsureCapacity(Count / 4 + temporaryRows);
        for (int row = 0; row < Count; row++)
        {
            var position = new Position { X = row }; var velocity = new Velocity { X = 1, Y = 2 };
            var health = new Health { Value = 100, Max = 100 }; T value = values(row);
            _entities[row] = row % 4 == 0 ? _world.CreateEntity(position, velocity, health, value, new Team(), new Sleeping())
                : row % 2 == 0 ? _world.CreateEntity(position, velocity, health, value, new Team())
                : _world.CreateEntity(position, velocity, health, value);
        }
    }
}

internal sealed partial class ArchWorld
{
    private bool CreateInitialFeatureEntities(string? workload)
    {
        if (workload?.EndsWith("-random-read") == true)
        {
            CreateFeatureEntities(static row => new JobOrdinal(row));
            _randomJobColumnsReady = true;
        }
        else if (workload?.EndsWith("-random-write") == true)
        {
            CreateRandomWriteEntities();
            _randomJobColumnsReady = true;
        }
        else if (workload?.StartsWith("buffer-") == true)
            CreateBufferEntities(1);
        else if (workload?.StartsWith("changes-") == true)
        {
            CreateFeatureEntities(static row => new ScoreHistory { Added = (ulong)(row + 1), Written = (ulong)(row + 1) });
            _scoreVersion = (ulong)Count;
        }
        else if (workload is "clone-set" or "prefab-set")
            CreateFeatureEntities(static _ => new CopyGraphNode<Arch.Core.Entity>());
        else if (workload?.StartsWith("index-") == true)
            CreateFeatureEntities(static row => ReferenceIndexedNumber.Project(new IndexedNumber(row % 16, row)));
        else if (workload?.StartsWith("shared-") == true)
        {
            int groups = workload.EndsWith("unique") ? Count : 16;
            CreateFeatureEntities(row => new SharedNumberId(row % groups));
        }
        else return false;
        return true;
    }

    private void CreateFeatureEntities<T>(Func<int, T> values, int temporaryRows = 0) where T : struct
    {
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<T>.ComponentType), Count / 2 + temporaryRows);
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<T>.ComponentType, Component<Team>.ComponentType), Count / 4 + temporaryRows);
        _world.EnsureCapacity(new Signature(Component<Position>.ComponentType, Component<Velocity>.ComponentType,
            Component<Health>.ComponentType, Component<T>.ComponentType, Component<Team>.ComponentType, Component<Sleeping>.ComponentType), Count / 4 + temporaryRows);
        for (int row = 0; row < Count; row++)
        {
            var position = new Position { X = row }; var velocity = new Velocity { X = 1, Y = 2 };
            var health = new Health { Value = 100, Max = 100 }; T value = values(row);
            _entities[row] = row % 4 == 0 ? _world.Create(position, velocity, health, value, new Team(), new Sleeping())
                : row % 2 == 0 ? _world.Create(position, velocity, health, value, new Team())
                : _world.Create(position, velocity, health, value);
        }
    }
}
