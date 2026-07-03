namespace SomeEngine.ECS;

public partial class World
{
    public ReadOnlySpan<Hierarchy.HierarchyChange> HierarchyChanges(uint lastVersion)
    {
        return _hierarchy.ReadChanges(lastVersion);
    }
}

