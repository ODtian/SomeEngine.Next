using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Benchmarks.Relations;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class KeyRelationTests
{
    [Fact]
    public void KeyBucketsSelectSameCharacterPairsFromParticipatingBodies()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Body());
        var second = world.Create(new Body());
        var third = world.Create(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        world.Create(new Hair { Character = characterA });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var direct = new List<(Entity, Entity)>();
        var indexed = new List<(Entity, Entity)>();
        var directOutput = new PairList(direct);
        var indexedOutput = new PairList(indexed);

        world.Within(bodies).Where(default(SameCharacterHair)).Enumerate(ref directOutput);
        world.Within(bodies).Where(default(SameCharacterHair), default(HairCharacterIndex)).Enumerate(ref indexedOutput);

        Assert.Equal(new[] { (first, second) }, direct);
        Assert.Equal(direct, indexed);
    }

    [Fact]
    public void KeyExceptKeepsOrdinaryBodiesInComposedRules()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Body { Kind = MotionKind.Dynamic });
        var second = world.Create(new Body { Kind = MotionKind.Dynamic });
        var third = world.Create(new Body { Kind = MotionKind.Dynamic });
        var ordinary = world.Create(new Body { Kind = MotionKind.Static });
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
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
    }

    [Fact]
    public void KeyProductKeepsInputRolesAndSemanticParameters()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Hair { Character = characterA });
        var second = world.Create(new Hair { Character = characterA });
        var third = world.Create(new Hair { Character = characterB });
        world.Add(second, new LeftSet());
        world.Add(third, new LeftSet());
        world.Add(first, new RightSet());
        world.Add(second, new RightSet());
        world.Add(third, new RightSet());
        var left = world.Query(new QueryDescription().WithStored<LeftSet>());
        var right = world.Query(new QueryDescription().WithStored<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        RelationQuery<KeyPairs<KeyProduct2, SelectedCharacterHair, SelectedCharacterIndex, Hair, Entity>, (Entity, Entity)> selected =
            world.Product(left, right).Where(new SelectedCharacterHair(characterA), default(SelectedCharacterIndex));

        selected.Enumerate(ref output);

        Assert.Equal(2, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(second, first), (second, second)]));
    }

    [Fact]
    public void KeyBetweenOrdersPairsAndDeduplicatesOverlap()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Hair { Character = characterA });
        var second = world.Create(new Hair { Character = characterA });
        var third = world.Create(new Hair { Character = characterA });
        var fourth = world.Create(new Hair { Character = characterB });
        world.Add(first, new RightSet());
        world.Add(second, new LeftSet());
        world.Add(second, new RightSet());
        world.Add(third, new LeftSet());
        world.Add(third, new RightSet());
        world.Add(fourth, new LeftSet());
        var left = world.Query(new QueryDescription().WithStored<LeftSet>());
        var right = world.Query(new QueryDescription().WithStored<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        RelationQuery<KeyPairs<KeyPairsBetween, SameCharacterHair, HairCharacterIndex, Hair, Entity>, (Entity, Entity)> selected =
            world.Between(left, right).Where(default(SameCharacterHair), default(HairCharacterIndex));

        selected.Enumerate(ref output);

        Assert.Equal(3, pairs.Count);
        Assert.True(pairs.ToHashSet().SetEquals([(first, second), (first, third), (second, third)]));
        Assert.Equal(3, world.Between(left, right).Except(default(SameCharacterHair), default(HairCharacterIndex)).Count());
    }

    [Fact]
    public void HandwrittenKeyBucketsReturnTheTypedImplementationRows()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Body());
        var second = world.Create(new Body());
        var third = world.Create(new Body());
        var ordinary = world.Create(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
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
    }

    [Fact]
    public void KeyBetweenBindsKeysAfterIdentityOrdering()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Hair { Character = characterA });
        var second = world.Create(new Hair { Character = characterB });
        world.Add(first, new RightSet());
        world.Add(second, new LeftSet());
        var left = world.Query(new QueryDescription().WithStored<LeftSet>());
        var right = world.Query(new QueryDescription().WithStored<RightSet>());
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);

        world.Between(left, right).Where(new FirstCharacterHair(characterA), default(FirstCharacterIndex))
            .Enumerate(ref output);

        Assert.Equal(new[] { (first, second) }, pairs);
    }

    [Fact]
    public void KeyBucketsForwardRefStructOutputAndEarlyStop()
    {
        using var jobs = new Scheduler(new JobConfiguration { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = new World(execution: new WorldJobs(jobs));
        var first = world.Create(new IndexedWord { Value = "cloth" });
        var second = world.Create(new IndexedWord { Value = "cloth" });
        var third = world.Create(new IndexedWord { Value = "cloth" });
        var words = world.Query(new QueryDescription().WithStored<IndexedWord>());
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
    }

    [Fact]
    public void KeyPairsFollowIndexedWritesComponentChangesAndEntityDestruction()
    {
        using var world = new World();
        var characterA = world.Create();
        var characterB = world.Create();
        var first = world.Create(new Body());
        var second = world.Create(new Body());
        var third = world.Create(new Body());
        world.Add(first, new Hair { Character = characterA });
        world.Add(second, new Hair { Character = characterA });
        world.Add(third, new Hair { Character = characterB });
        var bodies = world.Query(new QueryDescription().WithStored<Body>());
        var selected = world.Within(bodies).Where(default(SameCharacterHair), default(HairCharacterIndex));
        var pairs = new List<(Entity, Entity)>();
        var output = new PairList(pairs);
        selected.Enumerate(ref output);
        Assert.Equal(new[] { (first, second) }, pairs);

        world.Set(second, new Hair { Character = characterB });
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

        world.Destroy(first);
        pairs.Clear();
        selected.Enumerate(ref output);
        Assert.Empty(pairs);
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
