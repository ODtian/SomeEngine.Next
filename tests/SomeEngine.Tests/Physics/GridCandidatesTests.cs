using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Spatial;

namespace SomeEngine.Physics.Tests;

public sealed class GridCandidatesTests
{
    [Fact]
    public void UniformGridWithinKeepsEachOverlapOnceAndFollowsReplacedBounds()
    {
        using var world = new World();
        Entity first = world.Create(Box(0));
        Entity second = world.Create(Box(.5f));
        Entity third = world.Create(Box(3));
        Query members = world.Query<SpatialBounds>();
        var grid = SpatialQueries.CreateUniformGrid(.4f);
        var candidates = grid.Candidates(world.Within(members));
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        candidates.Enumerate(ref output);
        Assert.Equal([(first, second)], pairs);

        world.Set(third, Box(.75f));
        pairs.Clear();
        candidates.Enumerate(ref output);
        Assert.Equal(3, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(first, second), (first, third), (second, third)]));

        world.Destroy(second);
        pairs.Clear();
        candidates.Enumerate(ref output);
        Assert.Equal([(first, third)], pairs);
    }

    private static SpatialBounds Box(float x) => new(new Vector3(x, 0, 0), new Vector3(x + 1, 1, 1));

    private readonly struct PairList(List<(Entity, Entity)> pairs) : IRelationOutput<(Entity, Entity)>
    {
        public bool Add(in (Entity, Entity) pair) { pairs.Add(pair); return true; }
    }
}
