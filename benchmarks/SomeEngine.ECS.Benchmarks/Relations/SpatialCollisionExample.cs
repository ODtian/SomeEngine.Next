using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Physics;

namespace SomeEngine.ECS.Benchmarks.Relations.Spatial;

// Business components and rules for the spatial collision example and paired measurements.
public enum MotionKind
{
    Dynamic,
    Static,
    Kinematic,
}

public struct Body : IComponent
{
    public MotionKind Kind;
    public CollisionBounds Bounds;
    public CategoryBits CollisionCategories;
}

public struct Hair : IIndexedComponent<Entity>
{
    public Entity Character;

    public readonly Entity GetKey() => Character;
}

public struct ExcludedBody : ITag;

public readonly struct BodyBoundsReader : ICollisionBoundsReader<Body>
{
    public CollisionBounds Read(in Body component) => component.Bounds;
}

public readonly struct DefaultContacts : IRelation<Body, Body>
{
    public static bool IsSymmetric => true;

    public bool Matches(in Body first, in Body second) =>
        first.Kind == MotionKind.Dynamic || second.Kind == MotionKind.Dynamic;
}

public readonly struct SameCharacterHair : IRelation<Hair, Hair>
{
    public bool Matches(in Hair first, in Hair second) => first.Character == second.Character;
}

public readonly struct HairCharacterIndex : IKeyRelation<SameCharacterHair, Hair, Entity>
{
    public bool Matches(in SameCharacterHair relation, in Entity first, in Entity second) => first == second;
}

public readonly struct OneWayCategories : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second) =>
        (first.CollisionCategories.AllowedCategories & second.CollisionCategories.Categories) != 0;
}

public readonly struct OneWayCategoryBitmap : IBitmapRelation<OneWayCategories, Body, OneWayPermission>
{
    public ulong ReadCategories(in OneWayCategories relation, in Body component) => component.CollisionCategories.Categories;

    public ulong ReadAllowedCategories(in OneWayCategories relation, in Body component) =>
        component.CollisionCategories.AllowedCategories;
}

public readonly struct MutualCategories : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second) =>
        (first.CollisionCategories.AllowedCategories & second.CollisionCategories.Categories) != 0 &&
        (second.CollisionCategories.AllowedCategories & first.CollisionCategories.Categories) != 0;
}

public readonly struct MutualCategoryBitmap : IBitmapRelation<MutualCategories, Body, MutualPermission>
{
    public ulong ReadCategories(in MutualCategories relation, in Body component) => component.CollisionCategories.Categories;

    public ulong ReadAllowedCategories(in MutualCategories relation, in Body component) =>
        component.CollisionCategories.AllowedCategories;
}
