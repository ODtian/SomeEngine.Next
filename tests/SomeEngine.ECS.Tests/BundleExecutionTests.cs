using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Registry;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class BundleExecutionTests
{
    [Fact]
    public void ExecuteBundleSpawn_CallbackFaultDiscardsCandidateAndReleasesRuntime()
    {
        var world = new World();
        int archetypeCount = world.ArchetypeCount;
        int[] componentIds =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<Velocity>.Id,
        ];

        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleSpawn(
                componentIds,
                static view =>
                {
                    var position = new Position { X = 10, Y = 20 };
                    view.Write(in position);
                    throw new BundleCallbackException();
                }));

        Assert.Equal(0, world.EntityCount);
        Assert.Equal(archetypeCount, world.ArchetypeCount);

        Entity entity = world.ExecuteBundleSpawn(
            componentIds,
            static view =>
            {
                var position = new Position { X = 1, Y = 2 };
                var velocity = new Velocity { X = 3, Y = 4 };
                view.Write(in position);
                view.Write(in velocity);
            });

        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(4, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void ExecuteBundleSpawnBatch_LaterFaultPublishesNoRows()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<Position>.Id];

        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleSpawnBatch(
                componentIds,
                4,
                static view =>
                {
                    var position = new Position { X = view.Index, Y = view.Index + 1 };
                    view.Write(in position);
                    if (view.Index == 2)
                        throw new BundleCallbackException();
                }));

        Assert.Equal(0, world.EntityCount);

        world.ExecuteBundleSpawnBatch(
            componentIds,
            2,
            static view =>
            {
                var position = new Position { X = view.Index + 10, Y = view.Index + 20 };
                view.Write(in position);
            });

        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void ExecuteBundleAddAndReplace_FaultsPreservePublishedEntity()
    {
        var world = new World();
        Entity entity = world.CreateEntity(new Position { X = 1, Y = 2 });
        int[] velocityIds = [ComponentMetadata<Velocity>.Id];

        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleAdd(
                entity,
                velocityIds,
                static view =>
                {
                    var velocity = new Velocity { X = 3, Y = 4 };
                    view.Write(in velocity);
                    throw new BundleCallbackException();
                }));

        Assert.False(world.Has<Velocity>(entity));
        Assert.Equal(1, world.Read<Position>(entity).X);

        world.ExecuteBundleAdd(
            entity,
            velocityIds,
            static view =>
            {
                var velocity = new Velocity { X = 5, Y = 6 };
                view.Write(in velocity);
            });

        int[] replacementIds =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<Velocity>.Id,
        ];
        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleReplace(
                entity,
                replacementIds,
                static view =>
                {
                    var position = new Position { X = 100, Y = 200 };
                    view.Write(in position);
                    throw new BundleCallbackException();
                }));

        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(6, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void ExecuteBundleSpawn_CompositeStorageFaultDiscardsEveryOwnerMutation()
    {
        var world = new World();
        int[] componentIds =
        [
            ComponentMetadata<NameIndex>.Id,
            ComponentMetadata<SceneId>.Id,
            BufferComponents.Header<IntElement>(),
            BufferComponents.Inline<IntElement>(),
        ];
        int[] sparseComponentIds = [ComponentMetadata<Damage>.Id];

        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleSpawn(
                componentIds,
                sparseComponentIds,
                static view =>
                {
                    var scene = new SceneId { Value = 7 };
                    var name = new NameIndex { Value = "discarded" };
                    var damage = new Damage { Amount = 9 };
                    ReadOnlyMemory<IntElement> buffer = new IntElement[]
                    {
                        new IntElement { Value = 11 },
                        new IntElement { Value = 12 },
                    };
                    view.WriteShared(in scene);
                    view.Write(in name);
                    view.WriteSparse(in damage);
                    view.WriteBuffer(in buffer);
                    throw new BundleCallbackException();
                }));

        Assert.Equal(0, world.EntityCount);
        Assert.Empty(world.GetByIndex<NameIndex, string>("discarded").ToArray());

        Entity entity = world.ExecuteBundleSpawn(
            componentIds,
            sparseComponentIds,
            static view =>
            {
                var scene = new SceneId { Value = 3 };
                var name = new NameIndex { Value = "published" };
                var damage = new Damage { Amount = 5 };
                ReadOnlyMemory<IntElement> buffer =
                    new IntElement[] { new IntElement { Value = 13 } };
                view.WriteShared(in scene);
                view.Write(in name);
                view.WriteSparse(in damage);
                view.WriteBuffer(in buffer);
            });

        Assert.Equal(3, world.GetShared<SceneId>(entity).Value);
        Assert.Equal(5, world.ReadSparse<Damage>(entity).Amount);
        Assert.Contains(entity, world.GetByIndex<NameIndex, string>("published").ToArray());
        world.ExecuteBufferRead<IntElement>(entity, static buffer =>
        {
            Assert.Equal(1, buffer.Count);
            Assert.Equal(13, buffer[0].Value);
        });
    }

    [Fact]
    public void ExecuteBundleSpawn_MissingOrUndeclaredWriteCannotPublish()
    {
        var world = new World();
        int[] spawnIds =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<Velocity>.Id,
        ];

        Assert.Throws<InvalidOperationException>(() =>
            world.ExecuteBundleSpawn(
                spawnIds,
                static view =>
                {
                    var position = new Position { X = 1, Y = 2 };
                    view.Write(in position);
                }));
        Assert.Equal(0, world.EntityCount);

        Entity entity = world.CreateEntity(new Position { X = 4, Y = 5 });
        int[] replacementIds = [ComponentMetadata<Position>.Id];
        Assert.Throws<InvalidOperationException>(() =>
            world.ExecuteBundleReplace(
                entity,
                replacementIds,
                static view =>
                {
                    var velocity = new Velocity { X = 8, Y = 9 };
                    view.Write(in velocity);
                }));

        Assert.Equal(4, world.Read<Position>(entity).X);
        Assert.False(world.Has<Velocity>(entity));
    }

    [Fact]
    public void ExecuteBundleSpawn_SharedWriteAfterMaterializationCannotPublish()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<SceneId>.Id];

        Assert.Throws<InvalidOperationException>(() =>
            world.ExecuteBundleSpawn(
                componentIds,
                static view =>
                {
                    var scene = new SceneId { Value = 6 };
                    view.WriteShared(in scene);
                    _ = view.Entity;
                    view.WriteShared(in scene);
                }));

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ExecuteBundleSpawn_HookFaultDiscardsCandidateAndReleasesTransaction()
    {
        var world = new World();
        world.Hooks<Position>().OnAdd(
            static (DeferredWorld _, Entity _, in Position _) =>
                throw new BundleCallbackException());
        int[] positionIds = [ComponentMetadata<Position>.Id];

        Assert.Throws<BundleCallbackException>(() =>
            world.ExecuteBundleSpawn(
                positionIds,
                static view =>
                {
                    var position = new Position { X = 1, Y = 2 };
                    view.Write(in position);
                }));
        Assert.Equal(0, world.EntityCount);

        int[] velocityIds = [ComponentMetadata<Velocity>.Id];
        Entity entity = world.ExecuteBundleSpawn(
            velocityIds,
            static view =>
            {
                var velocity = new Velocity { X = 3, Y = 4 };
                view.Write(in velocity);
            });
        Assert.Equal(4, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void ExecuteBundleSpawnBatch_MaterializesOnlyTheCurrentCallbackRow()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<Position>.Id];

        world.ExecuteBundleSpawnBatch(
            componentIds,
            4,
            view =>
            {
                Assert.Equal(view.Index, world.EntityCount);
                var position = new Position { X = view.Index, Y = view.Index + 1 };
                view.Write(in position);
                Assert.Equal(view.Index + 1, world.EntityCount);
            });

        Assert.Equal(4, world.EntityCount);
    }

    [Fact]
    public void ExecuteBundleSpawnBatch_RechecksRawWriteEligibilityAfterLazyIndexCreation()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<IndexedName>.Id];

        world.ExecuteBundleSpawnBatch(
            componentIds,
            3,
            view =>
            {
                string value = $"name-{view.Index}";
                var component = new IndexedName { Value = value };
                view.Write(in component);
                if (view.Index == 0)
                    Assert.Single(world.GetByIndex<IndexedName, string>(value).ToArray());
            });

        for (int i = 0; i < 3; i++)
            Assert.Single(world.GetByIndex<IndexedName, string>($"name-{i}").ToArray());
    }

    [Fact]
    public void ExecuteBundleSpawnBatch_RejectsCapturedWorldMutationsWithoutDamagingTheCandidate()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<Position>.Id];

        world.ExecuteBundleSpawnBatch(
            componentIds,
            2,
            view =>
            {
                var position = new Position { X = view.Index + 1, Y = view.Index + 2 };
                view.Write(in position);
                Entity entity = view.Entity;

                Assert.Throws<InvalidOperationException>(() => world.CreateEntity());
                Assert.Throws<InvalidOperationException>(() => world.DestroyEntity(entity));
                Assert.Throws<InvalidOperationException>(
                    () => world.Add(entity, new Velocity { X = 3, Y = 4 }));
                Assert.Throws<InvalidOperationException>(() => world.Remove<Position>(entity));
                Assert.Throws<InvalidOperationException>(
                    () => world.Replace(entity, new Position { X = 9, Y = 10 }));
                Assert.Throws<InvalidOperationException>(
                    () => world.ExecuteBundleSpawn(
                        componentIds,
                        static nestedView =>
                        {
                            var nested = new Position { X = 11, Y = 12 };
                            nestedView.Write(in nested);
                        }));
            });

        Assert.Equal(2, world.EntityCount);
        var values = new List<float>();
        var query = world.Query(world.QueryDefinition().Read<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                values.Add(row.Read<Position>().X);
        });
        values.Sort();
        Assert.Equal([1f, 2f], values);
    }

    [Fact]
    public void ExecuteBundleSpawn_RejectsLazyIndexBackfillForPendingComponent()
    {
        var world = new World();
        int[] componentIds = [ComponentMetadata<IndexedName>.Id];

        Entity entity = world.ExecuteBundleSpawn(
            componentIds,
            view =>
            {
                _ = view.Entity;
                Assert.Throws<InvalidOperationException>(
                    () => world.GetByIndex<IndexedName, string>("pending").ToArray());
                var name = new IndexedName { Value = "ready" };
                view.Write(in name);
                Assert.Single(world.GetByIndex<IndexedName, string>("ready").ToArray());
            });

        Assert.Equal([entity], world.GetByIndex<IndexedName, string>("ready").ToArray());
    }

    [Fact]
    public void ExecuteBundleSpawn_HookCannotMutateWorldOrBackfillAPendingIndex()
    {
        var world = new World();
        world.Hooks<Position>().OnAdd(
            (DeferredWorld deferred, Entity entity, in Position _) =>
            {
                Assert.Throws<InvalidOperationException>(() => world.DestroyEntity(entity));
                Assert.Throws<InvalidOperationException>(
                    () => deferred.GetByIndex<IndexedName, string>("pending").ToArray());
            });
        int[] componentIds =
        [
            ComponentMetadata<Position>.Id,
            ComponentMetadata<IndexedName>.Id,
        ];

        Entity entity = world.ExecuteBundleSpawn(
            componentIds,
            static view =>
            {
                var position = new Position { X = 1, Y = 2 };
                var name = new IndexedName { Value = "ready" };
                view.Write(in position);
                view.Write(in name);
            });

        Assert.True(world.IsAlive(entity));
        Assert.Equal([entity], world.GetByIndex<IndexedName, string>("ready").ToArray());
    }

    private sealed class BundleCallbackException : Exception;
}
