using SomeEngine.ECS.Benchmarks.Relations.Bitmap;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Relations.Tests;

public sealed class BitmapRelationTests
{
    [Fact]
    public void CustomPermissionsKeepTheirMatchAlgorithmThroughResultCallbacks()
    {
        using var world = new World();
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Character | CollisionCategory.Scenery });
        var second = world.CreateEntity(new CollisionLayer { Categories = CollisionCategory.Scenery });
        world.CreateEntity(new CollisionLayer { Categories = CollisionCategory.Scenery | CollisionCategory.Sensor });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        var rows = new List<(Entity, Entity)>();
        var output = new PairList(rows);

        world.Within(layers).Where(default(WholeCategoryContacts), default(WholeCategoryBitmap)).Enumerate(ref output);

        Assert.Equal([(first, second)], rows);
        Assert.Equal(2, world.Within(layers).Except(default(WholeCategoryContacts), default(WholeCategoryBitmap)).Count());
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void OneWayBitmapUsesFirstMaskAndSecondCategories()
    {
        using var world = new World();
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Scenery });
        var second = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Sensor });
        var third = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Sensor });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        var direct = new List<(Entity, Entity)>();
        var bitmap = new List<(Entity, Entity)>();
        var directOutput = new PairList(direct);
        var bitmapOutput = new PairList(bitmap);

        world.Product(layers, layers).Where(default(OneWayContacts)).Enumerate(ref directOutput);
        world.Product(layers, layers).Where(default(OneWayContacts), default(OneWayContactBitmap)).Enumerate(ref bitmapOutput);

        Assert.True(direct.ToHashSet().SetEquals([(first, second), (second, third)]));
        Assert.Equal(2, bitmap.Count);
        Assert.True(bitmap.ToHashSet().SetEquals(direct));
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void MutualBitmapRequiresEachSideToPermitTheOther()
    {
        using var world = new World();
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Scenery | CollisionCategory.Sensor });
        var second = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Character });
        var third = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Sensor, AllowedCategories = CollisionCategory.Scenery });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        var direct = new List<(Entity, Entity)>();
        var bitmap = new List<(Entity, Entity)>();
        var directOutput = new PairList(direct);
        var bitmapOutput = new PairList(bitmap);

        world.Product(layers, layers).Where(default(MutualContacts)).Enumerate(ref directOutput);
        world.Product(layers, layers).Where(default(MutualContacts), default(MutualContactBitmap)).Enumerate(ref bitmapOutput);

        Assert.True(direct.ToHashSet().SetEquals([(first, second), (second, first)]));
        Assert.Equal(2, bitmap.Count);
        Assert.True(bitmap.ToHashSet().SetEquals(direct));
        Assert.Equal(4, world.Product(layers, layers).Where(default(OneWayContacts), default(OneWayContactBitmap)).Count());
        Assert.Equal(7, world.Product(layers, layers).Except(default(MutualContacts), default(MutualContactBitmap)).Count());
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void OneWayFixedCandidatesShareRowsAndContactCountsAcrossImplementations()
    {
        using var world = new World();
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Scenery | CollisionCategory.Sensor });
        var second = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Character });
        var third = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Sensor, AllowedCategories = CollisionCategory.Scenery });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        (Entity, Entity)[] candidates = [(first, second), (second, first), (first, third), (third, second), (third, first)];
        var direct = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
        var bitmap = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
        var handwritten = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);

        BitmapCollisionExample.Candidates(world, layers, candidates).Where(default(OneWayContacts)).Enumerate(ref direct);
        BitmapCollisionExample.Candidates(world, layers, candidates)
            .Where(default(OneWayContacts), default(OneWayContactBitmap)).Enumerate(ref bitmap);
        HandwrittenBitmapCollision.OneWay(world, layers, candidates, ref handwritten);

        (Entity, Entity)[] expected = [(first, second), (second, first), (first, third), (third, second)];
        Assert.Equal(expected, direct.Pairs.ToArray());
        Assert.Equal(expected, bitmap.Pairs.ToArray());
        Assert.Equal(expected, handwritten.Pairs.ToArray());
        Span<int> contactCounts = stackalloc int[third.Index + 1];
        BitmapCollisionConsumption.CountContacts(bitmap.Pairs, contactCounts);
        Assert.Equal(3, contactCounts[first.Index]);
        Assert.Equal(3, contactCounts[second.Index]);
        Assert.Equal(2, contactCounts[third.Index]);
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void MutualFixedCandidatesShareRowsAndContactCountsAcrossImplementations()
    {
        using var world = new World();
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Scenery | CollisionCategory.Sensor });
        var second = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Character });
        var third = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Sensor, AllowedCategories = CollisionCategory.Scenery });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        (Entity, Entity)[] candidates = [(first, second), (second, first), (first, third), (third, second), (third, first)];
        var direct = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
        var bitmap = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);
        var handwritten = new BitmapPairOutput(new (Entity, Entity)[candidates.Length]);

        BitmapCollisionExample.Candidates(world, layers, candidates).Where(default(MutualContacts)).Enumerate(ref direct);
        BitmapCollisionExample.Candidates(world, layers, candidates)
            .Where(default(MutualContacts), default(MutualContactBitmap)).Enumerate(ref bitmap);
        HandwrittenBitmapCollision.Mutual(world, layers, candidates, ref handwritten);

        (Entity, Entity)[] expected = [(first, second), (second, first)];
        Assert.Equal(expected, direct.Pairs.ToArray());
        Assert.Equal(expected, bitmap.Pairs.ToArray());
        Assert.Equal(expected, handwritten.Pairs.ToArray());
        Span<int> contactCounts = stackalloc int[third.Index + 1];
        BitmapCollisionConsumption.CountContacts(bitmap.Pairs, contactCounts);
        Assert.Equal(2, contactCounts[first.Index]);
        Assert.Equal(2, contactCounts[second.Index]);
        Assert.Equal(0, contactCounts[third.Index]);
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void BitmapFiltersForwardCountExistenceAndRefStructEarlyStop()
    {
        using var jobs = new Scheduler(new JobRuntimeConfig { WorkerCount = 0, SafetyMode = JobSafetyMode.Checked });
        using var world = WorldJobs.CreateWorld(scheduler: jobs);
        var first = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Scenery | CollisionCategory.Sensor });
        var second = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Character });
        var third = world.CreateEntity(new CollisionLayer
            { Categories = CollisionCategory.Sensor, AllowedCategories = CollisionCategory.Scenery });
        var layers = world.Query(world.QueryDefinition().All<CollisionLayer>());
        (Entity, Entity)[] candidates = [(first, second), (second, first), (first, third), (third, second)];
        var selected = BitmapCollisionExample.Candidates(world, layers, candidates)
            .Where(default(OneWayContacts), default(OneWayContactBitmap))
            .Except(default(MutualContacts), default(MutualContactBitmap));
        Span<(Entity, Entity)> result = stackalloc (Entity, Entity)[1];
        var output = new FirstPair(result);

        bool completed = selected.Enumerate(ref output);

        Assert.False(completed);
        Assert.Equal(1, output.Count);
        Assert.Equal((first, third), result[0]);
        Assert.Equal(2, selected.Count());
        Assert.True(selected.Any());
        world.ReleaseQuery(layers);
    }

    [Fact]
    public void BitmapPermissionsComposeWithMotionAndHairRules()
    {
        using var world = new World();
        var characterA = world.CreateEntity();
        var characterB = world.CreateEntity();
        var a1 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var a2 = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var b = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        var s = world.CreateEntity(new Body { Kind = MotionKind.Static });
        var k = world.CreateEntity(new Body { Kind = MotionKind.Kinematic });
        var d = world.CreateEntity(new Body { Kind = MotionKind.Dynamic });
        world.Add(a1, new Hair { Character = characterA });
        world.Add(a2, new Hair { Character = characterA });
        world.Add(b, new Hair { Character = characterB });
        var dynamicLayer = new CollisionLayer
            { Categories = CollisionCategory.Character, AllowedCategories = CollisionCategory.Character | CollisionCategory.Scenery };
        world.Add(a1, dynamicLayer);
        world.Add(a2, dynamicLayer);
        world.Add(b, dynamicLayer);
        world.Add(d, dynamicLayer);
        world.Add(s, new CollisionLayer { Categories = CollisionCategory.Scenery, AllowedCategories = CollisionCategory.Character });
        world.Add(k, new CollisionLayer { Categories = CollisionCategory.Sensor, AllowedCategories = CollisionCategory.Character });
        var bodies = world.Query(world.QueryDefinition().All<Body>());
        var source = world.Within(bodies).Where(default(DefaultContacts))
            .Except(default(SameCharacterHair), default(HairCharacterIndex));
        var oneWayDirect = new BitmapPairOutput(new (Entity, Entity)[15]);
        var oneWayBitmap = new BitmapPairOutput(new (Entity, Entity)[15]);
        var mutualDirect = new BitmapPairOutput(new (Entity, Entity)[15]);
        var mutualBitmap = new BitmapPairOutput(new (Entity, Entity)[15]);

        source.Where(default(OneWayContacts)).Enumerate(ref oneWayDirect);
        source.Where(default(OneWayContacts), default(OneWayContactBitmap)).Enumerate(ref oneWayBitmap);
        source.Where(default(MutualContacts)).Enumerate(ref mutualDirect);
        source.Where(default(MutualContacts), default(MutualContactBitmap)).Enumerate(ref mutualBitmap);

        (Entity, Entity)[] expectedOneWay = [
            (a1, b), (a1, s), (a1, d), (a2, b), (a2, s), (a2, d), (b, s), (b, d), (s, d), (k, d)];
        (Entity, Entity)[] expectedMutual = [
            (a1, b), (a1, s), (a1, d), (a2, b), (a2, s), (a2, d), (b, s), (b, d), (s, d)];
        Assert.Equal(10, oneWayDirect.Count);
        Assert.True(oneWayDirect.Pairs.ToArray().ToHashSet().SetEquals(expectedOneWay));
        Assert.Equal(10, oneWayBitmap.Count);
        Assert.True(oneWayBitmap.Pairs.ToArray().ToHashSet().SetEquals(expectedOneWay));
        Assert.Equal(9, mutualDirect.Count);
        Assert.True(mutualDirect.Pairs.ToArray().ToHashSet().SetEquals(expectedMutual));
        Assert.Equal(9, mutualBitmap.Count);
        Assert.True(mutualBitmap.Pairs.ToArray().ToHashSet().SetEquals(expectedMutual));
        world.ReleaseQuery(bodies);
    }

    private ref struct FirstPair : IRelationOutput<(Entity, Entity)>
    {
        private readonly Span<(Entity, Entity)> _result;

        public FirstPair(Span<(Entity, Entity)> result) => _result = result;

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

// Business example for a custom permission that requires the complete target category set.
public readonly struct WholeCategoryContacts : IRelation<CollisionLayer, CollisionLayer>
{
    public bool Matches(in CollisionLayer first, in CollisionLayer second) =>
        second.Categories != 0 && (first.AllowedCategories & second.Categories) == second.Categories;
}

public readonly struct WholeCategoryBitmap : IBitmapRelation<WholeCategoryContacts, CollisionLayer, WholeCategoryPermission>
{
    public ulong ReadCategories(in WholeCategoryContacts relation, in CollisionLayer component) => (ulong)component.Categories;
    public ulong ReadAllowedCategories(in WholeCategoryContacts relation, in CollisionLayer component) => (ulong)component.AllowedCategories;
}

public readonly struct WholeCategoryPermission : ICategoryPermission
{
    public static bool RequiresBothDirections => false;

    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        second.Categories != 0 && (first.AllowedCategories & second.Categories) == second.Categories;

    public static bool Matches<TRelation, TComponent, TReader>(in TRelation relation, ref TReader reader,
        in TComponent first, in TComponent second)
        where TRelation : struct
        where TComponent : struct
        where TReader : struct, ICategoryBitsReader<TRelation, TComponent>
    {
        ulong categories = reader.ReadCategories(in relation, in second);
        return categories != 0 && (reader.ReadAllowedCategories(in relation, in first) & categories) == categories;
    }
}
