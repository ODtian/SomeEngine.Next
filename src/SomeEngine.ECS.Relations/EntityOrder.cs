using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public static class EntityOrder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(Entity first, Entity second)
    {
        int index = first.Index.CompareTo(second.Index);
        return index != 0 ? index : first.Generation.CompareTo(second.Generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Entity, Entity) OrderPair(Entity first, Entity second) =>
        Compare(first, second) < 0 ? (first, second) : (second, first);
}
