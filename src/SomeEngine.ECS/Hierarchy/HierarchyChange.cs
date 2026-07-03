using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hierarchy;

public enum HierarchyChangeKind : byte
{
    Added,
    Changed,
    Removed,
    Reordered,
}

public readonly struct HierarchyChange
{
    public readonly HierarchyChangeKind Kind;
    public readonly Entity Child;
    public readonly Entity OldParent;
    public readonly Entity NewParent;
    public readonly int OldIndex;
    public readonly int NewIndex;
    public readonly uint Version;

    public HierarchyChange(
        HierarchyChangeKind kind,
        Entity child,
        Entity oldParent,
        Entity newParent,
        int oldIndex,
        int newIndex,
        uint version)
    {
        Kind = kind;
        Child = child;
        OldParent = oldParent;
        NewParent = newParent;
        OldIndex = oldIndex;
        NewIndex = newIndex;
        Version = version;
    }
}

