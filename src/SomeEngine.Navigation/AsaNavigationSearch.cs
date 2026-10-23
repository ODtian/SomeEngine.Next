using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>A request-owned A*-string-pulling-A* workspace. Both searches consume the caller's expansion budget.</summary>
public sealed class AsaSearchState
{
    private readonly NavigationSearchInput<HeadingNavigationMesh> _input;
    private AsaGraph _graph = new();
    private AsaGraphSearch _search = null!;
    private int[] _corridor = [];
    private int[] _portals = [];
    private Vector3[] _crossings = [];
    private bool _refining;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal AsaSearchState(in NavigationSearchInput<HeadingNavigationMesh> input)
    {
        _input = input;
        int start = input.Space.FindRegion(input.Start, input.StartHeading);
        int goal = input.Space.FindRegion(input.Goal, input.GoalHeading);
        if (start < 0 || goal < 0 || !CanEnter(start) || !CanEnter(goal))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }
        HeadingNavigationMesh mesh = input.Space;
        foreach (HeadingNavigationNode node in mesh.Graph.Nodes.Span)
            _graph.Add(new(node.Position, mesh.GetChannel(node.Channel).Center, node.Region, node.Channel));
        for (int index = 0; index < mesh.Graph.Nodes.Length; index++)
        {
            HeadingNavigationNode node = mesh.Graph.Nodes.Span[index];
            if (!CanEnter(node.Region)) continue;
            foreach (HeadingNavigationEdge edge in mesh.Graph.Edges.Span.Slice(node.ConnectionStart, node.ConnectionCount))
            {
                if (!CanEnter(mesh.Graph.Nodes.Span[edge.ToNode].Region)) continue;
                float cost = edge.Action == HeadingNavigationAction.Rotation
                    ? RotationCost(_graph.Nodes[index].Heading, _graph.Nodes[edge.ToNode].Heading)
                    : TranslationCost(_graph.Nodes[index], _graph.Nodes[edge.ToNode]);
                if (edge.Portal >= 0) cost += PortalCost(edge.Portal);
                _graph.Connect(index, edge.ToNode, cost, edge.Portal);
            }
        }
        int[] starts = AddPosition(start, SurfacePoint(start, input.Start));
        int[] goals = AddPosition(goal, SurfacePoint(goal, input.Goal));
        ConnectBoundary(starts, start, outward: true);
        ConnectBoundary(goals, goal, outward: false);
        if (start == goal) ConnectTranslation(starts, goals);
        AttachEndpoints(starts, goals);
    }

    internal NavigationSearchStep Advance(int budget)
    {
        if (_status != NavigationSearchStatus.Searching) return new(_status, _path);
        while (budget > 0)
        {
            NavigationSearchStatus step = _search.Advance(ref budget);
            if (step == NavigationSearchStatus.Searching) break;
            if (step == NavigationSearchStatus.Unreachable) { _status = step; break; }
            if (_refining)
            {
                _path = CreatePath();
                _status = NavigationSearchStatus.Found;
                break;
            }
            Refine();
        }
        return new(_status, _path);
    }

    private void Refine()
    {
        int[] route = _search.Route();
        var regions = new List<int>();
        var portals = new List<int>();
        foreach (int node in route)
        {
            int region = _graph.Nodes[node].Region;
            if (regions.Count > 0 && regions[^1] == region) continue;
            regions.Add(region);
            if (regions.Count > 1) portals.Add(_search.ParentPortals[node]);
        }
        _corridor = regions.ToArray();
        _portals = portals.ToArray();
        _crossings = AsaPathStraightening.Crossings(_input.Space.Geometry, _portals, _corridor,
            _graph.Nodes[_search.Start].Position, _graph.Nodes[_search.Goal].Position);
        _graph = new();
        int[] previousExit = [];
        int[] starts = [];
        for (int index = 0; index < _corridor.Length; index++)
        {
            int region = _corridor[index];
            int[] entry = AddPosition(region, SurfacePoint(region, _crossings[index]));
            int[] exit = AddPosition(region, SurfacePoint(region, _crossings[index + 1]));
            if (index == 0) starts = entry;
            else ConnectTranslation(previousExit, entry, _portals[index - 1]);
            ConnectTranslation(entry, exit);
            previousExit = exit;
        }
        AttachEndpoints(starts, previousExit);
        _refining = true;
    }

    private int[] AddPosition(int region, Vector3 position)
    {
        HeadingNavigationMesh mesh = _input.Space;
        var nodes = new List<int>();
        var channels = new Dictionary<int, int>();
        foreach (HeadingNavigationInterval heading in mesh.GetAllowedHeadings(region).Span)
        {
            int node = _graph.Add(new(position, heading.Center, region, heading.Channel));
            nodes.Add(node);
            channels.Add(heading.Channel, node);
        }
        HeadingNavigationRegion description = mesh.Regions.Span[region];
        foreach (HeadingNavigationRotation rotation in mesh.Rotations.Span.Slice(description.RotationStart, description.RotationCount))
            _graph.Connect(channels[rotation.FromChannel], channels[rotation.ToChannel],
                MathF.Abs(rotation.HeadingChange) / _input.Profile.MaximumAngularSpeed);
        return nodes.ToArray();
    }

    private void ConnectBoundary(int[] endpoint, int region, bool outward)
    {
        HeadingNavigationRegion description = _input.Space.Regions.Span[region];
        foreach (int end in endpoint)
        for (int node = description.NodeStart; node < description.NodeStart + description.NodeCount; node++)
        {
            if (_graph.Nodes[node].Channel != _graph.Nodes[end].Channel) continue;
            if (outward) _graph.Connect(end, node, TranslationCost(_graph.Nodes[end], _graph.Nodes[node]));
            else _graph.Connect(node, end, TranslationCost(_graph.Nodes[node], _graph.Nodes[end]));
        }
    }

    private void ConnectTranslation(int[] from, int[] to, int portal = -1)
    {
        foreach (int first in from)
        foreach (int second in to)
            if (_graph.Nodes[first].Channel == _graph.Nodes[second].Channel)
                _graph.Connect(first, second, TranslationCost(_graph.Nodes[first], _graph.Nodes[second]) +
                    (portal >= 0 ? PortalCost(portal) : 0), portal);
    }

    private void AttachEndpoints(int[] starts, int[] goals)
    {
        AsaNode first = _graph.Nodes[starts[0]], last = _graph.Nodes[goals[0]];
        int start = _graph.Add(first with { Heading = _input.StartHeading, Channel = -1 });
        int goal = _graph.Add(last with { Heading = _input.GoalHeading ?? last.Heading, Channel = -1 });
        foreach (int node in starts)
            if (_input.Space.GetChannel(_graph.Nodes[node].Channel).Contains(_input.StartHeading))
                _graph.Connect(start, node, RotationCost(_input.StartHeading, _graph.Nodes[node].Heading));
        foreach (int node in goals)
            if (_input.GoalHeading is not { } heading || _input.Space.GetChannel(_graph.Nodes[node].Channel).Contains(heading))
                _graph.Connect(node, goal, _input.GoalHeading is { } target ? RotationCost(_graph.Nodes[node].Heading, target) : 0);
        float minimumAreaCost = _input.Policy.DefaultAreaCost;
        foreach (NavigationAreaCost area in _input.Policy.AreaCosts.Span) minimumAreaCost = MathF.Min(minimumAreaCost, area.Multiplier);
        _search = new(_graph, start, goal, _input.Profile.MaximumSpeed, _input.Profile.MaximumAngularSpeed,
            minimumAreaCost, _input.GoalHeading.HasValue);
    }

    private bool CanEnter(int region)
    {
        NavigationPolygon polygon = _input.Space.Geometry.Polygons.Span[region];
        ulong capabilities = _input.Profile.Capabilities & _input.Policy.AllowedCapabilities;
        return (polygon.RequiredCapabilities & capabilities) == polygon.RequiredCapabilities &&
            float.IsFinite(_input.Policy.GetAreaCost(polygon.Area));
    }

    private float PortalCost(int index)
    {
        NavigationPortal portal = _input.Space.Geometry.Portals.Span[index];
        ulong capabilities = _input.Profile.Capabilities & _input.Policy.AllowedCapabilities;
        return (portal.RequiredCapabilities & capabilities) == portal.RequiredCapabilities
            ? portal.Cost + _input.Policy.GetActionCost(portal.Action) : float.PositiveInfinity;
    }

    private float TranslationCost(AsaNode first, AsaNode second)
    {
        Vector3 displacement = second.Position - first.Position;
        Vector3 longitudinal = NavigationHeading.Direction(first.Heading);
        Vector3 lateral = new(longitudinal.Z, 0, -longitudinal.X);
        float cost = (MathF.Abs(Vector3.Dot(displacement, longitudinal)) + MathF.Abs(Vector3.Dot(displacement, lateral))) /
            _input.Profile.MaximumSpeed;
        return cost * _input.Policy.GetAreaCost(_input.Space.Geometry.Polygons.Span[first.Region].Area);
    }

    private float RotationCost(float from, float to) => MathF.Abs(NavigationHeading.Difference(to, from)) / _input.Profile.MaximumAngularSpeed;
    private Vector3 SurfacePoint(int region, Vector3 position) =>
        _input.Space.Geometry.TrySampleHeight(region, position, out float height) ? new(position.X, height, position.Z) : position;

    private NavigationPath CreatePath()
    {
        var poses = new List<NavigationPose>();
        foreach (int index in _search.Route())
        {
            AsaNode node = _graph.Nodes[index];
            float heading = index == _search.Goal && !_input.GoalHeading.HasValue ? poses[^1].Heading : node.Heading;
            var pose = new NavigationPose(node.Position, heading);
            if (poses.Count == 0 || poses[^1] != pose) poses.Add(pose);
        }
        var dependencies = new List<NavigationPathDependency>();
        var seen = new HashSet<NavigationTileCoordinate>();
        foreach (int polygon in _corridor)
        {
            NavigationTileCoordinate tile = _input.Space.Geometry.Polygons.Span[polygon].Tile;
            if (seen.Add(tile)) dependencies.Add(new(tile, _input.Space.ContentVersion));
        }
        return new(_corridor, _crossings, dependencies.ToArray(), _search.Cost,
            sections: new NavigationPathSection[] { new(0, default, _input.Space.Geometry, 0, _corridor.Length) }, poses: poses.ToArray());
    }
}

