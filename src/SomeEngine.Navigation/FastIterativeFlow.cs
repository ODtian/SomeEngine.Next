using System.Numerics;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public sealed class FastIterativeFlowState(NavigationFlowInput input) : NavigationFlowBuildState(input)
{
    internal bool Started;
    internal int ActiveCount, Processed;
    internal float[] Values { get; } = new float[input.Costs.Grid.Cells.Length];
    internal float[] BoundaryValues { get; } = new float[input.Costs.Grid.Cells.Length];
    internal float[] Candidates { get; } = new float[input.Costs.Grid.Cells.Length];
    internal int[] Goals { get; } = new int[input.Costs.Grid.Cells.Length];
    internal int[] CandidateGoals { get; } = new int[input.Costs.Grid.Cells.Length];
    internal int[] Axes { get; } = new int[input.Costs.Grid.Cells.Length * 4];
    internal int[] Active { get; } = new int[input.Costs.Grid.Cells.Length];
    internal int[] NextActive { get; } = new int[input.Costs.Grid.Cells.Length];
    internal bool[] IsActive { get; } = new bool[input.Costs.Grid.Cells.Length];
    internal int[] Successors { get; } = new int[input.Costs.Grid.Cells.Length];
    internal Vector3[] Directions { get; } = new Vector3[input.Costs.Grid.Cells.Length];
}

