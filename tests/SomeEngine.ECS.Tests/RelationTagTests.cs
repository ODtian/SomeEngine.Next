using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class RelationTagTests
{
    [Fact]
    public void AddRelation_AutoAddsRelationTag()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();

        // 添加 relation 前，source 没有 RelationTag
        Assert.False(world.Has<RelationTag<Likes>>(e1));

        world.AddRelation(e1, e2, new Likes { Strength = 1.0f });

        // 添加 relation 后，source 自动获得 RelationTag
        Assert.True(world.Has<RelationTag<Likes>>(e1));
    }

    [Fact]
    public void RemoveLastRelation_AutoRemovesRelationTag()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();
        var e3 = world.CreateEntity();

        world.AddRelation(e1, e2, new Likes { Strength = 1.0f });
        world.AddRelation(e1, e3, new Likes { Strength = 2.0f });

        Assert.True(world.Has<RelationTag<Likes>>(e1));

        // 移除一个 relation，tag 应该保留
        world.RemoveRelation<Likes>(e1, e2);
        Assert.True(world.Has<RelationTag<Likes>>(e1));

        // 移除最后一个 relation，tag 应自动移除
        world.RemoveRelation<Likes>(e1, e3);
        Assert.False(world.Has<RelationTag<Likes>>(e1));
    }

    [Fact]
    public void DestroyTarget_DropsTag()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(source, target, new Likes { Strength = 1.0f });

        world.DestroyEntity(target);

        Assert.Empty(world.GetRelations<Likes>(source).ToArray());
        Assert.False(world.Has<RelationTag<Likes>>(source));
    }

    [Fact]
    public void RelationTag_QueryFilter_OnlyMatchesRelatedEntities()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 1, Y = 1 });
        var e2 = world.CreateEntity<Position>(new Position { X = 2, Y = 2 });
        var e3 = world.CreateEntity<Position>(new Position { X = 3, Y = 3 });
        var target = world.CreateEntity();

        // 只有 e1 和 e3 有 Likes relation
        world.AddRelation(e1, target, new Likes { Strength = 1.0f });
        world.AddRelation(e3, target, new Likes { Strength = 3.0f });

        // 使用 Query 过滤有 RelationTag<Likes> 的 entity
        var query = world.CreateQuery()
            .With<Position>()
            .With<RelationTag<Likes>>()
            .Build();

        int count = 0;
        foreach (var arch in query.Archetypes)
        {
            foreach (var chunk in arch.Chunks)
            {
                count += chunk.Count;
            }
        }

        Assert.Equal(2, count); // 只有 e1 和 e3 匹配
    }

    [Fact]
    public void RelationTag_ComponentsPreservedOnTagAdd()
    {
        var world = new World();
        var e1 = world.CreateEntity<Position>(new Position { X = 5, Y = 10 });
        var target = world.CreateEntity();

        world.AddRelation(e1, target, new Likes { Strength = 1.0f });

        // Position 应该在 RelationTag 迁移后保持
        Assert.Equal(5, world.Read<Position>(e1).X);
        Assert.Equal(10, world.Read<Position>(e1).Y);
    }

    [Fact]
    public void RelationTag_DifferentRelationTypes_IndependentTags()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var target = world.CreateEntity();

        world.AddRelation(e1, target, new Likes { Strength = 1.0f });
        world.AddRelation(e1, target, new Owns { Slot = 1 });

        Assert.True(world.Has<RelationTag<Likes>>(e1));
        Assert.True(world.Has<RelationTag<Owns>>(e1));

        // 移除 Likes 不影响 Owns tag
        world.RemoveRelation<Likes>(e1, target);
        Assert.False(world.Has<RelationTag<Likes>>(e1));
        Assert.True(world.Has<RelationTag<Owns>>(e1));
    }
}
