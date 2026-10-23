using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class HeadingNavigationRegionBuildState
{
    public HeadingNavigationMesh Mesh { get; internal set; } = null!;
    public HeadingNavigationRegionGraph Graph => Mesh.RegionGraph!;
    internal HeadingNavigationMesh Input = null!;
    internal NavigationProfile Profile = null!;
    internal NavigationPolicy Policy = null!;
    internal HeadingNavigationMeshTile[] Tiles = [];
    internal NavigationRegionTile[] Directory = [];
    internal HeadingNavigationRegionEntrance[] Entrances = [];
    internal int[] EntranceNodes = [];
    internal HeadingNavigationTilePortal[] Portals = [];
    internal HeadingNavigationRegionEdge[] Crossings = [];
    internal HeadingNavigationRegionEdge[][] LocalEdges = [];
}

/// <summary>Builds per-tile channel entrances and their local heading-graph travel costs in jobs.</summary>
public static class HeadingNavigationRegionBuilder
{
    public static NavigationBuild<HeadingNavigationRegionBuildState> Schedule(HeadingNavigationMesh mesh,
        NavigationProfile profile, NavigationPolicy policy, JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new HeadingNavigationRegionBuildState { Input = mesh, Profile = profile, Policy = policy };
        return new(state, scheduler.Schedule(new BuildTopology(state, scheduler), Reads(state, scheduler), dependency));
    }

