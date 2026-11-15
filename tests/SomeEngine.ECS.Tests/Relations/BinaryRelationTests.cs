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
        var first = world.Create(new Score { Value = 1 });
        var overlap = world.Create(new Score { Value = 4 });
        var last = world.Create(new Score { Value = 7 });
        world.Add(first, new LeftSet());
        world.Add(overlap, new LeftSet());
        world.Add(overlap, new RightSet());
        world.Add(last, new RightSet());
        var left = world.Query(new QueryDescription().WithStored<LeftSet>());
        var right = world.Query(new QueryDescription().WithStored<RightSet>());
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
        var first = world.Create(new Score { Value = 1 });
        var middle = world.Create(new Score { Value = 4 });
        var last = world.Create(new Score { Value = 7 });
        world.Add(first, new LeftSet());
        var members = world.Query(new QueryDescription().WithStored<Score>());
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
        var first = world.Create(new Score { Value = 1 });
        var second = world.Create(new Score { Value = 4 });
        var third = world.Create(new Score { Value = 7 });
        var last = world.Create(new Score { Value = 10 });
        world.Add(first, new RightSet());
        world.Add(second, new LeftSet());
        world.Add(second, new RightSet());
        world.Add(third, new LeftSet());
        world.Add(third, new RightSet());
        world.Add(last, new LeftSet());
        var left = world.Query(new QueryDescription().WithStored<LeftSet>());
        var right = world.Query(new QueryDescription().WithStored<RightSet>());
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
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        ulong baseline = world.Version;
        var score = world.Create(new Score { Value = 1 });
        world.Add(score, new Temperature { Value = 20 });
        world.Create(new Weight { Value = 8 });
        var left = world.Query(new QueryDescription().WithStored<Weight>().Without<Score>());
        var right = world.Query(new QueryDescription().WithStored<Score>().WithChanged<Temperature>(0).Without<Weight>());

        Assert.Equal(1, world.Between(left, right).Where(new ScoreWeightGap(5)).Count(baseline));
    }

    [Fact]
    public void WithinBindsDifferentOptionalComponentsByOutputPosition()
    {
        using var world = new World();
        var score = world.Create(new Score { Value = 1 });
        var weight = world.Create(new Weight { Value = 8 });
        var ordinary = world.Create();
        world.Add(weight, new LeftSet());
        world.Add(score, new LeftSet());
        world.Add(ordinary, new LeftSet());
        var members = world.Query(new QueryDescription().WithStored<LeftSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Within(members).Where(new ScoreWeightGap(5)).Enumerate(ref output);

        Assert.Equal([(score, weight)], pairs);
        Assert.Equal(2, world.Within(members).Except(new ScoreWeightGap(5)).Count());
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

public struct Temperature : IAddedComponent
{
    public int Value;
}

public readonly struct ScoreWeightGap(int minimum) : IRelation<Score, Weight>
{
    public bool Matches(in Score score, in Weight weight) => weight.Value - score.Value >= minimum;
}
