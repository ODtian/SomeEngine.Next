using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public enum NavigationCrowdCostSampling : byte { Directional, Scalar }

/// <summary>Positive speed bounds and density thresholds for a normalized distance/time/discomfort cost multiplier.</summary>
public sealed record NavigationCrowdPolicy
{
    public NavigationCrowdCostSampling Sampling { get; init; } = NavigationCrowdCostSampling.Directional;
    public float FreeSpeed { get; init; } = 3;
    public float MinimumSpeed { get; init; } = 0.1f;
    public float LowDensity { get; init; } = 0.25f;
    public float HighDensity { get; init; } = 1.5f;
    public float DistanceWeight { get; init; } = 1;
    public float TimeWeight { get; init; } = 1;
    public float DiscomfortWeight { get; init; } = 1;

    internal float Multiplier(float density, float flowSpeed)
    {
        float mix = Math.Clamp((density - LowDensity) / (HighDensity - LowDensity), 0, 1);
        float speed = Math.Clamp(FreeSpeed + mix * (MathF.Max(0, flowSpeed) - FreeSpeed), MinimumSpeed, FreeSpeed);
        if (speed == 0) return float.PositiveInfinity;
        float freeCost = DistanceWeight + TimeWeight / FreeSpeed;
        return (DistanceWeight + (TimeWeight + DiscomfortWeight * density) / speed) / freeCost;
    }
}

public static partial class NavigationCostSystem
{
    /// <summary>Applies a completed crowd field to the supplied base costs; repeated frames continue from those base costs.</summary>
    public static NavigationBuild<NavigationCostBuildState> Schedule(NavigationCosts baseCosts,
        NavigationBuild<NavigationCrowdBuildState> crowd, NavigationCrowdPolicy? policy = null,
        ulong? version = null, Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        policy ??= baseCosts.Policy.Crowd ?? new NavigationCrowdPolicy();
        var state = new NavigationCostBuildState(baseCosts.Grid, baseCosts.Policy, version ?? baseCosts.Version + crowd.State.Version);
        return new(state, scheduler.Schedule(new ReadCrowd(state, baseCosts, crowd.State, policy, scheduler),
            JobResourceAccess.Read(scheduler.GetResourceToken(crowd.State)), scheduler.CombineDependencies([crowd.Completion, dependency])));
    }

    /// <summary>Installs one completed cost object into every supplied shared-goal flow.</summary>
    public static JobHandle ScheduleApply(NavigationBuild<NavigationCostBuildState> costs, ReadOnlyMemory<NavigationFlow> flows,
        Scheduler? scheduler = null, JobHandle dependency = default)
    {
        scheduler ??= JobSystem.Default;
        var accesses = new List<JobResourceAccess> { JobResourceAccess.Read(scheduler.GetResourceToken(costs.State)),
            NavigationJobAccess.Read(scheduler, flows) };
        foreach (NavigationFlow flow in flows.Span) accesses.Add(JobResourceAccess.Write(scheduler.GetResourceToken(flow)));
        return scheduler.Schedule(new ApplyCosts(costs.State, flows), accesses.ToArray(),
            scheduler.CombineDependencies([costs.Completion, dependency]));
    }

    private readonly struct ReadCrowd(NavigationCostBuildState state, NavigationCosts baseCosts,
        NavigationCrowdBuildState crowd, NavigationCrowdPolicy policy, Scheduler scheduler) : IJob
    {
        public void Execute()
        {
            NavigationCrowdField field = crowd.Field;
            var accesses = new List<JobResourceAccess>
            {
                NavigationJobAccess.Read(scheduler, baseCosts.CellCosts), NavigationJobAccess.Read(scheduler, baseCosts.EdgeCosts),
                NavigationJobAccess.Read(scheduler, field.Density), NavigationJobAccess.Read(scheduler, field.AverageVelocity),
                NavigationJobAccess.Write(scheduler, state.Cells), NavigationJobAccess.Write(scheduler, state.Edges),
                JobResourceAccess.Write(scheduler.GetResourceToken(state)),
            };
            baseCosts.Grid.AddReadAccesses(scheduler, accesses);
            field.Grid.AddReadAccesses(scheduler, accesses);
            scheduler.Schedule(new BuildCrowdCosts(state, baseCosts, field, policy), accesses.ToArray());
        }
    }

    private readonly struct BuildCrowdCosts(NavigationCostBuildState state, NavigationCosts baseCosts,
        NavigationCrowdField crowd, NavigationCrowdPolicy policy) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = baseCosts.Grid;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
            {
                NavigationGridCell from = grid.Cells.Span[cell];
                float scalar = policy.Sampling == NavigationCrowdCostSampling.Scalar ? ScalarMultiplier(grid, crowd, policy, cell) : 1;
                state.Cells[cell] = baseCosts.CellCosts.Span[cell] * scalar;
                for (int edge = from.EdgeStart; edge < from.EdgeStart + from.EdgeCount; edge++)
                {
                    if (policy.Sampling == NavigationCrowdCostSampling.Scalar)
                    {
                        state.Edges[edge] = baseCosts.EdgeCosts.Span[edge] * scalar;
                        continue;
                    }
                    Vector3 destination = grid.Cells.Span[grid.Edges.Span[edge].ToCell].Position;
                    NavigationCrowdCell sample = crowd.Sample(destination);
                    Vector3 direction = destination - from.Position;
                    if (direction.LengthSquared() > 0) direction = Vector3.Normalize(direction);
                    float multiplier = policy.Multiplier(sample.Density, Vector3.Dot(sample.AverageVelocity, direction));
                    state.Edges[edge] = baseCosts.EdgeCosts.Span[edge] * multiplier;
                }
            }
            state.Costs = new(grid, baseCosts.Policy, state.Cells, state.Edges, state.Version);
        }
    }

    private static float ScalarMultiplier(NavigationGrid grid, NavigationCrowdField crowd, NavigationCrowdPolicy policy, int cell)
    {
        float density = 0, speed = 0;
        int count = 0;
        ReadOnlySpan<NavigationCellOffset> directions = [new(-1, 0), new(1, 0), new(0, -1), new(0, 1)];
        foreach (NavigationCellOffset direction in directions)
        {
            int neighbor = NavigationCrowdSupport.Neighbor(grid, cell, direction.X, direction.Z);
            if (neighbor < 0) continue;
            NavigationCrowdCell sample = crowd.Sample(grid.Cells.Span[neighbor].Position);
            density += sample.Density;
            speed += sample.Density * sample.AverageVelocity.Length();
            count++;
        }
        return count > 0 ? policy.Multiplier(density / count, density > 0 ? speed / density : 0) : 1;
    }

    private readonly struct ApplyCosts(NavigationCostBuildState state, ReadOnlyMemory<NavigationFlow> flows) : IJob
    {
        public void Execute()
        {
            foreach (NavigationFlow flow in flows.Span) flow.SetCosts(state.Costs);
        }
    }
}
