using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Benchmarks.Relations.Bitmap;

public ref struct BitmapPairOutput : IRelationOutput<(Entity, Entity)>
{
    private readonly Span<(Entity, Entity)> _pairs;

    public BitmapPairOutput(Span<(Entity, Entity)> pairs)
    {
        _pairs = pairs;
    }

    public int Count { get; private set; }

    public readonly ReadOnlySpan<(Entity, Entity)> Pairs => _pairs[..Count];

    public bool Add(in (Entity, Entity) pair)
    {
        _pairs[Count++] = pair;
        return true;
    }
}

public static class BitmapCollisionConsumption
{
    public static void CountContacts(ReadOnlySpan<(Entity, Entity)> pairs, Span<int> contactCounts)
    {
        foreach (var pair in pairs)
        {
            contactCounts[pair.Item1.Index]++;
            contactCounts[pair.Item2.Index]++;
        }
    }
}
