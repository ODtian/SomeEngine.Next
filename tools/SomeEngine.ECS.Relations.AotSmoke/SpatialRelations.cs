using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Entities;
using SomeEngine.Physics;
using System.Runtime.CompilerServices;

namespace SomeEngine.Tools.RelationsSmoke;

// Business example: movement, finite categories and a character entity key.
public enum MotionKind { Dynamic, Static, Kinematic }

public struct Body : IComponent
{
    public MotionKind Motion;
    public CollisionBounds Bounds;
    public CategoryBits Categories;
}

public struct Hair : IIndexedComponent<Entity>
{
    public Entity Character;
    public readonly Entity GetKey() => Character;
}

public readonly struct BodyBoundsReader : ICollisionBoundsReader<Body>
{
    public CollisionBounds Read(in Body component) => component.Bounds;
}

public readonly struct DefaultContacts : IRelation<Body, Body>
{
    public static bool IsSymmetric => true;

    public bool Matches(in Body first, in Body second) =>
        first.Motion == MotionKind.Dynamic || second.Motion == MotionKind.Dynamic;
}

public readonly struct SameCharacterHair : IRelation<Hair, Hair>
{
    public bool Matches(in Hair first, in Hair second) => first.Character == second.Character;
}

public readonly struct HairCharacterIndex : IKeyRelation<SameCharacterHair, Hair, Entity>
{
    public bool Matches(in SameCharacterHair relation, in Entity first, in Entity second) => first == second;
}

public readonly struct MutualCategories : IRelation<Body, Body>
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public bool Matches(in Body first, in Body second) =>
        MutualPermission.Matches(in first.Categories, in second.Categories);
}

public readonly struct BodyBitmap : IBitmapRelation<MutualCategories, Body, MutualPermission>
{
    public ulong ReadCategories(in MutualCategories relation, in Body component) => component.Categories.Categories;

    public ulong ReadAllowedCategories(in MutualCategories relation, in Body component) => component.Categories.AllowedCategories;
}
