using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class CollisionRelationExampleTests
{
    [Fact]
    public void BodyAndHairRulesReturnTheThirteenExamplePairs()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var a1 = world.Create(new Body { Kind = MotionKind.Dynamic });
        var a2 = world.Create(new Body { Kind = MotionKind.Dynamic });
        var b = world.Create(new Body { Kind = MotionKind.Dynamic });
        var s = world.Create(new Body { Kind = MotionKind.Static });
        var k = world.Create(new Body { Kind = MotionKind.Kinematic });
        var d = world.Create(new Body { Kind = MotionKind.Dynamic });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Within(bodies)
            .Where(default(DefaultContacts))
            .Except(default(SameCharacterHair))
            .Enumerate(ref output);

        Assert.Equal(13, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([
            (a1, b), (a1, s), (a1, k), (a1, d),
            (a2, b), (a2, s), (a2, k), (a2, d),
            (b, s), (b, k), (b, d), (s, d), (k, d)]));
    }

    [Theory]
    [InlineData(MotionKind.Dynamic, MotionKind.Dynamic, true)]
    [InlineData(MotionKind.Dynamic, MotionKind.Static, true)]
    [InlineData(MotionKind.Dynamic, MotionKind.Kinematic, true)]
    [InlineData(MotionKind.Static, MotionKind.Static, false)]
    [InlineData(MotionKind.Static, MotionKind.Kinematic, false)]
    [InlineData(MotionKind.Kinematic, MotionKind.Kinematic, false)]
    public void DefaultContactsMatchTheMotionKinds(MotionKind first, MotionKind second, bool expected)
    {
        using var world = new World();
        world.Create(new Body { Kind = first });
        world.Create(new Body { Kind = second });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());

        Assert.Equal(expected, world.Within(bodies).Where(default(DefaultContacts)).Any());
    }

    [Fact]
    public void BinaryFiltersForwardRefStructOutputAndEarlyStop()
    {
        using var world = new World();
        var first = world.Create(new Body { Kind = MotionKind.Dynamic });
        var second = world.Create(new Body { Kind = MotionKind.Dynamic });
        var third = world.Create(new Body { Kind = MotionKind.Dynamic });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        Span<(Entity, Entity)> result = stackalloc (Entity, Entity)[1];
        var output = new FirstPair(result);

        bool completed = world.Within(bodies)
            .Where(default(DefaultContacts))
            .Except(default(SameCharacterHair))
            .Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Contains(result[0], new[] { (first, second), (first, third), (second, third) });
    }

    private ref struct FirstPair : IRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _result;

        public FirstPair(Span<(Entity, Entity)> result)
        {
            _result = result;
            Count = 0;
        }

        public int Count { get; private set; }

        public bool Add(in (Entity, Entity) pair)
        {
            _result[Count++] = pair;
            return false;
        }
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
