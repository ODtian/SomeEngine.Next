using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS.Relations;

public readonly record struct RelationEdge<T>(Entity Entity) where T : struct;

public readonly record struct RelationEndpoints<T>(Entity Source, Entity Target)
    : IAddedComponent, IRemovedComponent, IControlledComponent, IComponentInitialization<RelationEndpoints<T>>, IEntityReferenceValue<RelationEndpoints<T>>,
    IEntityReferenceOperations<RelationEndpoints<T>> where T : struct
{
    static void IEntityReferenceValue<RelationEndpoints<T>>.Remap(ref RelationEndpoints<T> value, in EntityMap entities)
        => value = new(entities.Map(value.Source), entities.Map(value.Target));
    void IEntityReferenceOperations<RelationEndpoints<T>>.Remap(ref RelationEndpoints<T> value, in EntityMap entities)
        => value = new(entities.Map(value.Source), entities.Map(value.Target));
    void IComponentInitialization<RelationEndpoints<T>>.ValidateCreate(World world, in RelationEndpoints<T> value)
        => Relations<T>.Data(world).ValidateCreate(value);
    void IComponentInitialization<RelationEndpoints<T>>.Initialize(World world, Entity entity)
        => Relations<T>.Data(world).InitializeEdge(entity);

}

public readonly record struct RelationEntry(Entity Other, Entity Edge);
public readonly record struct RelationRetarget<T>(RelationEdge<T> Edge, Entity Source, Entity Target) where T : struct;

public readonly ref struct RelationRead
{
    private readonly World _world;
    public ReadOnlySpan<RelationEntry> Entries { get; }
    public int Count => Entries.Length;
    internal RelationRead(World world, ReadOnlySpan<RelationEntry> entries) { _world = world; Entries = entries; }
    public ReadOnlySpan<RelationEntry>.Enumerator GetEnumerator() => Entries.GetEnumerator();
    public void Dispose() => _world.EndValueRead();
}

public readonly ref struct IncidentRelationsRead
{
    private readonly World _world;
    private readonly ReadOnlySpan<RelationEntry> _first, _second;
    public int Count => _first.Length + _second.Length;
    internal IncidentRelationsRead(World world, ReadOnlySpan<RelationEntry> first, ReadOnlySpan<RelationEntry> second)
    {
        _world = world;
        _first = first;
        _second = second;
    }
    public IncidentRelationsEnumerator GetEnumerator() => new(_first, _second);
    public void Dispose() => _world.EndValueRead();
}

public ref struct IncidentRelationsEnumerator
{
    private readonly ReadOnlySpan<RelationEntry> _first, _second;
    private int _index;
    internal IncidentRelationsEnumerator(ReadOnlySpan<RelationEntry> first, ReadOnlySpan<RelationEntry> second)
    {
        _first = first;
        _second = second;
        _index = -1;
    }
    public readonly RelationEntry Current => _index < _first.Length ? _first[_index] : _second[_index - _first.Length];
    public bool MoveNext() => ++_index < _first.Length + _second.Length;
}

internal struct AdjacencyHead
{
    internal RelationEntry[]? Items;
    internal int CountFlags, Generation;
    internal readonly int Count => CountFlags & int.MaxValue;
    internal readonly bool IsOrdered => CountFlags < 0;
    internal readonly ReadOnlySpan<RelationEntry> Entries => Items.AsSpan(0, Count);
    internal void SetCount(int count) => CountFlags = count | (CountFlags & int.MinValue);
    internal static RelationEntry[] Rent(int count) => count <= 8
        ? new RelationEntry[System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(count, 1))]
        : System.Buffers.ArrayPool<RelationEntry>.Shared.Rent(count);
    internal static void Return(RelationEntry[] items)
    {
        if (items.Length >= 16) System.Buffers.ArrayPool<RelationEntry>.Shared.Return(items);
    }
}

internal struct AppliedEdge
{
    internal Entity Source, Target;
    internal int SourcePlace, TargetPlace, Generation;
}

internal struct EndpointClaim
{
    internal int Generation;
    internal Entity Edge;
}

internal readonly record struct EndpointPair(Entity Source, Entity Target)
{
    internal static EndpointPair From(Entity source, Entity target, bool directed)
        => directed || source.Id < target.Id || source.Id == target.Id && source.Generation <= target.Generation
            ? new(source, target) : new(target, source);
}