/// <summary>ASA on the representation's own heading graph, followed by spatial and heading refinement.</summary>
public readonly struct AsaNavigationSearch : INavigationSearch<HeadingNavigationMesh, AsaSearchState>
{
    public static AsaSearchState Create(in NavigationSearchInput<HeadingNavigationMesh> input) => new(in input);
    public static NavigationSearchStep Advance(AsaSearchState state, int expansionBudget) => state.Advance(expansionBudget);
}

internal readonly record struct AsaNode(Vector3 Position, float Heading, int Region, int Channel);
internal readonly record struct AsaEdge(int To, float Cost, int Portal);
internal sealed class AsaGraph
{
    internal readonly List<AsaNode> Nodes = [];
    internal readonly List<List<AsaEdge>> Edges = [];
    internal int Add(AsaNode node) { Nodes.Add(node); Edges.Add([]); return Nodes.Count - 1; }
    internal void Connect(int from, int to, float cost, int portal = -1)
    {
        if (float.IsFinite(cost)) Edges[from].Add(new(to, cost, portal));
    }
}

internal sealed class AsaGraphSearch
{
    private readonly AsaGraph _graph;
    private readonly float _speed, _angularSpeed, _minimumAreaCost;
    private readonly bool _goalHeading;
    private readonly PriorityQueue<(int Node, float Cost), float> _open = new();
    private readonly float[] _costs;
    private readonly int[] _parents;
    internal readonly int[] ParentPortals;
    internal int Start { get; }
    internal int Goal { get; }
    internal float Cost => _costs[Goal];
    internal AsaGraphSearch(AsaGraph graph, int start, int goal, float speed, float angularSpeed, float minimumAreaCost, bool goalHeading)
    {
        _graph = graph; Start = start; Goal = goal; _speed = speed; _angularSpeed = angularSpeed;
        _minimumAreaCost = minimumAreaCost; _goalHeading = goalHeading;
        _costs = new float[graph.Nodes.Count]; Array.Fill(_costs, float.PositiveInfinity);
        _parents = new int[graph.Nodes.Count]; Array.Fill(_parents, -1);
        ParentPortals = new int[graph.Nodes.Count]; Array.Fill(ParentPortals, -1);
        _costs[start] = 0;
        _open.Enqueue((start, 0), Heuristic(start));
    }

