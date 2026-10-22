using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

/// <summary>Builds profile-specific Recast tiles and installs their complete mesh and adjacency together.</summary>
public static class NavigationMeshBuilder
{
    public static NavigationBuild<NavigationMeshBuildState> Schedule(NavigationBuildInput input,
        NavigationProfile profile, NavigationPolicy policy, NavigationMeshSettings? settings = null,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        float padding = NavigationSurfaceTraversal.GetRotationRadius(profile.Footprint.Span) + 2 * input.Settings.CellSize;
        var padded = new NavigationBuildInput(input.Sources, input.Region,
            input.Settings with { BorderSize = MathF.Max(input.Settings.BorderSize, padding) });
        NavigationBuild<NavigationSurfaceBuildState> surface = NavigationSurfaceBuilder.Schedule(padded, dependency, scheduler);
        var state = new NavigationMeshBuildState(profile, policy, settings ?? new());
        JobHandle completion = scheduler.Schedule(new BuildFromSurface(state, surface.State, scheduler), surface.Completion);
        return new(state, completion);
    }

    public static NavigationBuild<NavigationMeshBuildState> Schedule(NavigationSurface surface,
        NavigationProfile profile, NavigationPolicy policy, NavigationMeshSettings? settings = null,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationMeshBuildState(profile, policy, settings ?? new()) { Surface = surface };
        return new(state, ScheduleTiles(state, scheduler, dependency));
    }

