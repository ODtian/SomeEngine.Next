using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct MaterialPassTemplate : SomeEngine.ECS.IAddedComponent
{
    public int ShaderId;
    public EntityReferenceLike Reference;
}

public struct EntityReferenceLike
{
    public int Index;
    public int Generation;
}

public class EntityCopyTests
{
    [Fact]
    public void CloneEntity_CopiesTableValuesAndTags()
    {
        var world = new World();
        var source = world.Create(new Position { X = 1, Y = 2 });
        world.Add(source, new Health { Value = 100 });
        world.Add(source, default(PlayerTag));

        var clone = world.Clone(source);

        Assert.NotEqual(source, clone);
        Assert.True(world.Has<Position>(clone));
        Assert.True(world.Has<Health>(clone));
        Assert.True(world.Has<PlayerTag>(clone));
        Assert.Equal(1, world.Read<Position>(clone).X);
        Assert.Equal(2, world.Read<Position>(clone).Y);
        Assert.Equal(100, world.Read<Health>(clone).Value);
    }

    [Fact]
    public void CopyEntity_ReplacesNonEmptyTargetIncludedSurface()
    {
        var world = new World();
        var source = world.Create(new Position { X = 10, Y = 20 });
        world.Add(source, default(PlayerTag));
        world.SetShared(source, new SceneId { Value = 7 });
        world.AddBuffer<IntElement>(source);
        AddBufferValue(world, source, new IntElement { Value = 1 });

        var target = world.Create(new Health { Value = 5 });
        world.Add(target, default(EnemyTag));
        world.SetShared(target, new RenderGroup { GroupId = 3, Material = "old" });
        world.AddBuffer<FloatElement>(target);
        AddBufferValue(world, target, new FloatElement { X = 9, Y = 9 });
        world.SetSparse(target, new Damage { Amount = 99 });

        world.Copy(source, target);

        Assert.True(world.Has<Position>(target));
        Assert.False(world.Has<Health>(target));
        Assert.True(world.Has<PlayerTag>(target));
        Assert.False(world.Has<EnemyTag>(target));
        Assert.True(world.Has<SceneId>(target));
        Assert.False(world.Has<RenderGroup>(target));
        Assert.True(world.Has<Buffer<IntElement>>(target));
        Assert.False(world.Has<Buffer<FloatElement>>(target));
        Assert.False(world.HasSparse<Damage>(target));
        Assert.Equal(10, world.Read<Position>(target).X);
        Assert.Equal(7, world.ReadShared<SceneId>(target).Value);
        Assert.Equal(1, SnapshotBuffer<IntElement>(world, target)[0].Value);
    }

    [Fact]
    public void CopyNoRemoved()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create(new Position { X = 4, Y = 5 });

        world.Copy(source, target);

