using System.Numerics;
using RouteEdge = SomeEngine.Navigation.SceneSearchRun.RouteEdge;
using SceneNode = SomeEngine.Navigation.SceneSearchRun.SceneNode;

namespace SomeEngine.Navigation;

internal sealed class DStarLiteWorkspace
{
    private readonly Dictionary<NodeIdentity, int> _indices = [];
    private readonly List<Vertex> _vertices = [];
    private readonly SortedSet<QueueEntry> _queue = [];
    private readonly Queue<int> _changedVertices = [];
    private readonly float _minimumAreaCost;
    private Vector3 _startPosition;
    private int _start = -1, _goal = -1;
    internal float KeyModifier { get; private set; }
    internal int ReusedVertexCount { get; private set; }
    internal int KnownVertexCount => _vertices.Count;
    internal long ExpandedVertexCount { get; private set; }
    internal float Cost => _vertices[_start].Rhs.Cost;

    internal DStarLiteWorkspace(Vector3 start, NavigationPolicy policy)
    {
        _startPosition = start;
        _minimumAreaCost = policy.DefaultAreaCost;
        foreach (NavigationAreaCost area in policy.AreaCosts.Span) _minimumAreaCost = MathF.Min(_minimumAreaCost, area.Multiplier);
    }

    internal void MoveStart(Vector3 position)
    {
        KeyModifier += Heuristic(_startPosition, position);
        _startPosition = position;
        if (_start >= 0) _vertices[_start].Position = position;
    }

    internal void ApplyGraph(IReadOnlyList<SceneNode> nodes, IReadOnlyList<List<RouteEdge>> edges,
        int start, int goal, NavigationSceneContent content)
    {
        var mapping = new int[nodes.Count];
        var active = new HashSet<int>();
        ReusedVertexCount = 0;
        for (int index = 0; index < nodes.Count; index++)
        {
            SceneNode node = nodes[index];
            NodeIdentity identity;
            if (index == start) identity = new(1, 0, default, 0, default, default, default);
            else if (index == goal) identity = new(2, 0, default, 0, default, default, default);
            else
            {
                NavigationRegionEntrance entrance = content.GetRegion(node.Region).Graph.Entrances.Span[node.Entrance];
                identity = new(0, node.Region, node.Tile, entrance.Port, node.Position, entrance.Left, entrance.Right);
            }
            if (!_indices.TryGetValue(identity, out int retained))
            {
                retained = _vertices.Count;
                _indices.Add(identity, retained);
                _vertices.Add(new());
            }
            else if (_vertices[retained].Active) ReusedVertexCount++;
            mapping[index] = retained;
            _vertices[retained].Position = node.Position;
            active.Add(retained);
        }
        _start = mapping[start]; _goal = mapping[goal];
        _vertices[_start].Position = _startPosition;
        var outgoing = new List<Arc>[_vertices.Count];
        for (int index = 0; index < outgoing.Length; index++) outgoing[index] = [];
        for (int from = 0; from < nodes.Count; from++)
            foreach (RouteEdge edge in edges[from]) outgoing[mapping[from]].Add(new(mapping[edge.To], edge));
        var changed = new List<int>();
        for (int index = 0; index < _vertices.Count; index++)
        {
            Vertex vertex = _vertices[index];
            var next = outgoing[index];
            next.Sort(CompareArc);
            if (!SameEdges(vertex.Successors, next)) changed.Add(index);
            vertex.Active = active.Contains(index);
            vertex.Successors = next;
            vertex.Predecessors.Clear();
        }
        for (int from = 0; from < _vertices.Count; from++)
            foreach (Arc edge in _vertices[from].Successors) _vertices[edge.To].Predecessors.Add(from);
        _vertices[_goal].Rhs = Distance.Zero;
        int[] pending = _changedVertices.Concat(changed).Append(_goal).Distinct().ToArray();
        _changedVertices.Clear();
        foreach (int vertex in pending) _changedVertices.Enqueue(vertex);
    }

    internal NavigationSearchStatus Advance()
    {
        if (_changedVertices.TryDequeue(out int changed))
        {
            UpdateVertex(changed);
            return NavigationSearchStatus.Searching;
        }
        Vertex start = _vertices[_start];
        Key first = _queue.Count == 0 ? Key.Infinity : _queue.Min.Priority;
        if (first.CompareTo(CalculateKey(_start)) >= 0 && start.G == start.Rhs)
            return float.IsPositiveInfinity(start.Rhs.Cost) ? NavigationSearchStatus.Unreachable : NavigationSearchStatus.Found;

        QueueEntry entry = _queue.Min;
        _queue.Remove(entry);
        Vertex current = _vertices[entry.Vertex];
        current.QueuedKey = null;
        ExpandedVertexCount++;
        Key key = CalculateKey(entry.Vertex);
        if (entry.Priority.CompareTo(key) < 0) Enqueue(entry.Vertex, key);
        else if (current.G.CompareTo(current.Rhs) > 0)
        {
            current.G = current.Rhs;
            foreach (int predecessor in current.Predecessors) UpdateVertex(predecessor);
        }
        else
        {
            current.G = Distance.Infinity;
            UpdateVertex(entry.Vertex);
            foreach (int predecessor in current.Predecessors) UpdateVertex(predecessor);
        }
        return NavigationSearchStatus.Searching;
    }