    private readonly struct BuildFromSurface(NavigationMeshBuildState state, NavigationSurfaceBuildState surface, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            state.Surface = surface.Surface;
            ScheduleTiles(state, scheduler, default);
        }
    }

    private static JobHandle ScheduleTiles(NavigationMeshBuildState state, Scheduler scheduler, JobHandle dependency)
    {
        ReadOnlySpan<NavigationSurfaceTile> tiles = state.Surface.Tiles.Span;
        state.TileData = new NavigationMeshTileData[tiles.Length];
        var reads = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, state.Profile.Footprint),
            NavigationJobAccess.Read(scheduler, state.Policy.AreaCosts),
        };
        foreach (NavigationSurfaceTile tile in tiles)
        {
            reads.Add(NavigationJobAccess.Read(scheduler, tile.Columns));
            reads.Add(NavigationJobAccess.Read(scheduler, tile.Spans));
        }
        var accesses = new JobResourceAccess[reads.Count + 1];
        reads.CopyTo(accesses);
        var built = new JobHandle[tiles.Length];
        for (int index = 0; index < tiles.Length; index++)
        {
            accesses[^1] = JobResourceAccess.Write(scheduler.GetResourceToken(state.TileData), index, 1);
            built[index] = scheduler.Schedule(new BuildTile(state, index), accesses, dependency);
        }

        var pairs = new List<(int First, int Second)>();
        for (int first = 0; first < tiles.Length; first++)
        for (int second = first + 1; second < tiles.Length; second++)
        {
            NavigationTileCoordinate a = tiles[first].Coordinate, b = tiles[second].Coordinate;
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z) == 1)
                pairs.Add((first, second));
        }
        var connections = new TileConnection[pairs.Count][];
        var joined = new JobHandle[pairs.Count + built.Length];
        var joinAccesses = new JobResourceAccess[reads.Count + 3];
        reads.CopyTo(joinAccesses);
        built.CopyTo(joined, 0);
        for (int index = 0; index < pairs.Count; index++)
        {
            (int first, int second) = pairs[index];
            joinAccesses[^3] = JobResourceAccess.Read(scheduler.GetResourceToken(state.TileData), first, 1);
            joinAccesses[^2] = JobResourceAccess.Read(scheduler.GetResourceToken(state.TileData), second, 1);
            joinAccesses[^1] = JobResourceAccess.Write(scheduler.GetResourceToken(connections), index, 1);
            joined[built.Length + index] = scheduler.Schedule(new JoinTiles(state, first, second, connections, index),
                joinAccesses, scheduler.CombineDependencies([built[first], built[second]]));
        }
        return scheduler.Schedule(new CreateMesh(state, connections),
            [JobResourceAccess.Read(scheduler.GetResourceToken(state.TileData)),
             JobResourceAccess.Read(scheduler.GetResourceToken(connections))], scheduler.CombineDependencies(joined));
    }

    private readonly struct BuildTile(NavigationMeshBuildState state, int index) : IJob
    {
        public void Execute() => state.TileData[index] = NavigationRecast.BuildTile(
            state.Surface, state.Surface.Tiles.Span[index], state.Profile, state.Policy, state.Settings);
    }

    private readonly record struct TileConnection(int FirstTile, int FirstPolygon, int SecondTile, int SecondPolygon, Vector3 Start, Vector3 End);

    private readonly struct JoinTiles(NavigationMeshBuildState state, int first, int second, TileConnection[][] results, int index) : IJob
    {
        public void Execute()
        {
            var connections = new List<TileConnection>();
            foreach (NavigationMeshBorder a in state.TileData[first].Borders)
            foreach (NavigationMeshBorder b in state.TileData[second].Borders)
            {
                if (TryOverlap(a.Start, a.End, b.Start, b.End, out Vector3 start, out Vector3 end))
                    ConnectWalkableIntervals(a, b, start, end, connections);
            }
            results[index] = connections.ToArray();
        }

        private void ConnectWalkableIntervals(NavigationMeshBorder a, NavigationMeshBorder b,
            Vector3 start, Vector3 end, List<TileConnection> connections)
        {
            float cellSize = state.Surface.Settings.CellSize;
            int axis = MathF.Abs(start.X - end.X) < 0.0001f ? 2 : 0;
            float origin = state.Surface.Bounds.Minimum[axis];
            int lowerCell = (int)MathF.Floor((start[axis] - origin) / cellSize);
            int upperCell = (int)MathF.Ceiling((end[axis] - origin) / cellSize);
            NavigationTileCoordinate firstTile = state.Surface.Tiles.Span[first].Coordinate;
            NavigationTileCoordinate secondTile = state.Surface.Tiles.Span[second].Coordinate;
            Vector3 across = new Vector3(secondTile.X - firstTile.X, 0, secondTile.Z - firstTile.Z) * (cellSize * 0.25f);
            Vector3 openStart = default, openEnd = default;
            bool open = false;
            for (int cell = lowerCell; cell < upperCell; cell++)
            {
                float lower = MathF.Max(start[axis], origin + cell * cellSize);
                float upper = MathF.Min(end[axis], origin + (cell + 1) * cellSize);
                float middle = (lower + upper) * 0.5f;
                Vector3 firstPoint = AlongEdge(a.Start, a.End, middle, axis) - across;
                Vector3 secondPoint = AlongEdge(b.Start, b.End, middle, axis) + across;
                float verticalReach = state.Profile.MaximumStepHeight + 2 * state.Surface.Settings.CellHeight;
                bool walkable = state.Surface.TryGetSupport(firstPoint, verticalReach, out NavigationSupport firstSupport) &&
                    state.Surface.TryGetSupport(secondPoint, verticalReach, out NavigationSupport secondSupport) &&
                    NavigationSurfaceTraversal.CanTraverse(firstSupport, secondSupport, state.Profile);
                if (walkable)
                {
                    if (!open) openStart = AlongEdge(start, end, lower, axis);
                    openEnd = AlongEdge(start, end, upper, axis);
                    open = true;
                }
                else if (open)
                {
                    connections.Add(new(first, a.Polygon, second, b.Polygon, openStart, openEnd));
                    open = false;
                }
            }
            if (open) connections.Add(new(first, a.Polygon, second, b.Polygon, openStart, openEnd));
        }
    }

    private static Vector3 AlongEdge(Vector3 start, Vector3 end, float coordinate, int axis) =>
        Vector3.Lerp(start, end, (coordinate - start[axis]) / (end[axis] - start[axis]));

    private static bool TryOverlap(Vector3 a, Vector3 b, Vector3 c, Vector3 d, out Vector3 start, out Vector3 end)
    {
        start = end = default;
        const float tolerance = 0.0001f;
        bool xAligned = MathF.Abs(a.X - b.X) < tolerance;
        int along = xAligned ? 2 : 0, across = xAligned ? 0 : 2;
        if (MathF.Abs(a[across] - c[across]) > tolerance || MathF.Abs(b[across] - d[across]) > tolerance)
            return false;
        float lower = MathF.Max(MathF.Min(a[along], b[along]), MathF.Min(c[along], d[along]));
        float upper = MathF.Min(MathF.Max(a[along], b[along]), MathF.Max(c[along], d[along]));
        if (upper - lower <= tolerance) return false;
        Vector3 firstA = Vector3.Lerp(a, b, (lower - a[along]) / (b[along] - a[along]));
        Vector3 firstB = Vector3.Lerp(c, d, (lower - c[along]) / (d[along] - c[along]));
        Vector3 lastA = Vector3.Lerp(a, b, (upper - a[along]) / (b[along] - a[along]));
        Vector3 lastB = Vector3.Lerp(c, d, (upper - c[along]) / (d[along] - c[along]));
        start = (firstA + firstB) * 0.5f;
        end = (lastA + lastB) * 0.5f;
        return true;
    }

    internal static NavigationPortal Portal(int target, Vector3 start, Vector3 end, Vector3 from, Vector3 to)
    {
        Vector3 direction = to - from, offset = start - (start + end) * 0.5f;
        return direction.X * offset.Z - direction.Z * offset.X >= 0
            ? new(target, start, end) : new(target, end, start);
    }

    internal static Vector3 Center(NavigationMesh mesh, int index)
    {
        NavigationPolygon polygon = mesh.Polygons.Span[index];
        Vector3 sum = default;
        foreach (int vertex in mesh.PolygonVertices.Span.Slice(polygon.VertexStart, polygon.VertexCount))
            sum += mesh.Positions.Span[vertex];
        return sum / polygon.VertexCount;
    }

    private readonly struct CreateMesh(NavigationMeshBuildState state, TileConnection[][] connections) : IJob
    {
        public void Execute()
        {
            var positions = new List<Vector3>();
            var vertices = new List<int>();
            var polygons = new List<NavigationPolygon>();
            var details = new List<Vector3>();
            var triangles = new List<NavigationDetailTriangle>();
            var portals = new List<List<NavigationPortal>>();
            var ranges = new NavigationMeshTileRange[state.TileData.Length];
            for (int tile = 0; tile < state.TileData.Length; tile++)
            {
                NavigationMesh mesh = state.TileData[tile].Mesh;
                int positionStart = positions.Count, vertexStart = vertices.Count;
                int polygonStart = polygons.Count, detailStart = details.Count, triangleStart = triangles.Count;
                ranges[tile] = new(state.Surface.Tiles.Span[tile].Coordinate, polygonStart, mesh.Polygons.Length);
                positions.AddRange(mesh.Positions.Span);
                details.AddRange(mesh.DetailPositions.Span);
                foreach (int vertex in mesh.PolygonVertices.Span) vertices.Add(vertex + positionStart);
                foreach (NavigationDetailTriangle triangle in mesh.DetailTriangles.Span)
                    triangles.Add(new(triangle.A + detailStart, triangle.B + detailStart, triangle.C + detailStart));
                foreach (NavigationPolygon polygon in mesh.Polygons.Span)
                {
                    polygons.Add(polygon with { VertexStart = polygon.VertexStart + vertexStart, DetailStart = polygon.DetailStart + triangleStart });
                    var directed = new List<NavigationPortal>();
                    foreach (NavigationPortal portal in mesh.Portals.Span.Slice(polygon.PortalStart, polygon.PortalCount))
                        directed.Add(portal with { ToPolygon = portal.ToPolygon + polygonStart });
                    portals.Add(directed);
                }
            }
            foreach (TileConnection[] pair in connections)
            foreach (TileConnection connection in pair)
            {
                int first = ranges[connection.FirstTile].PolygonStart + connection.FirstPolygon;
                int second = ranges[connection.SecondTile].PolygonStart + connection.SecondPolygon;
                Vector3 from = Center(state.TileData[connection.FirstTile].Mesh, connection.FirstPolygon);
                Vector3 to = Center(state.TileData[connection.SecondTile].Mesh, connection.SecondPolygon);
                NavigationPortal forward = Portal(second, connection.Start, connection.End, from, to);
                portals[first].Add(forward);
                portals[second].Add(new(first, forward.Right, forward.Left));
            }
            var combined = new List<NavigationPortal>();
            for (int index = 0; index < polygons.Count; index++)
            {
                polygons[index] = polygons[index] with { PortalStart = combined.Count, PortalCount = portals[index].Count };
                combined.AddRange(portals[index]);
            }
            state.Tiles = ranges;
            state.Mesh = new(positions.ToArray(), vertices.ToArray(), polygons.ToArray(), combined.ToArray(),
                details.ToArray(), triangles.ToArray(), state.Settings.ContentVersion);
            state.TileData = [];
        }
    }
}
