using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public enum RelationChangeKind : byte
{
    Added,
    Changed,
    Removed,
}

public readonly struct RelationChange<T>
    where T : struct, IRelation
{
    public readonly RelationChangeKind Kind;
    public readonly Entity Source;
    public readonly Entity Target;
    public readonly Entity OldTarget;
    public readonly T Value;
    public readonly T OldValue;
    public readonly uint Version;

    public RelationChange(
        RelationChangeKind kind,
        Entity source,
        Entity target,
        Entity oldTarget,
        in T value,
        in T oldValue,
        uint version)
    {
        Kind = kind;
        Source = source;
        Target = target;
        OldTarget = oldTarget;
        Value = value;
        OldValue = oldValue;
        Version = version;
    }
}

