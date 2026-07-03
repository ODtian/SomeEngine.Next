using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;
using Xunit;

namespace SomeEngine.ECS.Tests;

public struct MaterialPassTemplate : SomeEngine.ECS.Components.IComponent
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
        var source = world.CreateEntity(new Position { X = 1, Y = 2 });
        world.Add(source, new Health { Value = 100 });
        world.AddTag<PlayerTag>(source);

        var clone = world.CloneEntity(source);

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
        var source = world.CreateEntity(new Position { X = 10, Y = 20 });
        world.AddTag<PlayerTag>(source);
        world.AddShared(source, new SceneId { Value = 7 });
        world.AddBuffer<IntElement>(source);
        world.GetBuffer<IntElement>(source).Add(new IntElement { Value = 1 });

        var target = world.CreateEntity(new Health { Value = 5 });
        world.AddTag<EnemyTag>(target);
        world.AddShared(target, new RenderGroup { GroupId = 3, Material = "old" });
        world.AddBuffer<FloatElement>(target);
        world.GetBuffer<FloatElement>(target).Add(new FloatElement { X = 9, Y = 9 });
        world.AddSparse(target, new Damage { Amount = 99 });

        world.CopyEntity(source, target);

        Assert.True(world.Has<Position>(target));
        Assert.False(world.Has<Health>(target));
        Assert.True(world.Has<PlayerTag>(target));
        Assert.False(world.Has<EnemyTag>(target));
        Assert.True(world.HasShared<SceneId>(target));
        Assert.False(world.HasShared<RenderGroup>(target));
        Assert.True(world.HasBuffer<IntElement>(target));
        Assert.False(world.HasBuffer<FloatElement>(target));
        Assert.False(world.HasSparse<Damage>(target));
        Assert.Equal(10, world.Read<Position>(target).X);
        Assert.Equal(7, world.GetShared<SceneId>(target).Value);
        Assert.Equal(1, world.GetBuffer<IntElement>(target).Read(0).Value);
    }

    [Fact]
    public void CopyNoRemoved()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity(new Position { X = 4, Y = 5 });

        world.CopyEntity(source, target);

        Assert.True(world.IsAlive(target));
        Assert.False(world.Has<Position>(target));
        Assert.Equal(0, CountRows(world.RunQuery(world.Query(world.QueryDefinition().Removed<Position>()))));
    }

    [Fact]
    public void CloneEntity_PreservesEnableableState()
    {
        var world = new World();
        var source = world.CreateEntity(new Stunned { Duration = 3 });
        world.Disable<Stunned>(source);

        var clone = world.CloneEntity(source);

        Assert.True(world.Has<Stunned>(clone));
        Assert.False(world.IsEnabled<Stunned>(clone));
        Assert.Equal(3, world.Read<Stunned>(clone).Duration);
    }

    [Fact]
    public void CopyEntity_CopiesSharedFromChunkTupleAndLeavesNoStaleSharedIndex()
    {
        var world = new World();
        var source = world.CreateEntity(new Position { X = 1 });
        var target = world.CreateEntity(new Position { X = 2 });
        world.AddShared(source, new SceneId { Value = 11 });
        world.AddShared(target, new SceneId { Value = 22 });

        world.CopyEntity(source, target);

        Assert.Equal(11, world.GetShared<SceneId>(target).Value);

        int reusedIndex = target.Index;
        world.DestroyEntity(target);
        var reused = world.CreateEntity();

        Assert.Equal(reusedIndex, reused.Index);
        Assert.False(world.HasShared<SceneId>(reused));
    }

    [Fact]
    public void CloneEntity_CopiesDynamicBufferWithoutAliasingOverflowStorage()
    {
        var world = new World();
        var source = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(source);
        var sourceBuffer = world.GetBuffer<SmallInlineElement>(source);
        sourceBuffer.Add(new SmallInlineElement { Value = 1 });
        sourceBuffer.Add(new SmallInlineElement { Value = 2 });
        sourceBuffer.Add(new SmallInlineElement { Value = 3 });
        sourceBuffer.Add(new SmallInlineElement { Value = 4 });

        var clone = world.CloneEntity(source);
        var cloneBuffer = world.GetBuffer<SmallInlineElement>(clone);
        cloneBuffer[0] = new SmallInlineElement { Value = 99 };
        cloneBuffer.Add(new SmallInlineElement { Value = 5 });

        Assert.Equal([1, 2, 3, 4], world.GetBuffer<SmallInlineElement>(source).AsSpan().ToArray().Select(x => x.Value).ToArray());
        Assert.Equal([99, 2, 3, 4, 5], world.GetBuffer<SmallInlineElement>(clone).AsSpan().ToArray().Select(x => x.Value).ToArray());
    }

    [Fact]
    public void CopyEntity_CopiesSparseAndRemovesStaleTargetSparse()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        world.AddSparse(source, new Damage { Amount = 12 });
        world.AddSparse(target, new Damage { Amount = 99 });

        world.CopyEntity(source, target);

        Assert.True(world.HasSparse<Damage>(target));
        Assert.Equal(12, world.GetSparse<Damage>(target).Amount);

        world.RemoveSparse<Damage>(source);
        world.CopyEntity(source, target);

        Assert.False(world.HasSparse<Damage>(target));
    }

    [Fact]
    public void CloneEntity_ExcludesCleanupByDefaultAndIncludesWhenRequested()
    {
        var world = new World();
        var source = world.CreateEntity(new Position { X = 3 });
        world.Add(source, new CleanupMarker { Value = 8 });

        var defaultClone = world.CloneEntity(source);
        var cleanupClone = world.CloneEntity(source, EntityCopyOptions.Standard | EntityCopyOptions.CleanupComponents);

        Assert.True(world.Has<Position>(defaultClone));
        Assert.False(world.Has<CleanupMarker>(defaultClone));
        Assert.True(world.Has<Position>(cleanupClone));
        Assert.True(world.Has<CleanupMarker>(cleanupClone));
        Assert.Equal(8, world.Read<CleanupMarker>(cleanupClone).Value);
    }

    [Fact]
    public void CopyEntity_RelationsExcludedByDefaultAndOutgoingOnlyWhenRequested()
    {
        var world = new World();
        var source = world.CreateEntity(new Position { X = 1 });
        var target = world.CreateEntity(new Position { X = 2 });
        var sourceRelationTarget = world.CreateEntity();
        var oldTargetRelationTarget = world.CreateEntity();
        var incomingSource = world.CreateEntity();

        world.AddRelation(source, sourceRelationTarget, new Likes { Strength = 4 });
        world.AddRelation(target, oldTargetRelationTarget, new Likes { Strength = 9 });
        world.AddRelation(incomingSource, source, new Likes { Strength = 7 });

        var defaultClone = world.CloneEntity(source);

        Assert.False(world.HasRelation<Likes>(defaultClone, sourceRelationTarget));
        Assert.False(world.Has<RelationTag<Likes>>(defaultClone));

        world.CopyEntity(source, target, EntityCopyOptions.Standard | EntityCopyOptions.OutgoingRelations);

        Assert.True(world.HasRelation<Likes>(target, sourceRelationTarget));
        Assert.False(world.HasRelation<Likes>(target, oldTargetRelationTarget));
        Assert.Empty(world.GetRelationSources<Likes>(target).ToArray());
        Assert.True(world.Has<RelationTag<Likes>>(target));
    }

    [Fact]
    public void CopyEntity_RejectsDeadForeignAndPendingCleanupEntities()
    {
        var world = new World();
        var live = world.CreateEntity();
        var dead = world.CreateEntity();
        world.DestroyEntity(dead);
        var foreignWorld = new World();
        _ = foreignWorld.CreateEntity();
        var foreign = foreignWorld.CreateEntity();
        var pendingCleanup = world.CreateEntity(new CleanupMarker { Value = 1 });
        world.DestroyEntity(pendingCleanup);

        Assert.Throws<InvalidOperationException>(() => world.CloneEntity(dead));
        Assert.Throws<InvalidOperationException>(() => world.CloneEntity(foreign));
        Assert.Throws<InvalidOperationException>(() => world.CloneEntity(pendingCleanup));
        Assert.Throws<InvalidOperationException>(() => world.CopyEntity(live, pendingCleanup));
    }

    [Fact]
    public void CopyEntity_ThrowsDuringActiveIteration()
    {
        var world = new World();
        var source = world.CreateEntity(new Position { X = 1 });
        var target = world.CreateEntity(new Position { X = 2 });
        var query = world.Query(world.QueryDefinition().Read<Position>());

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in world.RunQuery(query).Rows)
                world.CopyEntity(source, target);
        });
    }

    [Fact]
    public void CloneEntity_MaterialPassTemplateCanDivergeFromSource()
    {
        var world = new World();
        var referenced = world.CreateEntity();
        var source = world.CreateEntity(new MaterialPassTemplate
        {
            ShaderId = 1,
            Reference = new EntityReferenceLike
            {
                Index = referenced.Index,
                Generation = referenced.Generation,
            },
        });
        world.AddShared(source, new SceneId { Value = 100 });
        world.AddBuffer<SmallInlineElement>(source);
        world.GetBuffer<SmallInlineElement>(source).Add(new SmallInlineElement { Value = 3 });
        world.GetBuffer<SmallInlineElement>(source).Add(new SmallInlineElement { Value = 4 });
        world.GetBuffer<SmallInlineElement>(source).Add(new SmallInlineElement { Value = 5 });
        world.AddSparse(source, new Damage { Amount = 6 });

        var clone = world.CloneEntity(source);
        world.Replace(clone, new MaterialPassTemplate
        {
            ShaderId = 2,
            Reference = world.Read<MaterialPassTemplate>(clone).Reference,
        });
        world.ReplaceShared(clone, new SceneId { Value = 200 });
        world.GetBuffer<SmallInlineElement>(clone)[0] = new SmallInlineElement { Value = 99 };
        world.GetSparse<Damage>(clone).Amount = 60;

        Assert.Equal(1, world.Read<MaterialPassTemplate>(source).ShaderId);
        Assert.Equal(referenced.Index, world.Read<MaterialPassTemplate>(source).Reference.Index);
        Assert.Equal(100, world.GetShared<SceneId>(source).Value);
        Assert.Equal([3, 4, 5], world.GetBuffer<SmallInlineElement>(source).AsSpan().ToArray().Select(x => x.Value).ToArray());
        Assert.Equal(6, world.GetSparse<Damage>(source).Amount);

        Assert.Equal(2, world.Read<MaterialPassTemplate>(clone).ShaderId);
        Assert.Equal(200, world.GetShared<SceneId>(clone).Value);
        Assert.Equal([99, 4, 5], world.GetBuffer<SmallInlineElement>(clone).AsSpan().ToArray().Select(x => x.Value).ToArray());
        Assert.Equal(60, world.GetSparse<Damage>(clone).Amount);
    }

    private static int CountRows(SomeEngine.ECS.Queries.QueryCursor run)
    {
        int count = 0;
        foreach (var _ in run.Rows)
            count++;

        return count;
    }
}