/// <summary>Parallel active-set integration of |grad T| = positive scalar cell cost on reciprocal XZ support connections.</summary>
public readonly struct FastIterativeFlow : INavigationFlowSolver<FastIterativeFlowState>
{
    private const int CellsPerJob = 64;
    public static string Id => "fast-iterative";
    public static FastIterativeFlowState Create(NavigationFlowInput input) => new(input);

    public static JobHandle Schedule(Scheduler scheduler, FastIterativeFlowState state, int budget, JobHandle dependency)
    {
        NavigationFlowInput input = state.Input;
        return scheduler.Schedule(new Begin(scheduler, state, budget),
            [JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)),
             NavigationJobAccess.Read(scheduler, input.Costs.Grid.Cells), NavigationJobAccess.Read(scheduler, input.Costs.Grid.Edges),
             NavigationJobAccess.Read(scheduler, input.Costs.CellCosts), NavigationJobAccess.Read(scheduler, input.Costs.EdgeCosts),
             NavigationJobAccess.Read(scheduler, input.Goals), NavigationJobAccess.Write(scheduler, state.Values),
             NavigationJobAccess.Write(scheduler, state.BoundaryValues), NavigationJobAccess.Write(scheduler, state.Goals),
             NavigationJobAccess.Write(scheduler, state.Axes), NavigationJobAccess.Write(scheduler, state.Active),
             NavigationJobAccess.Write(scheduler, state.IsActive)], dependency);
    }

    private readonly struct Begin(Scheduler scheduler, FastIterativeFlowState state, int budget) : IJob
    {
        public void Execute()
        {
            if (state.Status == NavigationFlowStatus.Ready) return;
            if (!state.Started) Initialize(state);
            ScheduleBatch(scheduler, state, budget);
        }
    }

    private static void Initialize(FastIterativeFlowState state)
    {
        state.Started = true;
        Array.Fill(state.Values, float.PositiveInfinity);
        Array.Fill(state.BoundaryValues, float.PositiveInfinity);
        Array.Fill(state.Goals, -1);
        NavigationGrid grid = state.Input.Costs.Grid;
        for (int cell = 0; cell < grid.Cells.Length; cell++)
        {
            state.Axes[cell * 4] = NavigationFlowSampler.FindEdge(state.Input, cell, -1, 0);
            state.Axes[cell * 4 + 1] = NavigationFlowSampler.FindEdge(state.Input, cell, 1, 0);
            state.Axes[cell * 4 + 2] = NavigationFlowSampler.FindEdge(state.Input, cell, 0, -1);
            state.Axes[cell * 4 + 3] = NavigationFlowSampler.FindEdge(state.Input, cell, 0, 1);
        }
        for (int goalIndex = 0; goalIndex < state.Input.Goals.Length; goalIndex++)
        {
            NavigationFlowGoal goal = state.Input.Goals.Span[goalIndex];
            bool seeded = false;
            for (int cell = 0; cell < grid.Cells.Length; cell++)
            {
                if (Vector3.Distance(grid.Cells.Span[cell].Position, goal.Position) > goal.ArrivalRange) continue;
                Seed(state, cell, goalIndex);
                seeded = true;
            }
            if (!seeded)
            {
                int cell = grid.FindCell(goal.Position);
                if (cell >= 0) Seed(state, cell, goalIndex);
            }
        }
        for (int cell = 0; cell < grid.Cells.Length; cell++)
        {
            if (state.BoundaryValues[cell] != 0) continue;
            for (int axis = 0; axis < 4; axis++)
            {
                int edge = state.Axes[cell * 4 + axis];
                if (edge >= 0) Activate(state, grid.Edges.Span[edge].ToCell, state.Active, ref state.ActiveCount);
            }
        }
    }

    private static void Seed(FastIterativeFlowState state, int cell, int goal)
    {
        if (state.Values[cell] == 0 || !float.IsFinite(state.Input.Costs.CellCosts.Span[cell])) return;
        state.BoundaryValues[cell] = state.Values[cell] = 0;
        state.Goals[cell] = goal;
    }

    private static void Activate(FastIterativeFlowState state, int cell, int[] active, ref int count)
    {
        if (state.IsActive[cell] || state.BoundaryValues[cell] == 0) return;
        state.IsActive[cell] = true;
        active[count++] = cell;
    }

    private static void ScheduleBatch(Scheduler scheduler, FastIterativeFlowState state, int budget)
    {
        if (state.ActiveCount == 0) { ScheduleDirections(scheduler, state); return; }
        if (budget <= 0) return;
        int count = Math.Min(state.ActiveCount - state.Processed, budget);
        var jobs = new JobHandle[(count + CellsPerJob - 1) / CellsPerJob];
        for (int job = 0; job < jobs.Length; job++)
        {
            int start = state.Processed + job * CellsPerJob, length = Math.Min(CellsPerJob, state.Processed + count - start);
            jobs[job] = scheduler.Schedule(new UpdateCells(state, start, length),
                [NavigationJobAccess.Read<float>(scheduler, state.Values), NavigationJobAccess.Read<int>(scheduler, state.Goals),
                 NavigationJobAccess.Read<int>(scheduler, state.Axes), NavigationJobAccess.Read<int>(scheduler, state.Active),
                 NavigationJobAccess.Read(scheduler, state.Input.Costs.CellCosts), NavigationJobAccess.Read(scheduler, state.Input.Costs.Grid.Edges),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.Candidates), start, length),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.CandidateGoals), start, length)]);
        }
        scheduler.Schedule(new MergeBatch(scheduler, state, count, budget - count),
            [JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)),
             NavigationJobAccess.Read<float>(scheduler, state.Candidates), NavigationJobAccess.Read<int>(scheduler, state.CandidateGoals),
             NavigationJobAccess.Read<float>(scheduler, state.BoundaryValues), NavigationJobAccess.Read<int>(scheduler, state.Axes),
             NavigationJobAccess.Read(scheduler, state.Input.Costs.Grid.Edges), NavigationJobAccess.Write(scheduler, state.Values),
             NavigationJobAccess.Write(scheduler, state.Goals), NavigationJobAccess.Write(scheduler, state.Active),
             NavigationJobAccess.Write(scheduler, state.NextActive), NavigationJobAccess.Write(scheduler, state.IsActive)],
            scheduler.CombineDependencies(jobs));
    }

    private readonly struct UpdateCells(FastIterativeFlowState state, int start, int count) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Input.Costs.Grid;
            for (int index = start; index < start + count; index++)
            {
                int cell = state.Active[index];
                int x = MinimumNeighbor(state, cell, 0), z = MinimumNeighbor(state, cell, 2);
                float a = x >= 0 ? state.Values[x] : float.PositiveInfinity;
                float b = z >= 0 ? state.Values[z] : float.PositiveInfinity;
                float distance = grid.CellSize * state.Input.Costs.CellCosts.Span[cell];
                float smaller = MathF.Min(a, b), difference = MathF.Abs(a - b);
                state.Candidates[index] = !float.IsFinite(smaller) ? float.PositiveInfinity
                    : difference >= distance ? smaller + distance
                    : (a + b + MathF.Sqrt(2 * distance * distance - difference * difference)) * 0.5f;
                int source = a <= b ? x : z;
                state.CandidateGoals[index] = source >= 0 ? state.Goals[source] : -1;
            }
        }
    }

    private static int MinimumNeighbor(FastIterativeFlowState state, int cell, int firstAxis)
    {
        NavigationGrid grid = state.Input.Costs.Grid;
        int selected = -1;
        float minimum = float.PositiveInfinity;
        for (int axis = firstAxis; axis < firstAxis + 2; axis++)
        {
            int edge = state.Axes[cell * 4 + axis];
            if (edge < 0) continue;
            int neighbor = grid.Edges.Span[edge].ToCell;
            if (state.Values[neighbor] >= minimum) continue;
            minimum = state.Values[neighbor];
            selected = neighbor;
        }
        return selected;
    }

    private readonly struct MergeBatch(Scheduler scheduler, FastIterativeFlowState state, int count, int remainingBudget) : IJob
    {
        public void Execute()
        {
            state.Processed += count;
            if (state.Processed == state.ActiveCount)
            {
                for (int index = 0; index < state.ActiveCount; index++) state.IsActive[state.Active[index]] = false;
                int nextCount = 0;
                for (int index = 0; index < state.ActiveCount; index++)
                {
                    int cell = state.Active[index];
                    if (state.Candidates[index] >= state.Values[cell]) continue;
                    state.Values[cell] = state.Candidates[index];
                    state.Goals[cell] = state.CandidateGoals[index];
                    Activate(state, cell, state.NextActive, ref nextCount);
                    for (int axis = 0; axis < 4; axis++)
                    {
                        int edge = state.Axes[cell * 4 + axis];
                        if (edge >= 0) Activate(state, state.Input.Costs.Grid.Edges.Span[edge].ToCell, state.NextActive, ref nextCount);
                    }
                }
                Array.Copy(state.NextActive, state.Active, nextCount);
                state.ActiveCount = nextCount;
                state.Processed = 0;
            }
            ScheduleBatch(scheduler, state, remainingBudget);
        }
    }

    private static void ScheduleDirections(Scheduler scheduler, FastIterativeFlowState state)
    {
        var jobs = new JobHandle[(state.Values.Length + CellsPerJob - 1) / CellsPerJob];
        for (int job = 0; job < jobs.Length; job++)
        {
            int start = job * CellsPerJob, count = Math.Min(CellsPerJob, state.Values.Length - start);
            jobs[job] = scheduler.Schedule(new BuildDirections(state, start, count),
                [NavigationJobAccess.Read<float>(scheduler, state.Values), NavigationJobAccess.Read<int>(scheduler, state.Axes),
                 NavigationJobAccess.Read(scheduler, state.Input.Costs.Grid.Cells), NavigationJobAccess.Read(scheduler, state.Input.Costs.Grid.Edges),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.Directions), start, count),
                 JobResourceAccess.Write(scheduler.GetResourceToken(state.Successors), start, count)]);
        }
        scheduler.Schedule(new CreateField(state),
            [JobResourceAccess.Exclusive(scheduler.GetResourceToken(state)), NavigationJobAccess.Read(scheduler, state.Input.Costs.Grid.Cells)],
            scheduler.CombineDependencies(jobs));
    }

    private readonly struct BuildDirections(FastIterativeFlowState state, int start, int count) : IJob
    {
        public void Execute()
        {
            NavigationGrid grid = state.Input.Costs.Grid;
            for (int cell = start; cell < start + count; cell++)
            {
                state.Successors[cell] = -1;
                float value = state.Values[cell];
                if (!float.IsFinite(value) || value == 0) continue;
                int x = MinimumNeighbor(state, cell, 0), z = MinimumNeighbor(state, cell, 2);
                Vector3 direction = AxisGradient(grid, state.Values, cell, x) + AxisGradient(grid, state.Values, cell, z);
                if (direction.LengthSquared() > 0) state.Directions[cell] = Vector3.Normalize(direction);
                float minimum = value;
                for (int axis = 0; axis < 4; axis++)
                {
                    int edge = state.Axes[cell * 4 + axis];
                    if (edge < 0 || state.Values[grid.Edges.Span[edge].ToCell] >= minimum) continue;
                    state.Successors[cell] = edge;
                    minimum = state.Values[grid.Edges.Span[edge].ToCell];
                }
            }
        }
    }

    private static Vector3 AxisGradient(NavigationGrid grid, float[] values, int cell, int neighbor) =>
        neighbor >= 0 && values[neighbor] < values[cell]
            ? (grid.Cells.Span[neighbor].Position - grid.Cells.Span[cell].Position) * ((values[cell] - values[neighbor]) / (grid.CellSize * grid.CellSize))
            : Vector3.Zero;

    private readonly struct CreateField(FastIterativeFlowState state) : IJob
    {
        public void Execute()
        {
            NavigationFlowInput input = state.Input;
            NavigationGrid grid = input.Costs.Grid;
            NavigationFlowDependency[] dependencies = grid.Cells.Span.ToArray().Select(cell => cell.Tile).Distinct()
                .Select(tile => new NavigationFlowDependency(tile, grid.ContentVersion, input.Costs.Version, input.GoalVersion)).ToArray();
            state.Field = new(input, state.Values, state.Successors, state.Directions, state.Goals, dependencies, Id, NavigationFlowSampling.Gradient);
            state.Status = NavigationFlowStatus.Ready;
        }
    }
}
