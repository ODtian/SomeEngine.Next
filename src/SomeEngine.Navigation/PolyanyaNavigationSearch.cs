using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One request's roots, visible intervals, priority queue and geometric distances.</summary>
public sealed class PolyanyaSearchState
{
    private const float Tolerance = 0.00001f;
    private readonly NavigationSearchInput<NavigationMesh> _input;
    private readonly PriorityQueue<int, float> _open = new();
    private readonly List<IntervalNode> _nodes = [];
    private readonly Dictionary<Vector3, float> _rootDistances = [];
    private readonly HashSet<Vector3> _boundaryVertices = [];
    private readonly bool[] _goalPolygons;
    private readonly ulong _capabilities;
    private readonly Vector3 _start;
    private readonly Vector3 _goal;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal PolyanyaSearchState(in NavigationSearchInput<NavigationMesh> input)
    {
        _input = input;
        _capabilities = input.Profile.Capabilities & input.Policy.AllowedCapabilities;
        NavigationMesh mesh = input.Space;
        _goalPolygons = new bool[mesh.Polygons.Length];
        int startPolygon = mesh.FindPolygon(input.Start);
        int goalPolygon = mesh.FindPolygon(input.Goal);
        if (startPolygon < 0 || goalPolygon < 0)
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }

        mesh.TrySampleHeight(startPolygon, input.Start, out float startHeight);
        mesh.TrySampleHeight(goalPolygon, input.Goal, out float goalHeight);
        _start = new(input.Start.X, startHeight, input.Start.Z);
        _goal = new(input.Goal.X, goalHeight, input.Goal.Z);
        for (int polygon = 0; polygon < mesh.Polygons.Length; polygon++)
        {
            if (!CanEnter(polygon)) continue;
            _goalPolygons[polygon] = Contains(polygon, _goal);
            if (Contains(polygon, _start))
                Enqueue(new(_start, _start, _start, -1, polygon, -1, 0, -1));
        }
        if (_open.Count == 0 || Array.IndexOf(_goalPolygons, true) < 0)
            _status = NavigationSearchStatus.Unreachable;
        else
            FindBoundaryVertices();
    }

    internal NavigationSearchStep Advance(int expansionBudget)
    {
        if (_status != NavigationSearchStatus.Searching)
            return new(_status, _path);

        for (int expanded = 0; expanded < expansionBudget && _open.TryDequeue(out int index, out _); expanded++)
        {
            IntervalNode node = _nodes[index];
            if (_rootDistances[node.Root] + Tolerance < node.Distance)
                continue;
            if (node.Goal)
            {
                _path = CreatePath(index);
                _status = NavigationSearchStatus.Found;
                return new(_status, _path);
            }
            Expand(index, node);
        }

        if (_open.Count == 0)
            _status = NavigationSearchStatus.Unreachable;
        return new(_status, _path);
    }

    private void Expand(int parent, IntervalNode node)
    {
        NavigationMesh mesh = _input.Space;
        NavigationPolygon polygon = mesh.Polygons.Span[node.Polygon];
        bool initial = node.Portal < 0;
        bool collinear = !initial && MathF.Abs(Cross(node.Left - node.Root, node.Right - node.Root)) <= Tolerance;
        Vector3 root = node.Root;
        float distance = node.Distance;
        if (collinear)
        {
            bool left = Vector3.DistanceSquared(root, node.Left) <= Vector3.DistanceSquared(root, node.Right);
            if (!TryTurn(node, left, out root)) return;
            distance += Vector3.Distance(node.Root, root);
        }

        if (_goalPolygons[node.Polygon])
        {
            if (initial || collinear)
                EnqueueGoal(parent, node.Polygon, root, distance);
            else if (TryRoot(node, _goal, out root, out distance))
                EnqueueGoal(parent, node.Polygon, root, distance);
        }

        int portalEnd = polygon.PortalStart + polygon.PortalCount;
        for (int portalIndex = polygon.PortalStart; portalIndex < portalEnd; portalIndex++)
        {
            NavigationPortal portal = mesh.Portals.Span[portalIndex];
            if (!CanTraverse(portal)) continue;
            if (initial)
            {
                if (!OnSegment(_start, portal.Left, portal.Right))
                    Enqueue(new(node.Root, portal.Left, portal.Right, node.Polygon,
                        portal.ToPolygon, portalIndex, node.Distance, parent));
            }
            else if (!IsReverse(node, portal))
            {
                if (collinear)
                    Enqueue(new(root, portal.Left, portal.Right, node.Polygon,
                        portal.ToPolygon, portalIndex, distance, parent));
                else
                    Project(parent, node, portalIndex, portal);
            }
        }
    }

    private void Project(int parent, IntervalNode node, int portalIndex, NavigationPortal portal)
    {
        // The two root rays split this portal into observable and turning intervals.
        Span<float> cuts = stackalloc float[4];
        cuts[0] = 0;
        cuts[1] = 1;
        int count = 2;
        AddRayCut(node.Root, node.Left, portal, cuts, ref count);
        AddRayCut(node.Root, node.Right, portal, cuts, ref count);
        cuts[..count].Sort();
        for (int cut = 1; cut < count; cut++)
        {
            float start = cuts[cut - 1], end = cuts[cut];
            if (end - start <= Tolerance) continue;
            Vector3 midpoint = Vector3.Lerp(portal.Left, portal.Right, (start + end) * 0.5f);
            if (!TryRoot(node, midpoint, out Vector3 root, out float distance)) continue;
            Vector3 left = Interpolate(portal, start), right = Interpolate(portal, end);
            Enqueue(new(root, left, right, node.Polygon, portal.ToPolygon, portalIndex, distance, parent));
        }
    }

    private static void AddRayCut(Vector3 root, Vector3 endpoint, NavigationPortal portal,
        Span<float> cuts, ref int count)
    {
        float first = Cross(endpoint - root, portal.Left - root);
        float last = Cross(endpoint - root, portal.Right - root);
        if (first == last) return;
        float fraction = first / (first - last);
        if (fraction > 0 && fraction < 1)
            cuts[count++] = fraction;
    }

    private bool TryRoot(IntervalNode node, Vector3 point, out Vector3 root, out float distance)
    {
        root = node.Root;
        distance = node.Distance;
        bool rightShadow = Cross(node.Right - root, point - root) < -Tolerance;
        bool leftShadow = Cross(node.Left - root, point - root) > Tolerance;
        if (!rightShadow && !leftShadow) return true;
        if (!TryTurn(node, leftShadow, out root)) return false;
        distance += Vector3.Distance(node.Root, root);
        return true;
    }

    private bool TryTurn(IntervalNode node, bool left, out Vector3 root)
    {
        NavigationPortal portal = _input.Space.Portals.Span[node.Portal];
        root = left ? portal.Left : portal.Right;
        return SamePoint(left ? node.Left : node.Right, root) && _boundaryVertices.Contains(root);
    }

    private bool IsReverse(IntervalNode node, NavigationPortal portal)
    {
        NavigationPortal incoming = _input.Space.Portals.Span[node.Portal];
        return portal.ToPolygon == node.FromPolygon &&
            SamePoint(portal.Left, incoming.Right) && SamePoint(portal.Right, incoming.Left);
    }

    private void EnqueueGoal(int parent, int polygon, Vector3 root, float distance) =>
        Enqueue(new(root, _goal, _goal, polygon, polygon, -1, distance, parent, true));

    private void Enqueue(IntervalNode node)
    {
        if (_rootDistances.TryGetValue(node.Root, out float previous) && previous + Tolerance < node.Distance)
            return;
        if (!_rootDistances.ContainsKey(node.Root) || node.Distance < previous)
            _rootDistances[node.Root] = node.Distance;
        int index = _nodes.Count;
        _nodes.Add(node);
        float remaining = node.Portal < 0
            ? Vector3.Distance(node.Root, _goal)
            : IntervalDistance(node.Root, node.Left, node.Right);
        _open.Enqueue(index, node.Distance + remaining);
    }

    private float IntervalDistance(Vector3 root, Vector3 left, Vector3 right)
    {
        if (SamePoint(root, left) || SamePoint(root, right))
            return Vector3.Distance(root, _goal);
        Vector3 edge = right - left;
        Vector3 target = _goal;
        if (Cross(edge, root - left) * Cross(edge, target - left) > 0)
            target = 2 * (left + edge * (Vector3.Dot(target - left, edge) / edge.LengthSquared())) - target;
        float denominator = Cross(target - root, edge);
        if (MathF.Abs(denominator) > Tolerance)
        {
            float fraction = Cross(left - root, target - root) / denominator;
            if (fraction >= 0 && fraction <= 1)
                return Vector3.Distance(root, target);
        }
        return MathF.Min(Vector3.Distance(root, left) + Vector3.Distance(left, _goal),
            Vector3.Distance(root, right) + Vector3.Distance(right, _goal));
    }

    private void FindBoundaryVertices()
    {
        // Subtract traversable portal spans from each edge, including partial-edge portals.
        NavigationMesh mesh = _input.Space;
        var spans = new List<EdgeSpan>();
        for (int polygonIndex = 0; polygonIndex < mesh.Polygons.Length; polygonIndex++)
        {
            if (!CanEnter(polygonIndex)) continue;
            NavigationPolygon polygon = mesh.Polygons.Span[polygonIndex];
            ReadOnlySpan<int> vertices = mesh.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount);
            for (int vertex = 0; vertex < vertices.Length; vertex++)
            {
                Vector3 first = mesh.Positions.Span[vertices[vertex]];
                Vector3 last = mesh.Positions.Span[vertices[(vertex + 1) % vertices.Length]];
                Vector3 edge = last - first;
                spans.Clear();
                foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                {
                    if (!CanTraverse(portal) || !OnSegment(portal.Left, first, last) ||
                        !OnSegment(portal.Right, first, last)) continue;
                    float left = Vector3.Dot(portal.Left - first, edge) / edge.LengthSquared();
                    float right = Vector3.Dot(portal.Right - first, edge) / edge.LengthSquared();
                    spans.Add(left < right
                        ? new(left, right, portal.Left, portal.Right)
                        : new(right, left, portal.Right, portal.Left));
                }
                spans.Sort(static (a, b) => a.Start.CompareTo(b.Start));
                float covered = 0;
                Vector3 coveredPoint = first;
                foreach (EdgeSpan span in spans)
                {
                    if (span.Start > covered + Tolerance)
                    {
                        _boundaryVertices.Add(coveredPoint);
                        _boundaryVertices.Add(span.First);
                    }
                    if (span.End > covered)
                    {
                        covered = span.End;
                        coveredPoint = span.Last;
                    }
                }
                if (covered < 1 - Tolerance)
                {
                    _boundaryVertices.Add(coveredPoint);
                    _boundaryVertices.Add(last);
                }
            }
        }
    }

    private bool CanEnter(int polygonIndex)
    {
        NavigationPolygon polygon = _input.Space.Polygons.Span[polygonIndex];
        return (polygon.RequiredCapabilities & _capabilities) == polygon.RequiredCapabilities &&
            !float.IsPositiveInfinity(_input.Policy.GetAreaCost(polygon.Area));
    }

    private bool CanTraverse(NavigationPortal portal) =>
        (portal.RequiredCapabilities & _capabilities) == portal.RequiredCapabilities &&
        !float.IsPositiveInfinity(portal.Cost) &&
        !float.IsPositiveInfinity(_input.Policy.GetActionCost(portal.Action)) && CanEnter(portal.ToPolygon);

    private bool Contains(int polygon, Vector3 point) =>
        _input.Space.TrySampleHeight(polygon, point, out float height) && MathF.Abs(height - point.Y) <= Tolerance;

    private NavigationPath CreatePath(int goalIndex)
    {
        var chain = new List<int>();
        for (int index = goalIndex; index >= 0; index = _nodes[index].Parent)
            chain.Add(index);
        chain.Reverse();
        var corridor = new List<int>();
        var corners = new List<Vector3>();
        var tiles = new HashSet<NavigationTileCoordinate>();
        var dependencies = new List<NavigationPathDependency>();
        foreach (int index in chain)
        {
            IntervalNode node = _nodes[index];
            if (corners.Count == 0 || !SamePoint(corners[^1], node.Root))
                corners.Add(node.Root);
            if (corridor.Count == 0 || corridor[^1] != node.Polygon)
                corridor.Add(node.Polygon);
            NavigationTileCoordinate tile = _input.Space.Polygons.Span[node.Polygon].Tile;
            if (tiles.Add(tile))
                dependencies.Add(new(tile, _input.Space.ContentVersion));
        }
        if (!SamePoint(corners[^1], _goal))
            corners.Add(_goal);
        IntervalNode target = _nodes[goalIndex];
        return new(corridor.ToArray(), corners.ToArray(), dependencies.ToArray(),
            target.Distance + Vector3.Distance(target.Root, _goal));
    }

    private static bool OnSegment(Vector3 point, Vector3 first, Vector3 last)
    {
        Vector3 edge = last - first;
        float fraction = Vector3.Dot(point - first, edge) / edge.LengthSquared();
        return MathF.Abs(Cross(edge, point - first)) <= Tolerance &&
            fraction >= -Tolerance && fraction <= 1 + Tolerance;
    }

    private static Vector3 Interpolate(NavigationPortal portal, float fraction) => fraction switch
    {
        0 => portal.Left,
        1 => portal.Right,
        _ => Vector3.Lerp(portal.Left, portal.Right, fraction),
    };

    private static float Cross(Vector3 first, Vector3 second) => first.X * second.Z - first.Z * second.X;
    private static bool SamePoint(Vector3 first, Vector3 second) =>
        Vector3.DistanceSquared(first, second) <= Tolerance * Tolerance;

    private readonly record struct EdgeSpan(float Start, float End, Vector3 First, Vector3 Last);
    private readonly record struct IntervalNode(Vector3 Root, Vector3 Left, Vector3 Right,
        int FromPolygon, int Polygon, int Portal, float Distance, int Parent, bool Goal = false);
}

/// <summary>Geometric shortest paths on planar convex polygon meshes using Polyanya.</summary>
/// <remarks>
/// Select this search through NavigationSystems with PolyanyaSearchState for a uniform Euclidean-distance
/// objective. The profile and policy determine available polygons and portals; the result cost is metres.
/// Each request keeps its own root/interval search across budgeted jobs and uses the common path consumers.
/// Algorithm: Cui, Harabor and Grastien, IJCAI 2017, https://www.ijcai.org/proceedings/2017/0070.pdf.
/// </remarks>
public readonly struct PolyanyaNavigationSearch : INavigationSearch<NavigationMesh, PolyanyaSearchState>
{
    public static PolyanyaSearchState Create(in NavigationSearchInput<NavigationMesh> input) => new(in input);
    public static NavigationSearchStep Advance(PolyanyaSearchState state, int expansionBudget) =>
        state.Advance(expansionBudget);
}
