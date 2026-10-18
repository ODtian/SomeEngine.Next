using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class MultiRelationTests
{
    [Fact]
    public void ThreePositionsPreserveRepeatedSetsAndApplyBothRules()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new Score { Value = 4 });
        world.CreateEntity(new Score { Value = 7 });
        world.CreateEntity(new Score { Value = 10 });
        var scores = world.Query(world.QueryDefinition().All<Score>());

        Assert.Equal(1, world.Product(scores, scores, scores)
            .Where(default(IncreasingScores3))
            .Except(new ThirdScoreMinimum(10))
            .Count());
    }

    [Fact]
    public void FourPositionsKeepTheRolesOfDifferentRepeatedSets()
    {
        using var world = new World();
        var first = world.CreateEntity(new Score { Value = 1 });
        var middle = world.CreateEntity(new Score { Value = 2 });
        var last = world.CreateEntity(new Score { Value = 3 });
        world.AddTag<LeftSet>(first);
        world.AddTag<LeftSet>(middle);
        world.AddTag<RightSet>(middle);
        world.AddTag<RightSet>(last);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var rows = new List<(Entity, Entity, Entity, Entity)>();
        var output = new RowList<(Entity, Entity, Entity, Entity)>(rows);

        world.Product(left, right, left, right)
            .Where(default(RepeatedBands4))
            .Except(new UpperBand4(3))
            .Enumerate(ref output);

        Assert.Equal(new[] { (first, middle, first, middle) }, rows);
    }

    [Fact]
    public void FivePositionsReadManagedComponentsAndApplyExclusion()
    {
        using var world = new World();
        world.CreateEntity(new DisplayName { Value = "Iris" });
        world.CreateEntity(new DisplayName { Value = "Rose" });
        world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new Score { Value = 2 });
        var names = world.Query(world.QueryDefinition().All<DisplayName>());
        var scores = world.Query(world.QueryDefinition().All<Score>());

        Assert.Equal(1, world.Product(names, scores, scores, scores, scores)
            .Where(default(AlternatingNames5))
            .Except(new ExcludedNamePrefix5("Ro"))
            .Count());
    }

    [Fact]
    public void SixPositionsCountTheRemainingOrderedRow()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new Score { Value = 2 });
        var scores = world.Query(world.QueryDefinition().All<Score>());

        Assert.Equal(1, world.Product(scores, scores, scores, scores, scores, scores)
            .Where(new ScoreSum6(11))
            .Except(new FirstScoreMinimum6(2))
            .Count());
    }

    [Fact]
    public void SevenPositionsPreserveExistenceAfterExclusion()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new Score { Value = 2 });
        var scores = world.Query(world.QueryDefinition().All<Score>());

        Assert.True(world.Product(scores, scores, scores, scores, scores, scores, scores)
            .Where(new ScoreSum7(13))
            .Except(new FirstScoreMinimum7(2))
            .Any());
    }

    [Fact]
    public void EightPositionsReturnTheEighthEntityAndForwardEarlyStopState()
    {
        using var world = new World();
        world.CreateEntity(new Score { Value = 1 });
        world.CreateEntity(new Score { Value = 2 });
        var expected = world.CreateEntity(new Weight { Value = 12 });
        world.CreateEntity(new Weight { Value = 20 });
        var scores = world.Query(world.QueryDefinition().All<Score>());
        var weights = world.Query(world.QueryDefinition().All<Weight>());
        var selected = world.Product(scores, scores, scores, scores, scores, scores, scores, weights)
            .Where(new AlternatingScores8(10))
            .Except(new ExcludedWeight8(15));
        Assert.Equal(1, selected.Count());
        Span<Entity> eighth = stackalloc Entity[1];
        var output = new EighthEntity(eighth);

        bool completed = selected.Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Equal(expected, eighth[0]);
    }

    private struct RowList<TRow>(List<TRow> rows) : IRelationOutput<TRow>
    {
        public bool Add(in TRow row)
        {
            rows.Add(row);
            return true;
        }
    }

    private ref struct EighthEntity : IRelationOutput<(Entity, Entity, Entity, Entity, Entity, Entity, Entity, Entity)>
    {
        private readonly Span<Entity> _result;

        public EighthEntity(Span<Entity> result)
        {
            _result = result;
            Count = 0;
        }

        public int Count { get; private set; }

        public bool Add(in (Entity, Entity, Entity, Entity, Entity, Entity, Entity, Entity) row)
        {
            _result[Count++] = row.Item8;
            return false;
        }
    }
}

// Business semantics used by the multi-position consumer checks.
public readonly struct IncreasingScores3 : IRelation<Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third) =>
        first.Value < second.Value && second.Value < third.Value;
}

public readonly struct ThirdScoreMinimum(int minimum) : IRelation<Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third) => third.Value >= minimum;
}

public readonly struct RepeatedBands4 : IRelation<Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth) =>
        first.Value < second.Value && first.Value == third.Value && second.Value == fourth.Value;
}

public readonly struct UpperBand4(int minimum) : IRelation<Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth) => second.Value >= minimum;
}

public readonly struct AlternatingNames5 : IRelation<DisplayName, Score, Score, Score, Score>
{
    public bool Matches(in DisplayName first, in Score second, in Score third, in Score fourth, in Score fifth) =>
        second.Value == 1 && third.Value == 2 && fourth.Value == 1 && fifth.Value == 2;
}

public readonly struct ExcludedNamePrefix5(string prefix) : IRelation<DisplayName, Score, Score, Score, Score>
{
    public bool Matches(in DisplayName first, in Score second, in Score third, in Score fourth, in Score fifth) =>
        first.Value.StartsWith(prefix, StringComparison.Ordinal);
}

public readonly struct ScoreSum6(int sum) : IRelation<Score, Score, Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth) =>
        first.Value + second.Value + third.Value + fourth.Value + fifth.Value + sixth.Value == sum;
}

public readonly struct FirstScoreMinimum6(int minimum) : IRelation<Score, Score, Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth) =>
        first.Value >= minimum;
}

public readonly struct ScoreSum7(int sum) : IRelation<Score, Score, Score, Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth, in Score seventh) =>
        first.Value + second.Value + third.Value + fourth.Value + fifth.Value + sixth.Value + seventh.Value == sum;
}

public readonly struct FirstScoreMinimum7(int minimum) : IRelation<Score, Score, Score, Score, Score, Score, Score>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth, in Score seventh) =>
        first.Value >= minimum;
}

public readonly struct AlternatingScores8(int minimumWeight) : IRelation<Score, Score, Score, Score, Score, Score, Score, Weight>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth, in Score seventh, in Weight eighth) =>
        first.Value == 1 && second.Value == 2 && third.Value == 1 && fourth.Value == 2 &&
        fifth.Value == 1 && sixth.Value == 2 && seventh.Value == 1 && eighth.Value >= minimumWeight;
}

public readonly struct ExcludedWeight8(int minimum) : IRelation<Score, Score, Score, Score, Score, Score, Score, Weight>
{
    public bool Matches(in Score first, in Score second, in Score third, in Score fourth, in Score fifth, in Score sixth, in Score seventh, in Weight eighth) =>
        eighth.Value >= minimum;
}
