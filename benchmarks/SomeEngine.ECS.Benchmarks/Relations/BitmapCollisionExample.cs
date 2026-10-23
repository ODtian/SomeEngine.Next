using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Benchmarks.Relations.Bitmap;

public static class BitmapCollisionExample
{
    public static RelationQuery<FixedCandidatePairs, (Entity, Entity)> Candidates(
        World world, QueryHandle members, (Entity, Entity)[] pairs) => new(new FixedCandidatePairs(world, members, pairs));
}

// Business components and rules for the finite-category collision example and paired measurements.
[Flags]
public enum CollisionCategory : ulong
{
    Character = 1,
    Scenery = 2,
    Sensor = 4,
}

public struct CollisionLayer : IComponent
{
    public CollisionCategory Categories;
    public CollisionCategory AllowedCategories;
}

public readonly struct OneWayContacts : IRelation<CollisionLayer, CollisionLayer>
{
    public bool Matches(in CollisionLayer first, in CollisionLayer second) =>
        (first.AllowedCategories & second.Categories) != 0;
}

public readonly struct OneWayContactBitmap : IBitmapRelation<OneWayContacts, CollisionLayer, OneWayPermission>
{
    public CategoryBits Read(in OneWayContacts relation, in CollisionLayer component) =>
        new((ulong)component.Categories, (ulong)component.AllowedCategories);
}

public readonly struct MutualContacts : IRelation<CollisionLayer, CollisionLayer>
{
    public bool Matches(in CollisionLayer first, in CollisionLayer second) =>
        (first.AllowedCategories & second.Categories) != 0 &&
        (second.AllowedCategories & first.Categories) != 0;
}

public readonly struct MutualContactBitmap : IBitmapRelation<MutualContacts, CollisionLayer, MutualPermission>
{
    public CategoryBits Read(in MutualContacts relation, in CollisionLayer component) =>
        new((ulong)component.Categories, (ulong)component.AllowedCategories);
}
