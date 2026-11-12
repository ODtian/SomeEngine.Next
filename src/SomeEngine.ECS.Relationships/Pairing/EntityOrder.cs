using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

public static class EntityOrder
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Compare(Entity first, Entity second)
    {
        int index = first.Id.CompareTo(second.Id);
        return index != 0 ? index : first.Generation.CompareTo(second.Generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Precedes(Entity first, Entity second) =>
        first.Id < second.Id || first.Id == second.Id && first.Generation < second.Generation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (Entity, Entity) OrderPair(Entity first, Entity second) =>
        Precedes(first, second) ? (first, second) : (second, first);
}
