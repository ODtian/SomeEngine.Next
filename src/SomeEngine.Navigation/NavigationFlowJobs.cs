using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

internal partial struct NavigationFlowGuidanceJob<TMotion, TReader>(float deltaTime) : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly float _deltaTime = deltaTime;

    public void Execute([JobResources<FlowFollowerResources>] in FlowFollower follower,
        [JobResources<NavigationAgentResources>] in NavigationAgent agent,
        in TMotion movement, ref NavigationFlowFollowState following, ref NavigationGuidance guidance)
    {
        NavigationMotion motion = TReader.Read(in movement);
        NavigationFlowField? field = follower.Flow.Field;
        guidance = new(default, motion.Heading, field?.Input.Version ?? 0, false);
        if (field is null) return;
        NavigationGrid grid = field.Input.Costs.Grid;
        int cell = grid.FindCell(motion.Position);
        if (cell < 0 || field.GoalIndices.Span[cell] < 0) return;
        int goalIndex = NavigationFlowTargets.Closest(field.Input.Goals.Span, motion.Position, out float regionDistance);
        NavigationFlowGoal goal = field.Input.Goals.Span[goalIndex];
        if (regionDistance <= 0.0001f)
        {
            guidance = new(default, goal.Heading ?? motion.Heading, field.Input.Version, true);
            return;
        }
        if (field.Sampling == NavigationFlowSampling.Gradient)
        {
            float speed = MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed);
            Vector3 move = NavigationFlowSampler.Displacement(field, cell, motion.Position, speed * _deltaTime);
            float facing = move.X == 0 && move.Z == 0 ? motion.Heading : NavigationHeading.FromDirection(move);
            guidance = new(move / _deltaTime, facing, field.Input.Version, true);
            return;
        }
        if (following.FieldVersion != follower.Flow.FieldVersion ||
            cell != following.Cell && cell != following.Destination)
        {
            following = new() { Cell = cell, Destination = -1, FieldVersion = follower.Flow.FieldVersion };
        }
        if (following.Destination >= 0 && Vector3.Distance(motion.Position, grid.Cells.Span[following.Destination].Position) <= 0.0001f)
        {
            following.Cell = following.Destination;
            following.Destination = -1;
        }
        int edge = field.SuccessorEdges.Span[following.Cell];
        if (following.Destination < 0 && edge >= 0)
            following.Destination = grid.Edges.Span[edge].ToCell;
        goal = field.Input.Goals.Span[field.GoalIndices.Span[following.Cell]];
        Vector3 target = following.Destination >= 0 ? grid.Cells.Span[following.Destination].Position : goal.Position;
        Vector3 displacement = target - motion.Position;
        float distance = displacement.Length();
        if (distance <= 0.0001f) return;
        float travel = MathF.Min(MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed) * _deltaTime, distance);
        if (following.Destination < 0)
            travel = MathF.Min(travel, MathF.Max(0, distance - goal.ArrivalRange));
        Vector3 velocity = displacement * (travel / (distance * _deltaTime));
        float heading = displacement.X == 0 && displacement.Z == 0 ? motion.Heading : NavigationHeading.FromDirection(displacement);
        guidance = new(velocity, heading, field.Input.Version, true);
    }
}

internal static class NavigationFlowTargets
{
    internal static int Closest(ReadOnlySpan<NavigationFlowGoal> goals, Vector3 position, out float distance)
    {
        int closest = -1;
        distance = float.PositiveInfinity;
        for (int index = 0; index < goals.Length; index++)
        {
            float current = MathF.Max(0, Vector3.Distance(position, goals[index].Position) - goals[index].ArrivalRange);
            if (current >= distance) continue;
            distance = current;
            closest = index;
        }
        return closest;
    }
}

internal partial struct NavigationFlowProgressJob<TMotion, TReader> : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public void Execute([JobResources<FlowFollowerResources>] in FlowFollower follower,
        in TMotion movement, ref NavigationProgress progress)
    {
        NavigationFlowField? field = follower.Flow.Field;
        if (field is null)
        {
            progress = new(NavigationStatus.Searching, float.PositiveInfinity, 0, 0);
            return;
        }
        NavigationMotion motion = TReader.Read(in movement);
        int goalIndex = NavigationFlowTargets.Closest(field.Input.Goals.Span, motion.Position, out float distance);
        if (goalIndex < 0)
        {
            progress = new(NavigationStatus.TargetUnavailable, distance, 0, field.Input.Version);
            return;
        }
        NavigationFlowGoal goal = field.Input.Goals.Span[goalIndex];
        float headingError = goal.Heading is { } heading ? MathF.Abs(NavigationHeading.Difference(heading, motion.Heading)) : 0;
        int cell = field.Input.Costs.Grid.FindCell(motion.Position);
        NavigationStatus status = distance <= 0.0001f
            ? headingError <= goal.HeadingTolerance ? NavigationStatus.Arrived : NavigationStatus.Aligning
            : cell >= 0 && float.IsFinite(field.CumulativeCosts.Span[cell]) ? NavigationStatus.Moving : NavigationStatus.Unreachable;
        progress = new(status, distance, headingError, field.Input.Version);
    }
}

internal partial struct NavigationFlowStopJob : IJobEntity
{
    public void Execute(ref NavigationFlowFollowState following, ref NavigationGuidance guidance,
        ref NavigationSteering steering, ref NavigationProgress progress)
    {
        following = default;
        guidance = default;
        steering = default;
        progress = new(NavigationStatus.Idle, 0, 0, 0);
    }
}