    internal List<RouteEdge> CreateRoute()
    {
        var route = new List<RouteEdge>();
        int node = _start;
        while (node != _goal)
        {
            Arc selected = default;
            Distance best = Distance.Infinity;
            foreach (Arc edge in _vertices[node].Successors)
            {
                Distance candidate = _vertices[edge.To].G.Add(edge.Edge.Cost);
                if (candidate.CompareTo(best) >= 0) continue;
                selected = edge; best = candidate;
            }
            route.Add(selected.Edge);
            node = selected.To;
        }
        return route;
    }

    private void UpdateVertex(int index)
    {
        Vertex vertex = _vertices[index];
        if (index != _goal)
        {
            vertex.Rhs = Distance.Infinity;
            foreach (Arc edge in vertex.Successors)
            {
                Distance candidate = _vertices[edge.To].G.Add(edge.Edge.Cost);
                if (candidate.CompareTo(vertex.Rhs) < 0) vertex.Rhs = candidate;
            }
        }
        if (vertex.QueuedKey is { } old) _queue.Remove(new(index, old));
        vertex.QueuedKey = null;
        if (vertex.G != vertex.Rhs) Enqueue(index, CalculateKey(index));
    }

    private Key CalculateKey(int index)
    {
        Vertex vertex = _vertices[index];
        Distance minimum = vertex.G.CompareTo(vertex.Rhs) < 0 ? vertex.G : vertex.Rhs;
        return new(new(minimum.Cost + Heuristic(_startPosition, vertex.Position) + KeyModifier, minimum.Steps), minimum);
    }
    private float Heuristic(Vector3 first, Vector3 second) => Vector3.Distance(first, second) * _minimumAreaCost;
    private void Enqueue(int vertex, Key key)
    {
        _vertices[vertex].QueuedKey = key;
        _queue.Add(new(vertex, key));
    }
    private static int CompareArc(Arc first, Arc second)
    {
        int target = first.To.CompareTo(second.To);
        if (target != 0) return target;
        int connection = first.Edge.Connection.CompareTo(second.Edge.Connection);
        return connection != 0 ? connection : first.Edge.Cost.CompareTo(second.Edge.Cost);
    }
    private static bool SameEdges(List<Arc> first, List<Arc> second)
    {
        if (first.Count != second.Count) return false;
        for (int index = 0; index < first.Count; index++)
            if (CompareArc(first[index], second[index]) != 0) return false;
        return true;
    }

    private readonly record struct NodeIdentity(int Kind, int Region, NavigationTileCoordinate Tile,
        int Port, Vector3 Position, Vector3 Left, Vector3 Right);
    private readonly record struct Arc(int To, RouteEdge Edge);
    private sealed class Vertex
    {
        internal Vector3 Position;
        internal bool Active;
        internal Distance G = Distance.Infinity, Rhs = Distance.Infinity;
        internal List<Arc> Successors = [];
        internal readonly HashSet<int> Predecessors = [];
        internal Key? QueuedKey;
    }

    // Actual cost is primary. The positive secondary step count orders zero-cost tile crossings.
    private readonly record struct Distance(float Cost, int Steps) : IComparable<Distance>
    {
        internal static readonly Distance Infinity = new(float.PositiveInfinity, int.MaxValue);
        internal static readonly Distance Zero = new(0, 0);
        internal Distance Add(float cost) => float.IsPositiveInfinity(Cost) ? Infinity : new(Cost + cost, Steps + 1);
        public int CompareTo(Distance other)
        {
            int cost = Cost.CompareTo(other.Cost);
            return cost != 0 ? cost : Steps.CompareTo(other.Steps);
        }
    }
    private readonly record struct Key(Distance First, Distance Second) : IComparable<Key>
    {
        internal static readonly Key Infinity = new(Distance.Infinity, Distance.Infinity);
        public int CompareTo(Key other)
        {
            int first = First.CompareTo(other.First);
            return first != 0 ? first : Second.CompareTo(other.Second);
        }
    }
    private readonly record struct QueueEntry(int Vertex, Key Priority) : IComparable<QueueEntry>
    {
        public int CompareTo(QueueEntry other)
        {
            int key = Priority.CompareTo(other.Priority);
            return key != 0 ? key : Vertex.CompareTo(other.Vertex);
        }
    }
}
