using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationRegionBuildState
{
    public NavigationMesh Mesh { get; internal set; } = null!;
    public NavigationRegionGraph Graph => Mesh.RegionGraph!;
    internal NavigationMesh Input { get; }
    internal NavigationProfile Profile { get; }
    internal NavigationPolicy Policy { get; }
    internal ReadOnlyMemory<NavigationRegionPort> Ports { get; }
    internal NavigationRegionGraph? Previous { get; }
    internal NavigationMeshTile[] TileMeshes = [];
    internal NavigationRegionTile[] Tiles = [];
    internal NavigationRegionEntrance[] Entrances = [];
    internal NavigationRegionEdge[] Crossings = [];
    internal NavigationRegionEdge[][] LocalEdges = [];

    internal NavigationRegionBuildState(NavigationMesh mesh, NavigationProfile profile,
        NavigationPolicy policy, ReadOnlyMemory<NavigationRegionPort> ports, NavigationRegionGraph? previous) =>
        (Input, Profile, Policy, Ports, Previous) = (mesh, profile, policy, ports, previous);
}

/// <summary>Builds HPA-style entrance costs using actual polygon searches inside each detailed tile.</summary>
public static class NavigationRegionBuilder
{
    public static NavigationBuild<NavigationRegionBuildState> Schedule(NavigationMesh mesh,
        NavigationProfile profile, NavigationPolicy policy, NavigationRegionPort[] ports,
        JobHandle dependency = default, Scheduler? scheduler = null) =>
        Schedule(mesh, profile, policy, (ReadOnlyMemory<NavigationRegionPort>)ports, dependency, scheduler);

