using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static class EntityOrder
{
    public static int Compare(Entity first, Entity second)
    {
        int index = first.Index.CompareTo(second.Index);
        return index != 0 ? index : first.Generation.CompareTo(second.Generation);
    }

    public static (Entity, Entity) OrderPair(Entity first, Entity second) =>
        Compare(first, second) < 0 ? (first, second) : (second, first);
}