        Assert.True(world.IsAlive(target));
        Assert.False(world.Has<Position>(target));
        Assert.Equal(
            0,
            CountRows(world, world.Query(new QueryDescription().WithStored<Removed<Position>>())));
    }


    [Fact]
    public void CloneEntity_PreservesEnableableState()
    {
        var world = new World();
        var source = world.Create(new Stunned { Duration = 3 });
        world.Disable<Stunned>(source);

        var clone = world.Clone(source);

        Assert.True(world.Has<Stunned>(clone));
        Assert.False(world.IsEnabled<Stunned>(clone));
        Assert.Equal(3, world.Read<Stunned>(clone).Duration);
    }

    [Fact]
    public void CopyEntity_CopiesSharedFromChunkTupleAndLeavesNoStaleSharedIndex()
    {
        var world = new World();
        var source = world.Create(new Position { X = 1 });
        var target = world.Create(new Position { X = 2 });
        world.SetShared(source, new SceneId { Value = 11 });
        world.SetShared(target, new SceneId { Value = 22 });

        world.Copy(source, target);

        Assert.Equal(11, world.ReadShared<SceneId>(target).Value);

        int reusedIndex = target.Id;
        world.Destroy(target);
        var reused = world.Create();

        Assert.Equal(reusedIndex, reused.Id);
        Assert.False(world.Has<SceneId>(reused));
    }

    [Fact]
    public void CloneEntity_CopiesDynamicBufferWithoutAliasingOverflowStorage()
    {
        var world = new World();
        var source = world.Create();
        world.AddBuffer<SmallInlineElement>(source);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(source);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });
            buffer.Add(new SmallInlineElement { Value = 4 });

            }
            finally { buffer.Dispose(); }
        };

        var clone = world.Clone(source);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(clone);
            try
            {

            buffer[0] = new SmallInlineElement { Value = 99 };
            buffer.Add(new SmallInlineElement { Value = 5 });

            }
            finally { buffer.Dispose(); }
        };

        Assert.Equal([1, 2, 3, 4], SnapshotBuffer<SmallInlineElement>(world, source).Select(x => x.Value));
        Assert.Equal([99, 2, 3, 4, 5], SnapshotBuffer<SmallInlineElement>(world, clone).Select(x => x.Value));
    }

    [Fact]
    public void CopyEntity_CopiesSparseAndRemovesStaleTargetSparse()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        world.SetSparse(source, new Damage { Amount = 12 });
        world.SetSparse(target, new Damage { Amount = 99 });

        world.Copy(source, target);

        Assert.True(world.HasSparse<Damage>(target));
        Assert.Equal(12, world.ReadSparse<Damage>(target).Amount);

        world.RemoveSparse<Damage>(source);
        world.Copy(source, target);

        Assert.False(world.HasSparse<Damage>(target));
    }

    [Fact]
    public void CloneCopiesTheStoredCleanupValue()
    {
        using var world = new World();
        Entity source = world.Create(new Position { X = 3 }, new CleanupMarker { Value = 8 });
        Entity clone = world.Clone(source);
        Assert.True(world.Has<Position>(clone));
        Assert.True(world.Has<CleanupMarker>(clone));
        Assert.Equal(8, world.Read<CleanupMarker>(clone).Value);
    }

    [Fact]
    public void CopyRequiresLiveIdentitiesAndAnActiveReplacementTarget()
    {
        using var world = new World();
        Entity live = world.Create();
        Entity dead = world.Create();
        world.Destroy(dead);
        Entity cleanup = world.Create(new CleanupMarker { Value = 1 });
        world.Destroy(cleanup);
        Assert.Throws<ArgumentException>(() => world.Clone(dead));
        Assert.Throws<InvalidOperationException>(() => world.Copy(live, cleanup));
        Assert.True(world.IsPendingCleanup(cleanup));
    }

    [Fact]
    public void CopyEntity_ThrowsDuringActiveIteration()
    {
        var world = new World();
        var source = world.Create(new Position { X = 1 });
        var target = world.Create(new Position { X = 2 });
        var query = world.Query(new QueryDescription().WithStored<Position>());

        Assert.Throws<InvalidOperationException>(() =>
        {
            {
            using QueryScope cursor = (query).OpenRead();
                foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                    world.Copy(source, target);

        };
        });
    }

    [Fact]
    public void CloneEntity_MaterialPassTemplateCanDivergeFromSource()
    {
        var world = new World();
        var referenced = world.Create();
        var source = world.Create(new MaterialPassTemplate
        {
            ShaderId = 1,
            Reference = new EntityReferenceLike
            {
                Index = referenced.Id,
                Generation = referenced.Generation,
            },
        });
        world.SetShared(source, new SceneId { Value = 100 });
        world.AddBuffer<SmallInlineElement>(source);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(source);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 3 });
            buffer.Add(new SmallInlineElement { Value = 4 });
            buffer.Add(new SmallInlineElement { Value = 5 });

            }
            finally { buffer.Dispose(); }
        };
        world.SetSparse(source, new Damage { Amount = 6 });

        var clone = world.Clone(source);
        world.Set(clone, new MaterialPassTemplate
        {
            ShaderId = 2,
            Reference = world.Read<MaterialPassTemplate>(clone).Reference,
        });
        world.SetShared(clone, new SceneId { Value = 200 });
        {
            var buffer = world.GetBuffer<SmallInlineElement>(clone);
            try
            {
buffer[0] = new SmallInlineElement { Value = 99 };
            }
            finally { buffer.Dispose(); }
        };
        world.SetSparse(clone, new Damage { Amount = 60 });

        Assert.Equal(1, world.Read<MaterialPassTemplate>(source).ShaderId);
        Assert.Equal(referenced.Id, world.Read<MaterialPassTemplate>(source).Reference.Index);
        Assert.Equal(100, world.ReadShared<SceneId>(source).Value);
        Assert.Equal([3, 4, 5], SnapshotBuffer<SmallInlineElement>(world, source).Select(x => x.Value));
        Assert.Equal(6, world.ReadSparse<Damage>(source).Amount);

        Assert.Equal(2, world.Read<MaterialPassTemplate>(clone).ShaderId);
        Assert.Equal(200, world.ReadShared<SceneId>(clone).Value);
        Assert.Equal([99, 4, 5], SnapshotBuffer<SmallInlineElement>(world, clone).Select(x => x.Value));
        Assert.Equal(60, world.ReadSparse<Damage>(clone).Amount);
    }

    private static int CountRows(World world, Query query) => query.Count;

    private static void AddBufferValue<T>(
        World world,
        SomeEngine.ECS.Entities.Entity entity,
        T value)
        where T : struct, IBufferElement
    {
        {
            var buffer = world.GetBuffer<T>(entity);
            try
            {
buffer.Add(value);
            }
            finally { buffer.Dispose(); }
        };
    }

    private static T[] SnapshotBuffer<T>(
        World world,
        SomeEngine.ECS.Entities.Entity entity)
        where T : struct, IBufferElement
    {
        T[] values = null!;
        {
            using ReadOnlyBuffer<T> buffer = world.ReadBuffer<T>(entity);
values = buffer.AsSpan().ToArray();
        };
        return values;
    }
}
