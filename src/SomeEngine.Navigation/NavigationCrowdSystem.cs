using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class NavigationCrowdBuildState(NavigationGrid grid, float densityExponent, ulong version)
{
    public NavigationGrid Grid { get; } = grid;
    public float DensityExponent { get; } = densityExponent;
    public ulong Version { get; } = version;
    public NavigationCrowdField Field { get; internal set; } = null!;
    internal List<NavigationCrowdSample> Collected { get; } = [];
    internal NavigationCrowdSample[] Samples = [];
    internal NavigationCrowdContribution[][] Partitions = [];
    internal NavigationCrowdContribution[] Contributions = [];
    internal NavigationCrowdRange[] Ranges = [];
    internal float[] Density { get; } = new float[grid.Cells.Length];
    internal Vector3[] Velocity { get; } = new Vector3[grid.Cells.Length];
}

/// <summary>Collects completed actual motion, splats disjoint sample partitions, then assigns one writer to each support cell.</summary>
public sealed class NavigationCrowdSystem<TMotion, TReader>
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly World _world;
    private readonly Scheduler _scheduler;
    private readonly QueryDefinition _actors;
    private readonly float _densityExponent;
    private JobHandle _completion;
    private ulong _version;
    public NavigationCrowdSystem(World world, Scheduler? scheduler = null, float densityExponent = 2)
    {
        _world = world;
        _scheduler = scheduler ?? WorldJobs.GetScheduler(world);
        _densityExponent = densityExponent;
        _actors = world.QueryDefinition().All<TMotion>().Enabled<NavigationCrowdAgent>().Build();
    }

    public NavigationBuild<NavigationCrowdBuildState> Schedule(NavigationGrid grid, JobHandle motionReady = default)
    {
        var state = new NavigationCrowdBuildState(grid, _densityExponent, ++_version);
        JobHandle ready = _scheduler.CombineDependencies([_completion, motionReady]);
        JobHandle collected = new CollectNavigationCrowd<TMotion, TReader>(state.Collected).Schedule(_world,
            [JobResourceAccess.Write(_scheduler.GetResourceToken(state.Collected))], new JobEntityScheduleOptions(filter: _actors), ready);
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Write(_scheduler.GetResourceToken(state.Collected)) };
        grid.AddReadAccesses(_scheduler, accesses);
        _completion = _scheduler.Schedule(new Partition(state, _scheduler), accesses.ToArray(), collected);
        return new(state, _completion);
    }

    private readonly struct Partition(NavigationCrowdBuildState state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Grid;
            var groups = new List<NavigationCrowdSample>[grid.FlowRanges.Length];
            for (int tile = 0; tile < groups.Length; tile++) groups[tile] = [];
            foreach (NavigationCrowdSample sample in state.Collected)
            {
                int cell = grid.FindCell(sample.Motion.Position);
                if (cell >= 0 && grid.IsDetailedCell(cell)) groups[grid.CellTiles.Span[cell]].Add(sample with { Cell = cell });
            }
            state.Samples = groups.SelectMany(group => group).ToArray();
            var ranges = new List<NavigationCrowdRange>();
            int first = 0;
            foreach (List<NavigationCrowdSample> group in groups)
            {
                for (int offset = 0; offset < group.Count; offset += 64) ranges.Add(new(first + offset, Math.Min(64, group.Count - offset)));
                first += group.Count;
            }
            state.Collected.Clear();
            state.Partitions = new NavigationCrowdContribution[ranges.Count][];
            var jobs = new JobHandle[ranges.Count];
            for (int partition = 0; partition < ranges.Count; partition++)
            {
                NavigationCrowdRange range = ranges[partition];
                NavigationCrowdContribution[] output = state.Partitions[partition] = new NavigationCrowdContribution[range.Count * 4];
                var accesses = new List<JobResourceAccess>
                {
                    JobResourceAccess.Read(scheduler.GetResourceToken(state.Samples), range.Start, range.Count),
                    NavigationJobAccess.Write(scheduler, output),
                };
                grid.AddReadAccesses(scheduler, accesses);
                jobs[partition] = scheduler.Schedule(new Splat(state, range, output), accesses.ToArray());
            }
            var reads = new JobResourceAccess[state.Partitions.Length + 1];
            for (int index = 0; index < state.Partitions.Length; index++) reads[index] = NavigationJobAccess.Read<NavigationCrowdContribution>(scheduler, state.Partitions[index]);
            reads[^1] = NavigationJobAccess.Read(scheduler, grid.FlowRanges);
            scheduler.Schedule(new GroupCells(state, scheduler), reads, scheduler.CombineDependencies(jobs));
        }
    }

    private readonly struct Splat(NavigationCrowdBuildState state, NavigationCrowdRange range, NavigationCrowdContribution[] output) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Grid;
            for (int index = 0; index < range.Count; index++)
            {
                NavigationCrowdSample sample = state.Samples[range.Start + index];
                Vector3 position = sample.Motion.Position;
                float alongX = (position.X - grid.Bounds.Minimum.X) / grid.CellSize - 0.5f;
                float alongZ = (position.Z - grid.Bounds.Minimum.Z) / grid.CellSize - 0.5f;
                int x = (int)MathF.Floor(alongX), z = (int)MathF.Floor(alongZ);
                float dx = alongX - x, dz = alongZ - z;
                for (int corner = 0; corner < 4; corner++)
                {
                    int offsetX = corner & 1, offsetZ = corner >> 1;
                    float weight = MathF.Pow(MathF.Min(offsetX == 0 ? 1 - dx : dx, offsetZ == 0 ? 1 - dz : dz), state.DensityExponent) * sample.Weight;
                    if (weight <= 0) continue;
                    int cell = NavigationCrowdSupport.Find(grid, sample.Cell, new(x + offsetX, z + offsetZ));
                    if (cell >= 0) output[index * 4 + corner] = new(cell, weight, sample.Motion.Velocity * weight);
                }
            }
        }
    }

    private readonly struct GroupCells(NavigationCrowdBuildState state, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            var counts = new int[state.Grid.Cells.Length];
            foreach (NavigationCrowdContribution[] partition in state.Partitions)
                foreach (NavigationCrowdContribution contribution in partition)
                    if (contribution.Density > 0) counts[contribution.Cell]++;
            state.Ranges = new NavigationCrowdRange[counts.Length];
            int total = 0;
            for (int cell = 0; cell < counts.Length; cell++)
            {
                state.Ranges[cell] = new(total, counts[cell]);
                total += counts[cell];
                counts[cell] = 0;
            }
            state.Contributions = new NavigationCrowdContribution[total];
            foreach (NavigationCrowdContribution[] partition in state.Partitions)
                foreach (NavigationCrowdContribution contribution in partition)
                    if (contribution.Density > 0)
                        state.Contributions[state.Ranges[contribution.Cell].Start + counts[contribution.Cell]++] = contribution;
            var jobs = new JobHandle[state.Grid.FlowRanges.Length];
            for (int tile = 0; tile < jobs.Length; tile++)
            {
                NavigationGridTileRange range = state.Grid.FlowRanges.Span[tile];
                jobs[tile] = scheduler.Schedule(new MergeCells(state, range.CellStart, range.CellCount),
                    [NavigationJobAccess.Read<NavigationCrowdContribution>(scheduler, state.Contributions),
                     JobResourceAccess.Read(scheduler.GetResourceToken(state.Ranges), range.CellStart, range.CellCount),
                     JobResourceAccess.Write(scheduler.GetResourceToken(state.Density), range.CellStart, range.CellCount),
                     JobResourceAccess.Write(scheduler.GetResourceToken(state.Velocity), range.CellStart, range.CellCount)]);
            }
            scheduler.Schedule(new CompleteField(state), JobResourceAccess.Write(scheduler.GetResourceToken(state)), scheduler.CombineDependencies(jobs));
        }
    }

    private readonly struct MergeCells(NavigationCrowdBuildState state, int start, int count) : IJob
    {
        public void Execute()
        {
            for (int cell = start; cell < start + count; cell++)
            {
                NavigationCrowdRange range = state.Ranges[cell];
                float density = 0;
                Vector3 momentum = default;
                foreach (NavigationCrowdContribution value in state.Contributions.AsSpan(range.Start, range.Count))
                {
                    density += value.Density;
                    momentum += value.Momentum;
                }
                state.Density[cell] = density;
                state.Velocity[cell] = density > 0 ? momentum / density : Vector3.Zero;
            }
        }
    }

    private readonly struct CompleteField(NavigationCrowdBuildState state) : IJob
    {
        public void Execute() => state.Field = new(state.Grid, state.Density, state.Velocity, state.Version);
    }
}

internal partial struct CollectNavigationCrowd<TMotion, TReader>(List<NavigationCrowdSample> samples) : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly List<NavigationCrowdSample> _samples = samples;
    public void Execute(in NavigationCrowdAgent crowd, in TMotion movement)
    {
        NavigationMotion motion = TReader.Read(in movement);
        if (motion.Grounded) _samples.Add(new(motion, crowd.Weight));
    }
}
