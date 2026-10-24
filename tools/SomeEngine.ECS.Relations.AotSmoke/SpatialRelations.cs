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

// Business example: a custom permission keeps its concrete Matches through the default result callback.
public readonly struct ForwardedMutualPermission : ICategoryPermission
{
    public static bool Matches(in CategoryBits first, in CategoryBits second) =>
        MutualPermission.Matches(in first, in second);

    public static bool Matches<TRelation, TComponent, TReader>(in TRelation relation, ref TReader reader,
        in TComponent first, in TComponent second)
        where TRelation : struct
        where TComponent : struct
        where TReader : struct, ICategoryBitsReader<TRelation, TComponent> =>
        MutualPermission.Matches<TRelation, TComponent, TReader>(in relation, ref reader, in first, in second);
}

public readonly struct ForwardedBodyBitmap : IBitmapRelation<MutualCategories, Body, ForwardedMutualPermission>
{
    public ulong ReadCategories(in MutualCategories relation, in Body component) =>
        default(BodyBitmap).ReadCategories(in relation, in component);

    public ulong ReadAllowedCategories(in MutualCategories relation, in Body component) =>
        default(BodyBitmap).ReadAllowedCategories(in relation, in component);
}
