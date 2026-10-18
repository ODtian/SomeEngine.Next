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
    public static bool Precedes(Entity first, Entity second) =>
        first.Index < second.Index || first.Index == second.Index && first.Generation < second.Generation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Entity, Entity) OrderPair(Entity first, Entity second) =>
        Precedes(first, second) ? (first, second) : (second, first);
}
