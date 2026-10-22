using System.Numerics;

namespace SomeEngine.Navigation;

/// <summary>One path request's open nodes, distances and predecessor portals.</summary>
public sealed class PolygonSearchState
{
    private readonly NavigationSearchInput<NavigationMesh> _input;
    private readonly PriorityQueue<SearchEntry, float> _open = new();
    private readonly float[] _costs;
    private readonly int[] _parents;
    private readonly int _startPolygon;
    private readonly int _goalPolygon;
    private readonly Vector3 _start;
    private readonly Vector3 _goal;
    private readonly ulong _capabilities;
    private readonly float _minimumAreaCost;
    private NavigationSearchStatus _status;
    private NavigationPath? _path;

    internal PolygonSearchState(in NavigationSearchInput<NavigationMesh> input)
    {
        _input = input;
        _capabilities = input.Profile.Capabilities & input.Policy.AllowedCapabilities;
        _minimumAreaCost = input.Policy.DefaultAreaCost;
        foreach (NavigationAreaCost cost in input.Policy.AreaCosts.Span)
            _minimumAreaCost = MathF.Min(_minimumAreaCost, cost.Multiplier);
        _costs = new float[input.Space.Portals.Length + 2];
        _parents = new int[_costs.Length];
        Array.Fill(_costs, float.PositiveInfinity);
        Array.Fill(_parents, -1);
        _startPolygon = input.Space.FindPolygon(input.Start);
        _goalPolygon = input.Space.FindPolygon(input.Goal);
        if (_startPolygon < 0 || _goalPolygon < 0 ||
            !CanEnter(input.Space.Polygons.Span[_startPolygon]) || !CanEnter(input.Space.Polygons.Span[_goalPolygon]))
        {
            _status = NavigationSearchStatus.Unreachable;
            return;
        }

        input.Space.TrySampleHeight(_startPolygon, input.Start, out float startHeight);
        input.Space.TrySampleHeight(_goalPolygon, input.Goal, out float goalHeight);
        _start = new(input.Start.X, startHeight, input.Start.Z);
        _goal = new(input.Goal.X, goalHeight, input.Goal.Z);
        _costs[0] = 0;
        _open.Enqueue(new(0, 0), Vector3.Distance(_start, _goal) * _minimumAreaCost);
    }

    private int GoalNode => _costs.Length - 1;

    internal NavigationSearchStep Advance(int expansionBudget)
    {
        if (_status != NavigationSearchStatus.Searching)
            return new(_status, _path);

        NavigationMesh mesh = _input.Space;
        for (int expanded = 0; expanded < expansionBudget && _open.TryDequeue(out SearchEntry entry, out _); expanded++)
        {
            if (entry.Cost != _costs[entry.Node])
                continue;
            if (entry.Node == GoalNode)
            {
                _path = CreatePath();
                _status = NavigationSearchStatus.Found;
                return new(_status, _path);
            }

            int polygonIndex = entry.Node == 0 ? _startPolygon : mesh.Portals.Span[entry.Node - 1].ToPolygon;
            NavigationPolygon polygon = mesh.Polygons.Span[polygonIndex];
            Vector3 position = Position(entry.Node);
            float areaCost = _input.Policy.GetAreaCost(polygon.Area);
            if (polygonIndex == _goalPolygon)
                Relax(GoalNode, entry.Node, entry.Cost + Vector3.Distance(position, _goal) * areaCost);

            int portalEnd = polygon.PortalStart + polygon.PortalCount;
            for (int portalIndex = polygon.PortalStart; portalIndex < portalEnd; portalIndex++)
            {
                NavigationPortal portal = mesh.Portals.Span[portalIndex];
                if ((portal.RequiredCapabilities & _capabilities) != portal.RequiredCapabilities ||
                    !CanEnter(mesh.Polygons.Span[portal.ToPolygon]))
                    continue;
                Vector3 midpoint = (portal.Left + portal.Right) * 0.5f;
                float cost = entry.Cost + Vector3.Distance(position, midpoint) * areaCost +
                    portal.Cost + _input.Policy.GetActionCost(portal.Action);
                Relax(portalIndex + 1, entry.Node, cost);
            }
        }

        if (_open.Count == 0)
            _status = NavigationSearchStatus.Unreachable;
        return new(_status, _path);
    }

    private Vector3 Position(int node)
    {
        if (node == 0) return _start;
        if (node == GoalNode) return _goal;
        NavigationPortal portal = _input.Space.Portals.Span[node - 1];
        return (portal.Left + portal.Right) * 0.5f;
    }

    private bool CanEnter(NavigationPolygon polygon) =>
        (polygon.RequiredCapabilities & _capabilities) == polygon.RequiredCapabilities &&
        !float.IsPositiveInfinity(_input.Policy.GetAreaCost(polygon.Area));

    private void Relax(int node, int parent, float cost)
    {
        if (cost >= _costs[node]) return;
        _costs[node] = cost;
        _parents[node] = parent;
        _open.Enqueue(new(node, cost), cost + Vector3.Distance(Position(node), _goal) * _minimumAreaCost);
    }

    private NavigationPath CreatePath()
    {
        var portalIndices = new List<int>();
        for (int node = _parents[GoalNode]; node > 0; node = _parents[node])
            portalIndices.Add(node - 1);
        portalIndices.Reverse();

        NavigationMesh mesh = _input.Space;
        var corridor = new int[portalIndices.Count + 1];
        corridor[0] = _startPolygon;
        for (int index = 0; index < portalIndices.Count; index++)
        {
            NavigationPortal portal = mesh.Portals.Span[portalIndices[index]];
            corridor[index + 1] = portal.ToPolygon;
        }
        Vector3[] corners = NavigationFunnel.Pull(mesh, portalIndices.ToArray(), corridor, _start, _goal);

        var tiles = new HashSet<NavigationTileCoordinate>();
        var dependencies = new List<NavigationPathDependency>();
        foreach (int polygon in corridor)
        {
            NavigationTileCoordinate tile = mesh.Polygons.Span[polygon].Tile;
            if (tiles.Add(tile))
                dependencies.Add(new(tile, mesh.ContentVersion));
        }
        return new(corridor, corners, dependencies.ToArray(), _costs[GoalNode]);
    }

    private readonly record struct SearchEntry(int Node, float Cost);
}

/// <summary>A* over directed polygon entrances, with a separate workspace for each request.</summary>
public readonly struct PolygonNavigationSearch : INavigationSearch<NavigationMesh, PolygonSearchState>
{
    public static PolygonSearchState Create(in NavigationSearchInput<NavigationMesh> input) => new(in input);
    public static NavigationSearchStep Advance(PolygonSearchState state, int expansionBudget) =>
        state.Advance(expansionBudget);
}
