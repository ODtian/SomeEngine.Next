using SomeEngine.ECS.Entities;

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

public struct Hair : IComponent
{
    public Entity Character;
}

public readonly struct DefaultContacts : IRelation<Body, Body>
{
    public bool Matches(in Body first, in Body second) =>
        first.Kind == MotionKind.Dynamic || second.Kind == MotionKind.Dynamic;
}

public readonly struct SameCharacterHair : IRelation<Hair, Hair>
{
    public bool Matches(in Hair first, in Hair second) => first.Character == second.Character;
}