    public static NavigationBuild<NavigationRegionBuildState> Schedule(NavigationMesh mesh,
        NavigationProfile profile, NavigationPolicy policy, ReadOnlyMemory<NavigationRegionPort> ports = default,
        JobHandle dependency = default, Scheduler? scheduler = null, NavigationRegionGraph? previous = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationRegionBuildState(mesh, profile, policy, ports, previous);
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, profile.Footprint),
            NavigationJobAccess.Read(scheduler, policy.AreaCosts),
            NavigationJobAccess.Read(scheduler, policy.ActionCosts),
            NavigationJobAccess.Read(scheduler, ports),
        };
        mesh.AddReadAccesses(scheduler, accesses);
        if (previous is not null)
        {
            accesses.Add(NavigationJobAccess.Read(scheduler, previous.Tiles));
            accesses.Add(NavigationJobAccess.Read(scheduler, previous.Entrances));
            accesses.Add(NavigationJobAccess.Read(scheduler, previous.Edges));
        }
        return new(state, scheduler.Schedule(new BuildTopology(state, scheduler), accesses.ToArray(), dependency));
    }

    private readonly struct BuildTopology(NavigationRegionBuildState state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationMesh mesh = state.Input;
            state.TileMeshes = mesh.SeparateTiles();
            var tileIndices = new Dictionary<NavigationTileCoordinate, int>();
            for (int tile = 0; tile < state.TileMeshes.Length; tile++)
                tileIndices.Add(state.TileMeshes[tile].Tile, tile);
            var counts = new int[state.TileMeshes.Length];
            var localPolygons = new int[mesh.Polygons.Length];
            for (int polygon = 0; polygon < mesh.Polygons.Length; polygon++)
                localPolygons[polygon] = counts[tileIndices[mesh.Polygons.Span[polygon].Tile]]++;

            var entrances = new List<NavigationRegionEntrance>();
            var crossingPoints = new Dictionary<(int Polygon, Vector3 Position), int>();
            var crossings = new List<NavigationRegionEdge>();
            ulong capabilities = state.Profile.Capabilities & state.Policy.AllowedCapabilities;
            for (int polygonIndex = 0; polygonIndex < mesh.Polygons.Length; polygonIndex++)
            {
                NavigationPolygon polygon = mesh.Polygons.Span[polygonIndex];
                if (!Available(polygon, capabilities, state.Policy)) continue;
                foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                {
                    NavigationPolygon target = mesh.Polygons.Span[portal.ToPolygon];
                    if (target.Tile == polygon.Tile || !Available(target, capabilities, state.Policy) ||
                        (portal.RequiredCapabilities & capabilities) != portal.RequiredCapabilities) continue;
                    float added = portal.Cost + state.Policy.GetActionCost(portal.Action);
                    if (!float.IsFinite(added)) continue;
                    int from = AddCrossingPoint(polygonIndex, portal.Left, portal.Right);
                    int to = AddCrossingPoint(portal.ToPolygon, portal.Right, portal.Left);
                    float cost = Vector3.Distance(entrances[from].Position, entrances[to].Position) *
                        state.Policy.GetAreaCost(polygon.Area) + added;
                    crossings.Add(new(from, to, cost, portal.RequiredCapabilities, portal.Action));
                }
            }
            foreach (NavigationRegionPort port in state.Ports.Span)
            {
                NavigationMesh tile = state.TileMeshes[tileIndices[port.Tile]].Mesh;
                int polygon = tile.FindPolygon(port.Position);
                float height = port.Position.Y;
                if (polygon >= 0) tile.TrySampleHeight(polygon, port.Position, out height);
                Vector3 position = new(port.Position.X, height, port.Position.Z);
                entrances.Add(new(port.Tile, polygon, position, position, position, Port: port.Id));
            }

            int[] order = Enumerable.Range(0, entrances.Count).OrderBy(index => tileIndices[entrances[index].Tile]).ToArray();
            var remap = new int[entrances.Count];
            state.Entrances = new NavigationRegionEntrance[entrances.Count];
            for (int index = 0; index < order.Length; index++)
            {
                remap[order[index]] = index;
                state.Entrances[index] = entrances[order[index]];
            }
            state.Crossings = crossings.Select(edge => edge with { From = remap[edge.From], To = remap[edge.To] }).ToArray();
            state.Tiles = new NavigationRegionTile[state.TileMeshes.Length];
            int entranceStart = 0;
            for (int tile = 0; tile < state.TileMeshes.Length; tile++)
            {
                NavigationMeshTile data = state.TileMeshes[tile];
                int entranceEnd = entranceStart;
                while (entranceEnd < state.Entrances.Length && state.Entrances[entranceEnd].Tile == data.Tile) entranceEnd++;
                state.Tiles[tile] = new(data.Tile, data.Bounds ?? data.Mesh.Bounds, data.Mesh.ContentVersion, entranceStart, entranceEnd - entranceStart);
                entranceStart = entranceEnd;
            }
            ScheduleLocalEdges();

            int AddCrossingPoint(int polygonIndex, Vector3 left, Vector3 right)
            {
                Vector3 midpoint = (left + right) * 0.5f;
                mesh.TrySampleHeight(polygonIndex, midpoint, out float height);
                Vector3 position = new(midpoint.X, height, midpoint.Z);
                if (crossingPoints.TryGetValue((polygonIndex, position), out int existing)) return existing;
                int index = entrances.Count;
                crossingPoints.Add((polygonIndex, position), index);
                entrances.Add(new(mesh.Polygons.Span[polygonIndex].Tile, localPolygons[polygonIndex], position, left, right));
                return index;
            }
        }

        private void ScheduleLocalEdges()
        {
            state.LocalEdges = new NavigationRegionEdge[state.TileMeshes.Length][];
            var jobs = new JobHandle[state.TileMeshes.Length];
            for (int tile = 0; tile < jobs.Length; tile++)
            {
                if (TryReuseLocalEdges(state, tile, out NavigationRegionEdge[] retained))
                {
                    state.LocalEdges[tile] = retained;
                    continue;
                }
                var accesses = new List<JobResourceAccess>
                {
                    NavigationJobAccess.Read(scheduler, state.Profile.Footprint),
                    NavigationJobAccess.Read(scheduler, state.Policy.AreaCosts),
                    NavigationJobAccess.Read(scheduler, state.Policy.ActionCosts),
                    JobResourceAccess.Write(scheduler.GetResourceToken(state.LocalEdges), tile, 1),
                };
                state.TileMeshes[tile].Mesh.AddReadAccesses(scheduler, accesses);
                jobs[tile] = scheduler.Schedule(new BuildLocalEdges(state, tile), accesses.ToArray());
            }
            scheduler.Schedule(new CreateGraph(state),
                [JobResourceAccess.Read(scheduler.GetResourceToken(state.LocalEdges))], scheduler.CombineDependencies(jobs));
        }
    }

    private static bool Available(NavigationPolygon polygon, ulong capabilities, NavigationPolicy policy) =>
        (polygon.RequiredCapabilities & capabilities) == polygon.RequiredCapabilities &&
        float.IsFinite(policy.GetAreaCost(polygon.Area));

    private static bool TryReuseLocalEdges(NavigationRegionBuildState state, int tileIndex, out NavigationRegionEdge[] edges)
    {
        edges = [];
        if (state.Previous is not { } previous) return false;
        NavigationRegionTile tile = state.Tiles[tileIndex];
        foreach (NavigationRegionTile old in previous.Tiles.Span)
        {
            if (old.Tile != tile.Tile || old.ContentVersion != tile.ContentVersion || old.EntranceCount != tile.EntranceCount) continue;
            for (int entrance = 0; entrance < tile.EntranceCount; entrance++)
            {
                NavigationRegionEntrance current = state.Entrances[tile.EntranceStart + entrance];
                NavigationRegionEntrance before = previous.Entrances.Span[old.EntranceStart + entrance];
                if (current with { EdgeStart = 0, EdgeCount = 0 } != before with { EdgeStart = 0, EdgeCount = 0 }) return false;
            }
            int offset = tile.EntranceStart - old.EntranceStart;
            var retained = new List<NavigationRegionEdge>();
            foreach (NavigationRegionEdge edge in previous.Edges.Span)
                if (edge.From >= old.EntranceStart && edge.From < old.EntranceStart + old.EntranceCount &&
                    edge.To >= old.EntranceStart && edge.To < old.EntranceStart + old.EntranceCount)
                    retained.Add(edge with { From = edge.From + offset, To = edge.To + offset });
            edges = retained.ToArray();
            return true;
        }
        return false;
    }

    private readonly struct BuildLocalEdges(NavigationRegionBuildState state, int tileIndex) : IJob
    {
        public void Execute()
        {
            NavigationRegionTile tile = state.Tiles[tileIndex];
            NavigationMesh mesh = state.TileMeshes[tileIndex].Mesh;
            var edges = new List<NavigationRegionEdge>();
            int end = tile.EntranceStart + tile.EntranceCount;
            for (int from = tile.EntranceStart; from < end; from++)
            for (int to = tile.EntranceStart; to < end; to++)
            {
                if (from == to) continue;
                var input = new NavigationSearchInput<NavigationMesh>(mesh, state.Profile, state.Policy,
                    state.Entrances[from].Position, state.Entrances[to].Position);
                PolygonSearchState search = PolygonNavigationSearch.Create(in input);
                NavigationSearchStep step;
                do { step = PolygonNavigationSearch.Advance(search, 256); }
                while (step.Status == NavigationSearchStatus.Searching);
                if (step.Path is { } path)
                    edges.Add(new(from, to, path.Cost));
            }
            state.LocalEdges[tileIndex] = edges.ToArray();
        }
    }

    private readonly struct CreateGraph(NavigationRegionBuildState state) : IJob
    {
        public void Execute()
        {
            NavigationRegionEdge[] edges = state.Crossings.Concat(state.LocalEdges.SelectMany(values => values))
                .OrderBy(edge => edge.From).ThenBy(edge => edge.To).ToArray();
            var entrances = state.Entrances.ToArray();
            int edgeStart = 0;
            for (int entrance = 0; entrance < entrances.Length; entrance++)
            {
                int edgeEnd = edgeStart;
                while (edgeEnd < edges.Length && edges[edgeEnd].From == entrance) edgeEnd++;
                entrances[entrance] = entrances[entrance] with { EdgeStart = edgeStart, EdgeCount = edgeEnd - edgeStart };
                edgeStart = edgeEnd;
            }
            NavigationRegionPort[] ports = entrances.Where(entrance => entrance.Port >= 0)
                .Select(entrance => new NavigationRegionPort(entrance.Port, entrance.Tile, entrance.Position)).ToArray();
            var graph = new NavigationRegionGraph(state.Tiles, entrances, edges, ports, state.Input.ContentVersion);
            state.Mesh = state.Input.WithRegionGraph(graph, state.TileMeshes);
        }
    }
}
