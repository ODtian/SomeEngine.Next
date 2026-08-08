using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public enum RelationAdjacencyRole : byte
{
    Outgoing,
    Incoming,
    Incident,
}

public enum RelationAdjacencyOrderPolicy : byte
{
    Unordered,
    Ordered,
}

public readonly record struct DirectedRelationPlacement(
    int? OutgoingIndex = null,
    int? IncomingIndex = null);

public readonly record struct UndirectedRelationPlacement(
    int? EndpointAIndex = null,
    int? EndpointBIndex = null);

public readonly struct RelationAdjacencyEntry<T>
    where T : struct, IComponent
{
    internal RelationAdjacencyEntry(RelationEdge<T> edge, Entity otherEndpoint)
    {
        Edge = edge;
        OtherEndpoint = otherEndpoint;
    }

    public RelationEdge<T> Edge { get; }

    public Entity OtherEndpoint { get; }
}

/// <summary>
/// Immutable generation snapshot. Keeping the value alive keeps its backing
/// array alive, so later relation publication cannot invalidate its span.
/// </summary>
public readonly struct RelationAdjacencySnapshot<T>
    where T : struct, IComponent
{
    private readonly ReadOnlyMemory<RelationAdjacencyEntry<T>> _entries;

    internal RelationAdjacencySnapshot(
        ReadOnlyMemory<RelationAdjacencyEntry<T>> entries,
        uint generation,
        RelationAdjacencyOrderPolicy policy)
    {
        _entries = entries;
        Generation = generation;
        OrderPolicy = policy;
    }

    public uint Generation { get; }

    public RelationAdjacencyOrderPolicy OrderPolicy { get; }

    public int Count => _entries.Length;

    public ReadOnlySpan<RelationAdjacencyEntry<T>> Entries => _entries.Span;
}

/// <summary>
/// Zero-copy filtered view over one immutable adjacency generation. Materialization is explicit
/// through <see cref="ToArray"/> and therefore stays at a caller-selected boundary.
/// </summary>
public readonly ref struct RelationEdgeQuery<T>
    where T : struct, IComponent
{
    private readonly ReadOnlySpan<RelationAdjacencyEntry<T>> _entries;
    private readonly Entity _otherEndpoint;

    internal RelationEdgeQuery(
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries,
        Entity otherEndpoint)
    {
        _entries = entries;
        _otherEndpoint = otherEndpoint;
    }

    public int Count
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i].OtherEndpoint == _otherEndpoint)
                    count++;
            }

            return count;
        }
    }

    public Enumerator GetEnumerator() => new(_entries, _otherEndpoint);

    public RelationEdge<T>[] ToArray()
    {
        var result = new RelationEdge<T>[Count];
        int offset = 0;
        for (int i = 0; i < _entries.Length; i++)
        {
            if (_entries[i].OtherEndpoint == _otherEndpoint)
                result[offset++] = _entries[i].Edge;
        }

        return result;
    }

    public ref struct Enumerator
    {
        private readonly ReadOnlySpan<RelationAdjacencyEntry<T>> _entries;
        private readonly Entity _otherEndpoint;
        private int _index;
        private RelationEdge<T> _current;

        internal Enumerator(
            ReadOnlySpan<RelationAdjacencyEntry<T>> entries,
            Entity otherEndpoint)
        {
            _entries = entries;
            _otherEndpoint = otherEndpoint;
            _index = 0;
            _current = default;
        }

        public RelationEdge<T> Current => _current;

        public bool MoveNext()
        {
            while (_index < _entries.Length)
            {
                RelationAdjacencyEntry<T> entry = _entries[_index++];
                if (entry.OtherEndpoint != _otherEndpoint)
                    continue;
                _current = entry.Edge;
                return true;
            }

            return false;
        }
    }
}
