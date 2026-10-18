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
}

public struct Hair : IComponent
{
    public Entity Character;
}

public struct ExcludedBody : ITag;

public readonly struct BodyBoundsReader : ICollisionBoundsReader<Body>
{
    public CollisionBounds Read(in Body component) => component.Bounds;
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