    private static JobResourceAccess[] Reads(HeadingNavigationRegionBuildState state, Scheduler scheduler)
    {
        var accesses = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, state.Policy.AreaCosts), NavigationJobAccess.Read(scheduler, state.Policy.ActionCosts),
        };
        state.Input.AddReadAccesses(scheduler, accesses);
        return accesses.ToArray();
    }

    private readonly struct BuildTopology(HeadingNavigationRegionBuildState state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            HeadingNavigationMesh mesh = state.Input;
            state.Tiles = mesh.SeparateTiles();
            var local = new int[mesh.Regions.Length];
            var counts = new Dictionary<NavigationTileCoordinate, int>();
            for (int region = 0; region < local.Length; region++)
            {
                NavigationTileCoordinate tile = mesh.Geometry.Polygons.Span[region].Tile;
                local[region] = counts.GetValueOrDefault(tile);
                counts[tile] = local[region] + 1;
            }
            var portalList = new List<HeadingNavigationTilePortal>();
            for (int region = 0; region < local.Length; region++)
            {
                NavigationPolygon from = mesh.Geometry.Polygons.Span[region];
                foreach (NavigationPortal portal in mesh.Geometry.Portals.Span.Slice(from.PortalStart, from.PortalCount))
                {
                    NavigationTileCoordinate to = mesh.Geometry.Polygons.Span[portal.ToPolygon].Tile;
                    if (to != from.Tile) portalList.Add(new(from.Tile, local[region], to, local[portal.ToPolygon],
                        portal.Left, portal.Right, portal.Cost, portal.RequiredCapabilities, portal.Action));
                }
            }
            state.Portals = portalList.ToArray();
            var entrances = new List<HeadingNavigationRegionEntrance>();
            var nodes = new List<int>();
            var entranceByNode = new Dictionary<int, int>();
            var directory = new List<NavigationRegionTile>();
            foreach (HeadingNavigationMeshTile tile in state.Tiles)
            {
                int first = entrances.Count;
                for (int node = 0; node < mesh.Graph.Nodes.Length; node++)
                {
                    HeadingNavigationNode source = mesh.Graph.Nodes.Span[node];
                    if (mesh.Geometry.Polygons.Span[source.Region].Tile != tile.Tile) continue;
                    bool boundary = false;
                    foreach (HeadingNavigationEdge edge in mesh.Graph.Edges.Span.Slice(source.ConnectionStart, source.ConnectionCount))
                        boundary |= mesh.Geometry.Polygons.Span[mesh.Graph.Nodes.Span[edge.ToNode].Region].Tile != tile.Tile;
                    if (!boundary) continue;
                    entranceByNode.Add(node, entrances.Count);
                    nodes.Add(node);
                    entrances.Add(new(tile.Tile, local[source.Region], source.Position, source.Channel));
                }
                directory.Add(new(tile.Tile, tile.Bounds, tile.Mesh.ContentVersion, first, entrances.Count - first));
            }
            state.Entrances = entrances.ToArray(); state.EntranceNodes = nodes.ToArray(); state.Directory = directory.ToArray();
            var crossings = new List<HeadingNavigationRegionEdge>();
            for (int index = 0; index < nodes.Count; index++)
            {
                HeadingNavigationNode node = mesh.Graph.Nodes.Span[nodes[index]];
                foreach (HeadingNavigationEdge edge in mesh.Graph.Edges.Span.Slice(node.ConnectionStart, node.ConnectionCount))
                    if (entranceByNode.TryGetValue(edge.ToNode, out int to) && entrances[index].Tile != entrances[to].Tile)
                        crossings.Add(new(index, to, EdgeCost(state, nodes[index], edge)));
            }
            state.Crossings = crossings.Where(edge => float.IsFinite(edge.Cost)).ToArray();
            state.LocalEdges = new HeadingNavigationRegionEdge[state.Tiles.Length][];
            var jobs = new JobHandle[state.Tiles.Length];
            for (int tile = 0; tile < jobs.Length; tile++)
            {
                var resources = Reads(state, scheduler).ToList();
                resources.Add(NavigationJobAccess.Read<HeadingNavigationRegionEntrance>(scheduler, state.Entrances));
                resources.Add(NavigationJobAccess.Read<int>(scheduler, state.EntranceNodes));
                resources.Add(JobResourceAccess.Write(scheduler.GetResourceToken(state.LocalEdges), tile, 1));
                jobs[tile] = scheduler.Schedule(new BuildTileCosts(state, tile), resources.ToArray());
            }
            scheduler.Schedule(new Finish(state),
                [NavigationJobAccess.Read<HeadingNavigationRegionEdge[]>(scheduler, state.LocalEdges),
                 NavigationJobAccess.Write(scheduler, state.Entrances)], scheduler.CombineDependencies(jobs));
        }
    }

    private readonly struct BuildTileCosts(HeadingNavigationRegionBuildState state, int tile) : IJob
    {
        public void Execute()
        {
            HeadingNavigationMesh mesh = state.Input;
            NavigationRegionTile description = state.Directory[tile];
            var output = new List<HeadingNavigationRegionEdge>();
            var values = new float[mesh.Graph.Nodes.Length];
            var queue = new PriorityQueue<(int Node, float Cost), float>();
            for (int source = description.EntranceStart; source < description.EntranceStart + description.EntranceCount; source++)
            {
                Array.Fill(values, float.PositiveInfinity); queue.Clear();
                int start = state.EntranceNodes[source]; values[start] = 0; queue.Enqueue((start, 0), 0);
                while (queue.TryDequeue(out var entry, out _))
                {
                    if (entry.Cost != values[entry.Node]) continue;
                    HeadingNavigationNode node = mesh.Graph.Nodes.Span[entry.Node];
                    foreach (HeadingNavigationEdge edge in mesh.Graph.Edges.Span.Slice(node.ConnectionStart, node.ConnectionCount))
                    {
                        if (mesh.Geometry.Polygons.Span[mesh.Graph.Nodes.Span[edge.ToNode].Region].Tile != description.Tile) continue;
                        float cost = entry.Cost + EdgeCost(state, entry.Node, edge);
                        if (cost >= values[edge.ToNode]) continue;
                        values[edge.ToNode] = cost;
                        queue.Enqueue((edge.ToNode, cost), cost);
                    }
                }
                for (int to = description.EntranceStart; to < description.EntranceStart + description.EntranceCount; to++)
                    if (to != source && float.IsFinite(values[state.EntranceNodes[to]])) output.Add(new(source, to, values[state.EntranceNodes[to]]));
            }
            state.LocalEdges[tile] = output.ToArray();
        }
    }

    private readonly struct Finish(HeadingNavigationRegionBuildState state) : IJob
    {
        public void Execute()
        {
            HeadingNavigationRegionEdge[] edges = state.Crossings.Concat(state.LocalEdges.SelectMany(edges => edges)).OrderBy(edge => edge.From).ToArray();
            int offset = 0;
            for (int index = 0; index < state.Entrances.Length; index++)
            {
                int start = offset;
                while (offset < edges.Length && edges[offset].From == index) offset++;
                state.Entrances[index] = state.Entrances[index] with { EdgeStart = start, EdgeCount = offset - start };
            }
            var graph = new HeadingNavigationRegionGraph(state.Directory, state.Entrances, edges, state.Portals, state.Input.ChannelCount, state.Input.ContentVersion);
            state.Mesh = state.Input.WithRegionGraph(graph, state.Tiles);
        }
    }

    private static float EdgeCost(HeadingNavigationRegionBuildState state, int from, HeadingNavigationEdge edge)
    {
        HeadingNavigationMesh mesh = state.Input;
        HeadingNavigationNode a = mesh.Graph.Nodes.Span[from], b = mesh.Graph.Nodes.Span[edge.ToNode];
        NavigationPolygon polygon = mesh.Geometry.Polygons.Span[b.Region];
        ulong capabilities = state.Profile.Capabilities & state.Policy.AllowedCapabilities;
        if ((polygon.RequiredCapabilities & capabilities) != polygon.RequiredCapabilities || !float.IsFinite(state.Policy.GetAreaCost(polygon.Area))) return float.PositiveInfinity;
        if (edge.Action == HeadingNavigationAction.Rotation) return MathF.Tau / mesh.ChannelCount / state.Profile.MaximumAngularSpeed;
        Vector3 forward = NavigationHeading.Direction(mesh.GetChannel(a.Channel).Center), delta = b.Position - a.Position;
        float cost = (MathF.Abs(Vector3.Dot(delta, forward)) + MathF.Abs(Vector3.Dot(delta, new(forward.Z, 0, -forward.X)))) /
            state.Profile.MaximumSpeed * state.Policy.GetAreaCost(mesh.Geometry.Polygons.Span[a.Region].Area);
        if (edge.Portal >= 0)
        {
            NavigationPortal portal = mesh.Geometry.Portals.Span[edge.Portal];
            if ((portal.RequiredCapabilities & capabilities) != portal.RequiredCapabilities) return float.PositiveInfinity;
            cost += portal.Cost + state.Policy.GetActionCost(portal.Action);
        }
        return cost;
    }
}
