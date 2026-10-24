using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationRegionUpdate(NavigationRegionSources region)
{
    public NavigationRegionSources Region { get; } = region;
    public NavigationSourceInput Input { get; internal set; } = null!;
    public ReadOnlyMemory<NavigationTileCoordinate> RebuiltTiles { get; internal set; }
    public NavigationMesh Mesh { get; internal set; } = null!;
    public NavigationSurface Surface { get; internal set; } = null!;
    internal NavigationMeshBuildState MeshBuild { get; set; } = null!;
    internal NavigationRegionUpdate? Previous;
}

public static class NavigationBuildSystem
{
    public static NavigationBuild<NavigationRegionUpdate> Schedule(NavigationBuild<NavigationSourceChanges> sources,
        Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationRegionUpdate(sources.State.Region);
        return new(state, scheduler.Schedule(new BeginBuild(state, sources.State, scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(state.Region)), sources.Completion));
    }

    private readonly struct BeginBuild(NavigationRegionUpdate state, NavigationSourceChanges sources, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            state.Input = sources.Input;
            state.Previous = state.Region.Completed;
            float tileWidth = state.Input.Geometry.Settings.TileSize * state.Input.Geometry.Settings.CellSize;
            int width = (int)MathF.Ceiling((state.Region.Bounds.Maximum.X - state.Region.Bounds.Minimum.X) / tileWidth);
            var changed = new List<NavigationTileCoordinate>();
            for (int index = 0; index < state.Input.TileVersions.Length; index++)
                if (state.Previous is null || index >= state.Previous.Input.TileVersions.Length ||
                    state.Input.TileVersions.Span[index] != state.Previous.Input.TileVersions.Span[index])
                    changed.Add(new(index % width, index / width));
            state.RebuiltTiles = changed.ToArray();
            if (changed.Count == 0)
            {
                state.Surface = state.Previous!.Surface;
                state.MeshBuild = state.Previous.MeshBuild;
                state.Mesh = state.Previous.Mesh;
                return;
            }
            NavigationBuild<NavigationSurfaceBuildState> surface = NavigationSurfaceBuilder.Schedule(state.Input.Geometry,
                state.RebuiltTiles, scheduler: scheduler);
            scheduler.Schedule(new BuildMesh(state, surface.State, scheduler), surface.Completion);
        }
    }

    private readonly struct BuildMesh(NavigationRegionUpdate state, NavigationSurfaceBuildState surface, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationSourceInput input = state.Input;
            float tileWidth = input.Geometry.Settings.TileSize * input.Geometry.Settings.CellSize;
            int width = (int)MathF.Ceiling((state.Region.Bounds.Maximum.X - state.Region.Bounds.Minimum.X) / tileWidth);
            var replacements = surface.Surface.Tiles.ToArray().ToDictionary(tile => tile.Coordinate);
            var tiles = new NavigationSurfaceTile[input.TileVersions.Length];
            for (int index = 0; index < tiles.Length; index++)
            {
                var coordinate = new NavigationTileCoordinate(index % width, index / width);
                tiles[index] = replacements.TryGetValue(coordinate, out NavigationSurfaceTile? tile)
                    ? tile : state.Previous!.Surface.Tiles.Span[index];
            }
            state.Surface = new(input.Geometry.Region, input.Geometry.Settings, tiles);
            NavigationRegionSettings settings = input.Settings;
            NavigationBuild<NavigationMeshBuildState> mesh = NavigationMeshBuilder.ScheduleRebuild(state.Surface,
                settings.Profile, settings.Policy, settings.Mesh with { ContentVersion = input.Version },
                state.Previous?.MeshBuild, input.TileVersions, scheduler);
            state.MeshBuild = mesh.State;
            scheduler.Schedule(new BuildGraph(state, scheduler), mesh.Completion);
        }
    }

    private readonly struct BuildGraph(NavigationRegionUpdate state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationRegionSettings settings = state.Input.Settings;
            NavigationBuild<NavigationRegionBuildState> graph = NavigationRegionBuilder.ScheduleRebuild(state.MeshBuild.Mesh,
                settings.Profile, settings.Policy, settings.Ports, state.Previous?.Mesh.RegionGraph, scheduler);
            scheduler.Schedule(new CompleteBuild(state, graph.State),
                JobResourceAccess.Write(scheduler.GetResourceToken(state)), graph.Completion);
        }
    }

    private readonly struct CompleteBuild(NavigationRegionUpdate state, NavigationRegionBuildState graph) : IJob
    {
        public void Execute() => state.Mesh = graph.Mesh;
    }
}

public static class NavigationApplySystem
{
    public static JobHandle Schedule(NavigationBuild<NavigationRegionUpdate> build, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        return scheduler.Schedule(new ApplyRegion(build.State),
            [JobResourceAccess.Read(scheduler.GetResourceToken(build.State)),
             JobResourceAccess.Exclusive(scheduler.GetResourceToken(build.State.Region)),
             JobResourceAccess.Write(scheduler.GetResourceToken(build.State.Region.Scene))], build.Completion);
    }

    private readonly struct ApplyRegion(NavigationRegionUpdate state) : IJob
    {
        public void Execute()
        {
            NavigationRegionSources region = state.Region;
            NavigationRegionUpdate update = state;
            lock (region.Gate)
            {
                if (state.RebuiltTiles.IsEmpty || !ReferenceEquals(region.Input, state.Input) ||
                    !ReferenceEquals(region.Settings, state.Input.Settings)) return;
                NavigationSceneContent current = region.Scene.Content;
                NavigationSceneRegion[] regions = current.Regions.ToArray();
                int index = Array.FindIndex(regions, item => item.Id == region.Region);
                bool created = index < 0;
                var replacement = new NavigationSceneRegion(region.Region, Matrix4x4.Identity, state.Mesh.RegionGraph!);
                if (created) regions = [.. regions, replacement];
                else regions[index] = replacement;
                var resident = current.Tiles.ToArray().Select(tile => tile.Id).ToHashSet();
                var tiles = current.Tiles.ToArray().Where(tile => tile.Region != region.Region).ToList();
                foreach (NavigationMeshTile tile in state.Mesh.Tiles.Span)
                    if (created || resident.Contains(new(region.Region, tile.Tile)))
                        tiles.Add(new(region.Region, tile.Tile, tile.Mesh));
                NavigationSceneConnection[] connections = current.Connections.ToArray();
                for (int connection = 0; connection < connections.Length; connection++)
                {
                    NavigationSceneConnection value = connections[connection];
                    if (ChangedPort(value.FromRegion, value.FromPort) || ChangedPort(value.ToRegion, value.ToPort))
                        connections[connection] = value with { ContentVersion = value.ContentVersion + 1 };
                }
                region.Scene.ApplyCompleted(new(regions, connections, tiles.ToArray()));
                region.Completed = state;
                state.Previous = null;

                bool ChangedPort(int regionId, int portId)
                {
                    if (regionId != region.Region) return false;
                    foreach (NavigationRegionPort port in update.Mesh.RegionGraph!.Ports.Span)
                        if (port.Id == portId) return update.RebuiltTiles.Span.Contains(port.Tile);
                    return true;
                }
            }
        }
    }
}
