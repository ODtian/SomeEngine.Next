using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;

namespace SomeEngine.ECS.Relations.Tests;

// Business types for the collision-query example.
public enum MotionKind
{
    Dynamic,
    Static,
    Kinematic,
}

public struct Body : IComponent
{
    public MotionKind Kind;
}

public struct Hair : IComponent, IIndexedComponent<Entity>
{
    public Entity Character;

    public readonly Entity GetKey() => Character;
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
