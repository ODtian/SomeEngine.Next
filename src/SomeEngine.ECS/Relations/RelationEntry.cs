using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public readonly struct RelationEntry<T> where T : struct, IRelation
{
    public readonly Entity Target;
    public readonly T Value;

    public RelationEntry(Entity target, in T value)
    {
        Target = target;
        Value = value;
    }
}

