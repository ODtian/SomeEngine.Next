using SomeEngine.ECS.Entities;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Relations;

/// <summary>Defines the entity identities and input positions emitted from key groups.</summary>
public interface IKeyPairing
{
    static abstract int QueryCount { get; }

    static abstract bool ReordersPairs { get; }

    static virtual bool ReadsFromSingleQuery => false;

    static abstract bool TryPair(in RelationQueryViews queries, Entity first, Entity second, out (Entity, Entity) pair);
}

public readonly struct KeyPairsWithin : IKeyPairing
{
    public static int QueryCount => 1;

    public static bool ReordersPairs => false;

    public static bool ReadsFromSingleQuery => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPair(in RelationQueryViews queries, Entity first, Entity second, out (Entity, Entity) pair)
    {
        pair = (first, second);
        return EntityOrder.Compare(first, second) < 0;
    }
}

public readonly struct KeyProduct2 : IKeyPairing
{
    public static int QueryCount => 2;

    public static bool ReordersPairs => false;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPair(in RelationQueryViews queries, Entity first, Entity second, out (Entity, Entity) pair)
    {
        pair = (first, second);
        return true;
    }
}

public readonly struct KeyPairsBetween : IKeyPairing
{
    public static int QueryCount => 2;

    public static bool ReordersPairs => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryPair(in RelationQueryViews queries, Entity first, Entity second, out (Entity, Entity) pair)
    {
        int order = EntityOrder.Compare(first, second);
        pair = order < 0 ? (first, second) : (second, first);
        return order != 0 && (order < 0 || !queries.First.TryGetRow(second, out _) ||
            !queries.Second.TryGetRow(first, out _));
    }
}