    internal NavigationSearchStatus Advance(ref int budget)
    {
        while (budget > 0 && _open.TryDequeue(out var entry, out _))
        {
            budget--;
            if (entry.Cost != _costs[entry.Node]) continue;
            if (entry.Node == Goal) return NavigationSearchStatus.Found;
            foreach (AsaEdge edge in _graph.Edges[entry.Node])
            {
                float cost = entry.Cost + edge.Cost;
                if (cost >= _costs[edge.To]) continue;
                _costs[edge.To] = cost;
                _parents[edge.To] = entry.Node;
                ParentPortals[edge.To] = edge.Portal;
                _open.Enqueue((edge.To, cost), cost + Heuristic(edge.To));
            }
        }
        return _open.Count == 0 ? NavigationSearchStatus.Unreachable : NavigationSearchStatus.Searching;
    }

    internal int[] Route()
    {
        var nodes = new List<int>();
        for (int node = Goal; node >= 0; node = _parents[node]) nodes.Add(node);
        nodes.Reverse();
        return nodes.ToArray();
    }

    private float Heuristic(int node)
    {
        AsaNode current = _graph.Nodes[node], goal = _graph.Nodes[Goal];
        Vector3 delta = goal.Position - current.Position;
        return MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z) / _speed * _minimumAreaCost +
            (_goalHeading ? MathF.Abs(NavigationHeading.Difference(goal.Heading, current.Heading)) / _angularSpeed : 0);
    }
}
