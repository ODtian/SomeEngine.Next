using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class BinaryRelationTests
{
    [Fact]
    public void ProductPreservesInputPositionsAndSelfPairs()
    {
        using var world = new World();
        var first = world.CreateEntity(new Score { Value = 1 });
        var overlap = world.CreateEntity(new Score { Value = 4 });
        var last = world.CreateEntity(new Score { Value = 7 });
        world.AddTag<LeftSet>(first);
        world.AddTag<LeftSet>(overlap);
        world.AddTag<RightSet>(overlap);
        world.AddTag<RightSet>(last);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Product(left, right).Enumerate(ref output);

        Assert.Equal(4, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(first, overlap), (first, last), (overlap, overlap), (overlap, last)]));
    }

    [Fact]
    public void WithinEmitsEachAscendingIdentityPairOnce()
    {
        using var world = new World();
        var first = world.CreateEntity(new Score { Value = 1 });
        var middle = world.CreateEntity(new Score { Value = 4 });
        var last = world.CreateEntity(new Score { Value = 7 });
        world.AddTag<LeftSet>(first);
        var members = world.Query(world.QueryDefinition().All<Score>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Within(members).Enumerate(ref output);

        Assert.Equal(3, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(first, middle), (first, last), (middle, last)]));
    }

    [Fact]
    public void BetweenDeduplicatesOverlapAndKeepsReversedDisjointPairs()
    {
        using var world = new World();
        var first = world.CreateEntity(new Score { Value = 1 });
        var second = world.CreateEntity(new Score { Value = 4 });
        var third = world.CreateEntity(new Score { Value = 7 });
        var last = world.CreateEntity(new Score { Value = 10 });
        world.AddTag<RightSet>(first);
        world.AddTag<LeftSet>(second);
        world.AddTag<RightSet>(second);
        world.AddTag<LeftSet>(third);
        world.AddTag<RightSet>(third);
        world.AddTag<LeftSet>(last);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Between(left, right).Enumerate(ref output);

        Assert.Equal(6, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([
            (first, second), (first, third), (first, last),
            (second, third), (second, last), (third, last)]));
    }

    [Fact]
    public void SortedBetweenBindsComponentsAcrossOriginalInputRoles()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        uint baseline = world.AcquireSystemTick();
        var score = world.CreateEntity(new Score { Value = 1 });
        world.Add(score, new Temperature { Value = 20 });
        world.CreateEntity(new Weight { Value = 8 });
        var left = world.Query(world.QueryDefinition().Read<Weight>().None<Score>());
        var right = world.Query(world.QueryDefinition().Read<Score>().Changed<Temperature>().None<Weight>());

        Assert.Equal(1, world.Between(left, right).Where(new ScoreWeightGap(5)).Count(baseline));
    }

    private struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair)
        {
            pairs.Add(pair);
            return true;
        }
    }
}

public struct LeftSet : ITag;
public struct RightSet : ITag;

public struct Temperature : IComponent
{
    public int Value;
}

public readonly struct ScoreWeightGap(int minimum) : IRelation<Score, Weight>
{
    public bool Matches(in Score score, in Weight weight) => weight.Value - score.Value >= minimum;
}
