using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public static class HeadingNavigationMeshBuilder
{
    public static NavigationBuild<HeadingNavigationMeshBuildState> Schedule(NavigationBuildInput input,
        NavigationProfile profile, NavigationPolicy policy, HeadingNavigationMeshSettings? settings = null,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        float padding = NavigationSurfaceTraversal.GetRotationRadius(profile.Footprint.Span) + 2 * input.Settings.CellSize;
        var padded = new NavigationBuildInput(input.Sources, input.Region,
            input.Settings with { BorderSize = MathF.Max(input.Settings.BorderSize, padding) });
        NavigationBuild<NavigationSurfaceBuildState> surface = NavigationSurfaceBuilder.Schedule(padded, dependency, scheduler);
        var state = new HeadingNavigationMeshBuildState(profile, policy, settings ?? new());
        return new(state, scheduler.Schedule(new BuildFromSurface(state, surface.State, scheduler), surface.Completion));
    }

    public static NavigationBuild<HeadingNavigationMeshBuildState> Schedule(NavigationSurface surface,
        NavigationProfile profile, NavigationPolicy policy, HeadingNavigationMeshSettings? settings = null,
        JobHandle dependency = default, Scheduler? scheduler = null)
    {
        scheduler ??= JobSystem.Default;
        var state = new HeadingNavigationMeshBuildState(profile, policy, settings ?? new());
        state.Geometry.Surface = surface;
        return new(state, ScheduleTiles(state, scheduler, dependency));
    }

    private readonly struct BuildFromSurface(HeadingNavigationMeshBuildState state, NavigationSurfaceBuildState surface, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            state.Geometry.Surface = surface.Surface;
            ScheduleTiles(state, scheduler, default);
        }
    }

    private static JobHandle ScheduleTiles(HeadingNavigationMeshBuildState state, Scheduler scheduler, JobHandle dependency)
    {
        int tileCount = state.Surface.Tiles.Length, channels = state.Settings.ChannelCount;
        int perGroup = state.Settings.ChannelsPerJob, groupCount = (channels + perGroup - 1) / perGroup;
        state.Fields = new NavigationRecastField[tileCount];
        state.Masks = new NavigationCellOffset[channels][];
        state.Feasibility = new bool[tileCount * channels][];
        state.TileHeadings = new BigInteger[tileCount][];
        state.Geometry.TileData = new NavigationMeshTileData[tileCount];
        var reads = new List<JobResourceAccess>
        {
            NavigationJobAccess.Read(scheduler, state.Profile.Footprint),
            NavigationJobAccess.Read(scheduler, state.Policy.AreaCosts),
        };
        foreach (NavigationSurfaceTile tile in state.Surface.Tiles.Span)
        {
            reads.Add(NavigationJobAccess.Read(scheduler, tile.Columns));
            reads.Add(NavigationJobAccess.Read(scheduler, tile.Spans));
        }
        var masks = new JobHandle[groupCount];
        for (int group = 0; group < groupCount; group++)
        {
            int start = group * perGroup, count = Math.Min(perGroup, channels - start);
            masks[group] = scheduler.Schedule(new BuildMasks(state, start, count),
                [NavigationJobAccess.Read(scheduler, state.Profile.Footprint),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.Masks), start, count)], dependency);
        }
        var fieldJobs = new JobHandle[tileCount];
        var inputs = new JobResourceAccess[reads.Count + 1];
        reads.CopyTo(inputs);
        var built = new JobHandle[tileCount];
        for (int tile = 0; tile < tileCount; tile++)
        {
            inputs[^1] = JobResourceAccess.Write(scheduler.GetResourceToken(state.Fields), tile, 1);
            fieldJobs[tile] = scheduler.Schedule(new BuildField(state, tile), inputs, dependency);
            var groups = new JobHandle[groupCount];
            for (int group = 0; group < groupCount; group++)
            {
                int start = group * perGroup, count = Math.Min(perGroup, channels - start);
                groups[group] = scheduler.Schedule(new EvaluateChannels(state, tile, start, count),
                    [JobResourceAccess.Read(scheduler.GetResourceToken(state.Fields), tile, 1),
                     JobResourceAccess.Read(scheduler.GetResourceToken(state.Masks), start, count),
                     JobResourceAccess.Write(scheduler.GetResourceToken(state.Feasibility), tile * channels + start, count)],
                    scheduler.CombineDependencies([fieldJobs[tile], masks[group]]));
            }
            built[tile] = scheduler.Schedule(new BuildRegions(state, tile),
                [JobResourceAccess.Write(scheduler.GetResourceToken(state.Fields), tile, 1),
                 JobResourceAccess.Read(scheduler.GetResourceToken(state.Feasibility), tile * channels, channels),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.Geometry.TileData), tile, 1),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.TileHeadings), tile, 1)], scheduler.CombineDependencies(groups));
        }
        JobHandle geometry = NavigationMeshBuilder.ScheduleConnections(state.Geometry, scheduler, built, reads);
        return scheduler.Schedule(new CreateMesh(state), JobResourceAccess.Read(scheduler.GetResourceToken(state.TileHeadings)), geometry);
    }

    private readonly struct BuildMasks(HeadingNavigationMeshBuildState state, int start, int count) : IJob
    {
        public void Execute()
        {
            float width = MathF.Tau / state.Settings.ChannelCount;
            for (int channel = start; channel < start + count; channel++)
                state.Masks[channel] = HeadingNavigationFootprint.Rasterize(state.Profile.Footprint.Span,
                    state.Surface.Settings.CellSize, (channel - 0.5f) * width, (channel + 0.5f) * width, state.Settings.MaximumSweepError);
        }
    }

    private readonly struct BuildField(HeadingNavigationMeshBuildState state, int tile) : IJob
    {
        public void Execute() => state.Fields[tile] = NavigationRecast.CreateField(state.Surface,
            state.Surface.Tiles.Span[tile], state.Profile, state.Policy);
    }

    private readonly struct EvaluateChannels(HeadingNavigationMeshBuildState state, int tile, int start, int count) : IJob
    {
        public void Execute()
        {
            for (int channel = start; channel < start + count; channel++)
                state.Feasibility[tile * state.Settings.ChannelCount + channel] =
                    NavigationRecast.EvaluateFootprint(state.Fields[tile], state.Masks[channel], state.Profile);
        }
    }

    private readonly struct BuildRegions(HeadingNavigationMeshBuildState state, int tile) : IJob
    {
        public void Execute()
        {
            NavigationRecastField field = state.Fields[tile];
            int channelCount = state.Settings.ChannelCount;
            var classes = new Dictionary<(int Area, BigInteger Channels), int>();
            var areas = new List<int> { 0 };
            var classHeadings = new List<BigInteger> { BigInteger.Zero };
            for (int span = 0; span < field.Compact.spanCount; span++)
            {
                BigInteger channels = BigInteger.Zero;
                for (int channel = 0; channel < channelCount; channel++)
                    if (state.Feasibility[tile * channelCount + channel][span]) channels |= BigInteger.One << channel;
                if (channels.IsZero) { field.Compact.areas[span] = 0; continue; }
                int area = field.AreaCodes[field.OriginalAreas[span]];
                if (!classes.TryGetValue((area, channels), out int classification))
                {
                    classification = areas.Count;
                    classes.Add((area, channels), classification);
                    areas.Add(area);
                    classHeadings.Add(channels);
                }
                field.Compact.areas[span] = classification;
            }
            state.Geometry.TileData[tile] = NavigationRecast.Polygonize(state.Surface, state.Surface.Tiles.Span[tile],
                field, areas, state.Geometry.Settings, out int[] polygonClasses, preserveSingleCellRegions: true);
            var headings = new BigInteger[polygonClasses.Length];
            for (int polygon = 0; polygon < headings.Length; polygon++) headings[polygon] = classHeadings[polygonClasses[polygon]];
            state.TileHeadings[tile] = headings;
        }
    }

    private readonly struct CreateMesh(HeadingNavigationMeshBuildState state) : IJob
    {
        public void Execute()
        {
            var allowed = new BigInteger[state.Geometry.Mesh.Polygons.Length];
            for (int tile = 0; tile < state.TileHeadings.Length; tile++)
                state.TileHeadings[tile].CopyTo(allowed, state.Geometry.Tiles.Span[tile].PolygonStart);
            state.Mesh = HeadingNavigationGraphBuilder.Create(state.Geometry.Mesh, allowed, state.Settings.ChannelCount);
            state.Fields = [];
            state.Masks = [];
            state.Feasibility = [];
            state.TileHeadings = [];
        }
    }
}
