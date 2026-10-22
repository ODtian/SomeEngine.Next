using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationSceneBuildState
{
    public NavigationScene Scene { get; internal set; } = null!;
    internal ReadOnlyMemory<NavigationRegionPlacement> Placements { get; }
    internal ReadOnlyMemory<NavigationSceneConnection> Connections { get; }
    internal NavigationProfile Profile { get; }
    internal NavigationPolicy Policy { get; }
    internal NavigationMesh[] RegionMeshes { get; }

    internal NavigationSceneBuildState(ReadOnlyMemory<NavigationRegionPlacement> placements,
        ReadOnlyMemory<NavigationSceneConnection> connections, NavigationProfile profile, NavigationPolicy policy)
    {
        Placements = placements;
        Connections = connections;
        Profile = profile;
        Policy = policy;
        RegionMeshes = new NavigationMesh[placements.Length];
    }
}

/// <summary>Places detailed geometry and computes matching entrance costs in world coordinates.</summary>
public static class NavigationSceneBuilder
{
    public static NavigationBuild<NavigationSceneBuildState> Schedule(NavigationRegionPlacement[] placements,
        NavigationSceneConnection[] connections, NavigationProfile profile, NavigationPolicy policy,
        JobHandle dependency = default, Scheduler? scheduler = null) =>
        Schedule((ReadOnlyMemory<NavigationRegionPlacement>)placements, connections, profile, policy, dependency, scheduler);

    public static NavigationBuild<NavigationSceneBuildState> Schedule(ReadOnlyMemory<NavigationRegionPlacement> placements,
        ReadOnlyMemory<NavigationSceneConnection> connections, NavigationProfile profile, NavigationPolicy policy,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new NavigationSceneBuildState(placements, connections, profile, policy);
        var jobs = new JobHandle[placements.Length];
        for (int index = 0; index < jobs.Length; index++)
        {
            NavigationMesh mesh = placements.Span[index].Mesh;
            var accesses = new List<JobResourceAccess> { NavigationJobAccess.Read(scheduler, placements) };
            mesh.AddReadAccesses(scheduler, accesses);
            if (mesh.RegionGraph is { } graph) accesses.Add(NavigationJobAccess.Read(scheduler, graph.Ports));
            jobs[index] = scheduler.Schedule(new PlaceRegion(state, index, scheduler), accesses.ToArray(), dependency);
        }
        JobHandle completion = scheduler.Schedule(new CreateScene(state),
            [NavigationJobAccess.Read(scheduler, connections),
             JobResourceAccess.Read(scheduler.GetResourceToken(state.RegionMeshes))], scheduler.CombineDependencies(jobs));
        return new(state, completion);
    }

    private readonly struct PlaceRegion(NavigationSceneBuildState state, int index, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationRegionPlacement placement = state.Placements.Span[index];
            NavigationMesh mesh = placement.Mesh.Transform(placement.Transform);
            ReadOnlyMemory<NavigationRegionPort> sourcePorts = placement.Mesh.RegionGraph?.Ports ?? default;
            var ports = new NavigationRegionPort[sourcePorts.Length];
            for (int port = 0; port < ports.Length; port++)
                ports[port] = sourcePorts.Span[port] with { Position = Vector3.Transform(sourcePorts.Span[port].Position, placement.Transform) };
            NavigationBuild<NavigationRegionBuildState> region = NavigationRegionBuilder.Schedule(mesh,
                state.Profile, state.Policy, ports, scheduler: scheduler);
            scheduler.Schedule(new SetRegion(state, index, region.State),
                [JobResourceAccess.Write(scheduler.GetResourceToken(state.RegionMeshes), index, 1)], region.Completion);
        }
    }

    private readonly struct SetRegion(NavigationSceneBuildState state, int index, NavigationRegionBuildState region) : IJob
    {
        public void Execute() => state.RegionMeshes[index] = region.Mesh;
    }

    private readonly struct CreateScene(NavigationSceneBuildState state) : IJob
    {
        public void Execute()
        {
            var regions = new NavigationSceneRegion[state.Placements.Length];
            var tiles = new List<NavigationSceneTile>();
            for (int index = 0; index < regions.Length; index++)
            {
                NavigationRegionPlacement placement = state.Placements.Span[index];
                NavigationMesh mesh = state.RegionMeshes[index];
                regions[index] = new(placement.Id, placement.Transform, mesh.RegionGraph!);
                foreach (NavigationMeshTile tile in mesh.Tiles.Span)
                    tiles.Add(new(placement.Id, tile.Tile, tile.Mesh));
            }
            state.Scene = new(new(regions, state.Connections, tiles.ToArray()));
        }
    }
}
