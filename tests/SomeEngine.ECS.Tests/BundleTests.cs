using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct PhysicsBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public Velocity Velocity;
}

public struct CombatBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public PhysicsBundle Physics;
    public Health Health;
    public PlayerTag Player;
}

public struct StatusBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public VisibilityState Visibility;
    public CleanupMarker Cleanup;
    public NameIndex Name;
    public Damage Damage;
}

public struct MotionHealthBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public Velocity Velocity;
    public Health Health;
}

public struct FullComponentBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public Velocity Velocity;
    public Health Health;
    public PureUnmanaged PureUnmanaged;
}

public struct CleanupPositionBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public CleanupMarker Cleanup;
    public Position Position;
}

public struct SortBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public SortLow Low;
    public SortHigh High;
}

public struct PositionVisibilityBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public VisibilityState Visibility;
}

public struct BufferBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public ReadOnlyMemory<IntElement> Values;
}

public struct VisibilityMoveSpeedBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public VisibilityState Visibility;
    public MoveSpeed MoveSpeed;
}

public struct SharedSceneBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public Position Position;
    public SceneId Scene;
}

public struct SharedOnlyBundle : SomeEngine.ECS.Components.IComponentBundle
{
    public SceneId Scene;
}

public class BundleTests
{
    [Fact]
    public void Spawn_SimpleBundle_CreatesEntityWithAllTableComponents()
    {
        var world = new World();

        var entity = world.Spawn(new PhysicsBundle
        {
            Position = new Position { X = 10, Y = 20 },
            Velocity = new Velocity { X = 3, Y = 4 },
        });

        Assert.True(world.IsAlive(entity));
        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.Equal(10f, world.Read<Position>(entity).X);
        Assert.Equal(4f, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void AddBundle_AllNew_AddsAllMissingComponentsInOneCall()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 5, Y = 6 },
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.Equal(1f, world.Read<Position>(entity).X);
        Assert.Equal(6f, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void AddBundle_ComponentAlreadyExists_Throws()
    {
        var world = new World();
        var entity = world.Create(new Position { X = 7, Y = 8 });

        Assert.Throws<InvalidOperationException>(() => world.AddBundle(entity, new PhysicsBundle
            {
                Position = new Position { X = 1, Y = 2 },
                Velocity = new Velocity { X = 3, Y = 4 },
            }));

        var sparseEntity = world.Create();
        world.SetSparse(sparseEntity, new Damage { Amount = 7 });

        Assert.Throws<InvalidOperationException>(() => world.AddBundle(sparseEntity, new StatusBundle
            {
                Visibility = new VisibilityState { Value = 1 },
                Cleanup = new CleanupMarker { Value = 2 },
                Name = new NameIndex { Value = "unit" },
                Damage = new Damage { Amount = 3 },
            }));
        Assert.Equal(7, world.Read<Position>(entity).X);
        Assert.False(world.Has<Velocity>(entity));
        Assert.Equal(7, world.ReadSparse<Damage>(sparseEntity).Amount);
        Assert.False(world.Has<CleanupMarker>(sparseEntity));
    }

    [Fact]
    public void ReplaceBundleRequiresEveryFieldAndUpdatesExistingValues()
    {
        var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });

        Assert.Throws<InvalidOperationException>(() =>
            world.ReplaceBundle(entity, new PhysicsBundle
            {
                Position = new Position { X = 11, Y = 12 },
                Velocity = new Velocity { X = 13, Y = 14 },
            }));

        world.Add(entity, new Velocity { X = 3, Y = 4 });

        world.ReplaceBundle(entity, new PhysicsBundle
        {
            Position = new Position { X = 11, Y = 12 },
            Velocity = new Velocity { X = 13, Y = 14 },
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.Equal(11f, world.Read<Position>(entity).X);
        Assert.Equal(14f, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void Spawn_NestedBundle_FlattensFieldsAndTagsForMigration()
    {
        var world = new World();

        var entity = world.Spawn(new CombatBundle
        {
            Physics = new PhysicsBundle
            {
                Position = new Position { X = 2, Y = 4 },
                Velocity = new Velocity { X = 6, Y = 8 },
            },
            Health = new Health { Value = 99 },
            Player = new PlayerTag(),
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Velocity>(entity));
        Assert.True(world.Has<Health>(entity));
        Assert.True(world.Has<PlayerTag>(entity));
        Assert.Equal(99, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void Spawn_BundleWithEnableableSparseCleanupAndIndexed_SupportsAllKinds()
    {
        var world = new World();

        var entity = world.Spawn(new StatusBundle
        {
            Visibility = new VisibilityState { Value = 5 },
            Cleanup = new CleanupMarker { Value = 8 },
            Name = new NameIndex { Value = "boss" },
            Damage = new Damage { Amount = 42 },
        });

        Assert.True(world.Has<VisibilityState>(entity));
        Assert.True(world.IsEnabled<VisibilityState>(entity));
        Assert.True(world.Has<CleanupMarker>(entity));
        Assert.True(world.HasSparse<Damage>(entity));
        Assert.Equal(42, world.ReadSparse<Damage>(entity).Amount);

        var matches = ReadIndexedEntities<NameIndex, string>(world, "boss");
        Assert.Contains(entity, matches.ToArray());
    }

    [Fact]
    public void Spawn_BundleWithBuffer_AddsBackingComponentsAndInitialValues()
    {
        var world = new World();

        var entity = world.Spawn(new BufferBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Values = new IntElement[]
            {
                new IntElement { Value = 10 },
                new IntElement { Value = 20 },
            },
        });

        Assert.True(world.Has<Position>(entity));
        Assert.True(world.Has<Buffer<IntElement>>(entity));
        {
            using ReadOnlyBuffer<IntElement> buffer = world.ReadBuffer<IntElement>(entity);

            Assert.Equal(2, buffer.Count);
            Assert.Equal(10, buffer[0].Value);
            Assert.Equal(20, buffer[1].Value);

        };
    }

    [Fact]
    public void Spawn_BundleWithSharedComponent_RoutesInitialChunkAndQueryFilter()
    {
        var world = new World();

        var e1 = world.Spawn(new SharedSceneBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Scene = new SceneId { Value = 7 },
        });
        var e2 = world.Spawn(new SharedSceneBundle
        {
            Position = new Position { X = 3, Y = 4 },
            Scene = new SceneId { Value = 7 },
        });

        Assert.True(world.Has<SceneId>(e1));
        Assert.True(world.Has<SceneId>(e2));
        Assert.Equal(7, world.ReadShared<SceneId>(e1).Value);

        var query = world.Query(
            new QueryDescription().WithStored<Position>()
                .WithStored<SceneId>());

        int count = 0;
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk chunk in cursor)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Shared<SceneId>(row).Value == 7) count++;

        };

        Assert.Equal(2, count);
    }

    [Fact]
    public void SetBundle_SharedOnly_MigratesSharedChunkWithoutTableWrites()
    {
        var world = new World();
        var entity = world.Create(new Position { X = 1, Y = 2 });
        world.SetShared(entity, new SceneId { Value = 1 });

        world.ReplaceBundle(entity, new SharedOnlyBundle
        {
            Scene = new SceneId { Value = 2 },
        });

        var sceneQuery = world.Query(
            new QueryDescription().WithStored<Position>()
                .WithStored<SceneId>());

        int oldCount = 0;
        int newCount = 0;
        {
            using QueryScope cursor = (sceneQuery).OpenRead();
            foreach (QueryChunk chunk in cursor)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.Shared<SceneId>(row).Value == 1) oldCount++;
                    else if (chunk.Shared<SceneId>(row).Value == 2)
                    {
                        newCount++;
                        Assert.Equal(1, chunk.Read<Position>()[row].X);
                    }

        };

        Assert.Equal(0, oldCount);
        Assert.Equal(1, newCount);
        Assert.Equal(2, world.ReadShared<SceneId>(entity).Value);
    }

    [Fact]
    public void SetBundle_AllExisting_PreservesEnableStateAndUpdatesIndexedValue()
    {
        var world = new World();
        var entity = world.Spawn(new StatusBundle
        {
            Visibility = new VisibilityState { Value = 1 },
            Cleanup = new CleanupMarker { Value = 2 },
            Name = new NameIndex { Value = "before" },
            Damage = new Damage { Amount = 3 },
        });

        world.Disable<VisibilityState>(entity);

        world.ReplaceBundle(entity, new StatusBundle
        {
            Visibility = new VisibilityState { Value = 10 },
            Cleanup = new CleanupMarker { Value = 20 },
            Name = new NameIndex { Value = "after" },
            Damage = new Damage { Amount = 30 },
        });

        Assert.False(world.IsEnabled<VisibilityState>(entity));
        Assert.Equal(10, world.Read<VisibilityState>(entity).Value);
        Assert.Equal(20, world.Read<CleanupMarker>(entity).Value);
        Assert.Equal(30, world.ReadSparse<Damage>(entity).Amount);
        Assert.DoesNotContain(entity, ReadIndexedEntities<NameIndex, string>(world, "before").ToArray());
        Assert.Contains(entity, ReadIndexedEntities<NameIndex, string>(world, "after").ToArray());
    }

    [Fact]
    public void Spawn_WarmedRuntime_PublishesIndependentEntity()
    {
        var world = new World();
        var bundle = new PhysicsBundle
        {
            Position = new Position { X = 1, Y = 2 },
            Velocity = new Velocity { X = 3, Y = 4 },
        };

        Entity first = world.Spawn(bundle);
        Entity second = world.Spawn(bundle);

        Assert.NotEqual(first, second);
        Assert.Equal(2, world.EntityCount);
        Assert.Equal(4, world.Read<Velocity>(second).Y);
    }

    [Fact]
    public void AddBundle_WarmedMigrationPath_PublishesExpectedValues()
    {
        var world = new World();
        var bundle = new PhysicsBundle
        {
            Position = new Position { X = 7, Y = 8 },
            Velocity = new Velocity { X = 9, Y = 10 },
        };

        var warmEntity = world.Create();
        world.AddBundle(warmEntity, bundle); // warm destination archetype

        var entity = world.Create();

        world.AddBundle(entity, bundle);

        Assert.Equal(7, world.Read<Position>(entity).X);
        Assert.Equal(10, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void CountedBundleStructWriterWritesEveryRequestedEntity()
    {
        using var world = new World();
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value];
        var entities = new Entity[3];
        world.ExecuteBundleSpawnBatch(ids, entities.Length, new BatchOutput(entities), new PairBatchWriter());
        Assert.Equal(entities.Length, world.EntityCount);
        for (int index = 0; index < entities.Length; index++)
        {
            Assert.True(world.IsAlive(entities[index]));
            Assert.Equal(index + 1, world.Read<Position>(entities[index]).X);
            Assert.Equal(index + 30, world.Read<Velocity>(entities[index]).Y);
        }
    }

    [Fact]
    public void CountedSingleComponentStructWriterWritesEveryEntity()
    {
        using var world = new World();
        var entities = new Entity[4];
        world.ExecuteBundleSpawnBatch([ComponentId<Position>.Value], entities.Length, new BatchOutput(entities), new SingleBatchWriter());
        for (int index = 0; index < entities.Length; index++)
        {
            Assert.True(world.IsAlive(entities[index]));
            Assert.Equal(index + 1, world.Read<Position>(entities[index]).X);
            Assert.Equal(index + 2, world.Read<Position>(entities[index]).Y);
        }
    }

    [Fact]
    public void ReservedBundleCanSpawnAdditionalRowsWithExistingEntities()
    {
        using var world = new World();
        world.Spawn(new PhysicsBundle { Position = new Position { X = -1 } });
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value];
        world.ReserveBundle(ids, 2_049);
        world.ExecuteBundleSpawnBatch(ids, 2_049, 0, new ReservedPairWriter());
        Assert.Equal(2_050, world.EntityCount);
        var query = world.Query(new QueryDescription().WithStored<Position>().WithStored<Velocity>());
        var values = new List<float>();
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                values.Add(rowChunk.Read<Position>()[row].X);

        };
        Assert.Equal(Enumerable.Range(-1, 2_050).Select(value => (float)value),
            values.OrderBy(value => value));
    }

    private readonly record struct BatchOutput(Entity[] Entities);
    private readonly struct PairBatchWriter : IBundleWriter<BatchOutput>
    {
        public void Write(BundleWriteView view, in BatchOutput output)
        {
            view.Write(new Position { X = view.Index + 1, Y = view.Index + 10 });
            view.Write(new Velocity { X = view.Index + 20, Y = view.Index + 30 });
            output.Entities[view.Index] = view.Entity;
        }
    }
    private readonly struct SingleBatchWriter : IBundleWriter<BatchOutput>
    {
        public void Write(BundleWriteView view, in BatchOutput output)
        {
            view.Write(new Position { X = view.Index + 1, Y = view.Index + 2 });
            output.Entities[view.Index] = view.Entity;
        }
    }
    private readonly struct ReservedPairWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.Write(new Position { X = view.Index });
            view.Write(new Velocity { X = view.Index + 1 });
        }
    }
}
