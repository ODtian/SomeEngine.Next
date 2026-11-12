using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Comparison;

internal interface IRelationPayload<T> : IComponent, EasyEcs.IComponent, Friflo.Engine.ECS.IComponent where T : struct
{
    static abstract T Create(int ordinal);
    int Ordinal { get; }
    long Value { get; }
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
public readonly struct DirectedParallelPayload(int ordinal) : IRelationPayload<DirectedParallelPayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 0;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static DirectedParallelPayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
public readonly struct DirectedUniquePairPayload(int ordinal) : IRelationPayload<DirectedUniquePairPayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 1;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static DirectedUniquePairPayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueSource)]
public readonly struct DirectedUniqueSourcePayload(int ordinal) : IRelationPayload<DirectedUniqueSourcePayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 2;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static DirectedUniqueSourcePayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
public readonly struct DirectedUniqueTargetPayload(int ordinal) : IRelationPayload<DirectedUniqueTargetPayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 3;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static DirectedUniqueTargetPayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
public readonly struct DirectedOneToOnePayload(int ordinal) : IRelationPayload<DirectedOneToOnePayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 4;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static DirectedOneToOnePayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
public readonly struct UndirectedParallelPayload(int ordinal) : IRelationPayload<UndirectedParallelPayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 5;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static UndirectedParallelPayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair)]
public readonly struct UndirectedUniquePairPayload(int ordinal) : IRelationPayload<UndirectedUniquePairPayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 6;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static UndirectedUniquePairPayload Create(int ordinal) => new(ordinal);
}

[RelationSchema(RelationDirection.Undirected, RelationCardinality.OneToOne)]
public readonly struct UndirectedOneToOnePayload(int ordinal) : IRelationPayload<UndirectedOneToOnePayload>
{
    private readonly int _ordinal = ordinal;
    public readonly int Kind = 9;
    private readonly long _value = ordinal * 3L + 7;
    public int Ordinal => _ordinal;
    public long Value => _value;
    public static UndirectedOneToOnePayload Create(int ordinal) => new(ordinal);
}
