using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Benchmarks.Relations;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class KeyRelationTests
{
    [Fact]
    public void KeyBucketsSelectSameCharacterPairsFromParticipatingBodies()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Body());
        var second = world.CreateEntity(new Body());
        var third = world.CreateEntity(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        world.CreateEntity(new Hair { Character = characterA });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var direct = new List<(Entity, Entity)>();
        var indexed = new List<(Entity, Entity)>();
        var directOutput = new PairList(direct);
        var indexedOutput = new PairList(indexed);

        world.Within(bodies).Where(default(SameCharacterHair)).Enumerate(ref directOutput);
        world.Within(bodies).Where(default(SameCharacterHair), default(HairCharacterIndex)).Enumerate(ref indexedOutput);

        Assert.Equal(new[] { (first, second) }, direct);
        Assert.Equal(direct, indexed);
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void KeyExceptKeepsOrdinaryBodiesInComposedRules()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var second = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var third = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var ordinary = world.CreateEntity(new Body { Kind = MotionKind.Static });
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Within(bodies)
            .Except(default(SameCharacterHair), default(HairCharacterIndex))
            .Where(default(DefaultContacts))
            .Enumerate(ref output);

        Assert.Equal(5, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([
            (first, third), (first, ordinary), (second, third), (second, ordinary), (third, ordinary)]));
        Assert.Equal(5, world.Within(bodies).Where(default(DefaultContacts))
            .Except(default(SameCharacterHair), default(HairCharacterIndex)).Count());
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void KeyProductKeepsInputRolesAndSemanticParameters()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Hair { Character = characterA });
        var second = world.CreateEntity(new Hair { Character = characterA });
        var third = world.CreateEntity(new Hair { Character = characterB });
        world.AddTag<LeftSet>(second);
        world.AddTag<LeftSet>(third);
        world.AddTag<RightSet>(first);
        world.AddTag<RightSet>(second);
        world.AddTag<RightSet>(third);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        RelationQuery<KeyPairs<KeyProduct2, SelectedCharacterHair, SelectedCharacterIndex, Hair, Entity>, (Entity, Entity)> selected =
            world.Product(left, right).Where(new SelectedCharacterHair(characterA), default(SelectedCharacterIndex));

        selected.Enumerate(ref output);

        Assert.Equal(2, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(second, first), (second, second)]));
        world.ReleaseQuery(left);
        world.ReleaseQuery(right);
    }

    [Fact]
    public void KeyBetweenOrdersPairsAndDeduplicatesOverlap()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Hair { Character = characterA });
        var second = world.CreateEntity(new Hair { Character = characterA });
        var third = world.CreateEntity(new Hair { Character = characterA });
        var fourth = world.CreateEntity(new Hair { Character = characterB });
        world.AddTag<RightSet>(first);
        world.AddTag<LeftSet>(second);
        world.AddTag<RightSet>(second);
        world.AddTag<LeftSet>(third);
        world.AddTag<RightSet>(third);
        world.AddTag<LeftSet>(fourth);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        RelationQuery<KeyPairs<KeyPairsBetween, SameCharacterHair, HairCharacterIndex, Hair, Entity>, (Entity, Entity)> selected =
            world.Between(left, right).Where(default(SameCharacterHair), default(HairCharacterIndex));

        selected.Enumerate(ref output);

        Assert.Equal(3, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(first, second), (first, third), (second, third)]));
        Assert.Equal(3, world.Between(left, right).Except(default(SameCharacterHair), default(HairCharacterIndex)).Count());
        world.ReleaseQuery(left);
        world.ReleaseQuery(right);
    }

    [Fact]
    public void HandwrittenKeyBucketsReturnTheTypedImplementationRows()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Body());
        var second = world.CreateEntity(new Body());
        var third = world.CreateEntity(new Body());
        var ordinary = world.CreateEntity(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var indexed = new List<(Entity, Entity)>();
        var handwritten = new List<(Entity, Entity)>();
        var indexedOutput = new PairList(indexed);
        var handwrittenOutput = new PairList(handwritten);

        world.Within(bodies).Except(default(SameCharacterHair), default(HairCharacterIndex)).Enumerate(ref indexedOutput);
        HandwrittenKeyPairs<Hair, Entity>.Except(world, bodies, ref handwrittenOutput);

        Assert.Equal(5, handwritten.Count);
        Assert.True(handwritten.ToHashSet().SetEquals([
            (first, third), (first, ordinary), (second, third), (second, ordinary), (third, ordinary)]));
        Assert.True(indexed.ToHashSet().SetEquals(handwritten));
        world.ReleaseQuery(bodies);
    }

    [Fact]
    public void KeyBetweenBindsKeysAfterIdentityOrdering()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Hair { Character = characterA });
        var second = world.CreateEntity(new Hair { Character = characterB });
        world.AddTag<RightSet>(first);
        world.AddTag<LeftSet>(second);
        var left = world.Query(world.QueryDefinition().All<LeftSet>());
        var right = world.Query(world.QueryDefinition().All<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Between(left, right).Where(new FirstCharacterHair(characterA), default(FirstCharacterIndex))
            .Enumerate(ref output);

        Assert.Equal(new[] { (first, second) }, pairs);
        world.ReleaseQuery(left);
        world.ReleaseQuery(right);
    }

    [Fact]
    public void KeyBucketsForwardRefStructOutputAndEarlyStop()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var first = world.CreateEntity(new IndexedWord { Value = "cloth" });
        var second = world.CreateEntity(new IndexedWord { Value = "cloth" });
        var third = world.CreateEntity(new IndexedWord { Value = "cloth" });
        var words = world.Query(world.QueryDefinition().All<IndexedWord>());
        Span<(Entity, Entity)> result = stackalloc (Entity, Entity)[1];
        var output = new FirstPair(result);
        var selected = world.Within(words)
            .Where(new RequiredKey<IndexedWord, string>("cloth"), default(RequiredKeyIndex<IndexedWord, string>))
            .Except(new RequiredKey<IndexedWord, string>("metal"), default(RequiredKeyIndex<IndexedWord, string>));

        bool completed = selected.Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Contains(result[0], new[] { (first, second), (first, third), (second, third) });
        Assert.True(selected.Any());
        world.ReleaseQuery(words);
    }

    [Fact]
    public void KeyPairsFollowReferenceWritesComponentChangesAndEntityDestruction()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var first = world.CreateEntity(new Body());
        var second = world.CreateEntity(new Body());
        var third = world.CreateEntity(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var selected = world.Within(bodies).Where(default(SameCharacterHair), default(HairCharacterIndex));
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        selected.Enumerate(ref output);
        Assert.Equal(new[] { (first, second) }, pairs);

        var writes = world.Query(world.QueryDefinition().ReadWrite<Hair>());
        world.ExecuteQuery(writes, query =>
        {
            foreach (var row in query.Rows)
                if (row.Entity == second)
                    row.ReadWrite<Hair>().Character = characterB;
        });
        world.ReleaseQuery(writes);
        pairs.Clear();
        selected.Enumerate(ref output);
        Assert.Equal(new[] { (second, third) }, pairs);

        world.Remove<Hair>(third);
        pairs.Clear();
        selected.Enumerate(ref output);
        Assert.Empty(pairs);

        world.Add(third, new Hair { Character = characterA });
        pairs.Clear();
        selected.Enumerate(ref output);
        Assert.Equal(new[] { (first, third) }, pairs);

        world.DestroyEntity(first);
        pairs.Clear();
        selected.Enumerate(ref output);
        Assert.Empty(pairs);
        world.ReleaseQuery(bodies);
    }

    private ref struct FirstPair : IRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _result;

        public FirstPair(Span<(Entity, Entity)> result)
        {
            _result = result;
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

public readonly struct SelectedCharacterHair(Entity character) : IRelation<Hair, Hair>
{
    public Entity Character => character;

    public bool Matches(in Hair first, in Hair second) => first.Character == Character && first.Character == second.Character;
}

public readonly struct SelectedCharacterIndex : IKeyRelation<SelectedCharacterHair, Hair, Entity>
{
    public bool Matches(in SelectedCharacterHair relation, in Entity first, in Entity second) =>
        first == relation.Character && first == second;
}

public readonly struct FirstCharacterHair(Entity character) : IRelation<Hair, Hair>
{
    public Entity Character => character;

    public bool Matches(in Hair first, in Hair second) => first.Character == Character;
}

public readonly struct FirstCharacterIndex : IKeyRelation<FirstCharacterHair, Hair, Entity>
{
    public bool Matches(in FirstCharacterHair relation, in Entity first, in Entity second) => first == relation.Character;
}

public struct IndexedWord : IIndexedComponent<string>
{
    public string Value;

    public readonly string GetKey() => Value;
}

public readonly struct RequiredKey<TComponent, TKey>(TKey key) : IRelation<TComponent, TComponent>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    public TKey Key => key;

    public bool Matches(in TComponent first, in TComponent second)
    {
        TKey firstKey = first.GetKey();
        return firstKey.Equals(Key) && firstKey.Equals(second.GetKey());
    }
}

public readonly struct RequiredKeyIndex<TComponent, TKey> : IKeyRelation<RequiredKey<TComponent, TKey>, TComponent, TKey>
    where TComponent : struct, IIndexedComponent<TKey>
    where TKey : notnull, IEquatable<TKey>
{
    public bool Matches(in RequiredKey<TComponent, TKey> relation, in TKey first, in TKey second) =>
        first.Equals(relation.Key) && first.Equals(second);
}
