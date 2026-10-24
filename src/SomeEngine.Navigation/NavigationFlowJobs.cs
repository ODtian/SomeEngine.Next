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
        [JobResources<NavigationAgentResources>] ref NavigationAgent agent,
        in TMotion movement, in NavigationTraversalRequest traversal,
        ref NavigationFlowFollowState following, ref NavigationGuidance guidance)
    {
        NavigationMotion motion = TReader.Read(in movement);
        NavigationFlowField? field = follower.Flow.Field;
        if (traversal.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing ||
            traversal.Status == NavigationTraversalStatus.Failed && traversal.PathVersion == field?.Input.Version)
        {
            guidance = new(default, motion.Heading, field?.Input.Version ?? 0, false);
            return;
        }
        if (follower.Flow.GetStatus(motion.Position) == NavigationFlowStatus.Searching)
        {
            guidance = new(default, motion.Heading, follower.Flow.Input.Version, false);
            return;
        }
        guidance = new(default, motion.Heading, field?.Input.Version ?? 0, false);
        if (field is null) return;
        agent = agent with { Profile = field.Input.Profile, Policy = field.Input.Costs.Policy };
        if (field.Input.HeadingGraph is { } headingGraph)
        {
            guidance = HeadingNavigationFlowGuidance.Create(headingGraph, field, follower.Flow.FieldVersion, agent,
                motion, _deltaTime, ref following);
            return;
        }
        NavigationGrid grid = field.Input.Costs.Grid;
        int cell = grid.FindCell(motion.Position);
        if (cell < 0 || !grid.IsDetailedCell(cell) || field.GoalIndices.Span[cell] < 0) return;
        int goalIndex = NavigationFlowTargets.Closest(field.Input.Goals.Span, motion.Position, out float regionDistance);
        NavigationFlowGoal goal = field.Input.Goals.Span[goalIndex];
        if (regionDistance <= 0.0001f)
        {
            guidance = new(default, goal.Heading ?? motion.Heading, field.Input.Version, true);
            return;
        }
        int boundaryEdge = field.SuccessorEdges.Span[cell];
        if (boundaryEdge >= 0 && !grid.IsDetailedCell(grid.Edges.Span[boundaryEdge].ToCell))
        {
            Vector3 frontier = grid.Cells.Span[cell].Position - motion.Position;
            float length = frontier.Length();
            if (length > 0.0001f)
            {
                float frontierTravel = MathF.Min(length, MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed) * _deltaTime);
                guidance = new(frontier * (frontierTravel / (length * _deltaTime)), NavigationHeading.FromDirection(frontier), field.Input.Version, true);
            }
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
            following = new() { Cell = cell, Destination = -1, Edge = -1, FieldVersion = follower.Flow.FieldVersion,
                TraversalSequence = following.TraversalSequence };
        }
        if (following.Destination >= 0 && Vector3.Distance(motion.Position, grid.Cells.Span[following.Destination].Position) <= 0.0001f)
        {
            following.Cell = following.Destination;
            following.Destination = -1;
        }
        int edge = field.SuccessorEdges.Span[following.Cell];
        following.Edge = edge;
        if (following.Destination < 0 && edge >= 0)
            following.Destination = grid.Edges.Span[edge].ToCell;
        if (edge >= 0 && grid.Edges.Span[edge].Action != 0)
        {
            Vector3 entry = grid.Cells.Span[following.Cell].Position - motion.Position;
            float length = entry.Length();
            if (length > 0.0001f)
            {
                float distanceToEntry = MathF.Min(length, MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed) * _deltaTime);
                guidance = new(entry * (distanceToEntry / (length * _deltaTime)), NavigationHeading.FromDirection(entry), field.Input.Version, true);
            }
            return;
        }
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
        in TMotion movement, in NavigationTraversalRequest traversal, ref NavigationProgress progress)
    {
        NavigationMotion motion = TReader.Read(in movement);
        if (traversal.Status is NavigationTraversalStatus.Waiting or NavigationTraversalStatus.Requested or
            NavigationTraversalStatus.Executing or NavigationTraversalStatus.Failed)
        {
            NavigationFlowTargets.Closest(follower.Flow.Goals.Span, motion.Position, out float remaining);
            NavigationStatus actionStatus = traversal.Status switch
            {
                NavigationTraversalStatus.Waiting => NavigationStatus.WaitingForPassage,
                NavigationTraversalStatus.Failed => NavigationStatus.TraversalFailed,
                _ => NavigationStatus.Traversing,
            };
            progress = new(actionStatus, remaining, 0, traversal.PathVersion);
            return;
        }
        NavigationFlowField? field = follower.Flow.Field;
        if (field is null)
        {
            progress = new(NavigationStatus.Searching, float.PositiveInfinity, 0, follower.Flow.Input.Version);
            return;
        }
        if (follower.Flow.GetStatus(motion.Position) == NavigationFlowStatus.Searching)
        {
            NavigationFlowTargets.Closest(follower.Flow.Input.Goals.Span, motion.Position, out float remaining);
            progress = new(NavigationStatus.Searching, remaining, 0, follower.Flow.Input.Version);
            return;
        }
        int goalIndex = NavigationFlowTargets.Closest(field.Input.Goals.Span, motion.Position, out float distance);
        if (goalIndex < 0)
        {
            progress = new(NavigationStatus.TargetUnavailable, distance, 0, field.Input.Version);
            return;
        }
        NavigationFlowGoal goal = field.Input.Goals.Span[goalIndex];
        float headingError = goal.Heading is { } heading ? MathF.Abs(NavigationHeading.Difference(heading, motion.Heading)) : 0;
        int cell = field.Input.HeadingGraph is { } headingGraph
            ? headingGraph.FindState(field, new(motion.Position, motion.Heading)) : field.Input.Costs.Grid.FindCell(motion.Position);
        NavigationGrid grid = field.Input.Costs.Grid;
        int edge = cell >= 0 ? field.SuccessorEdges.Span[cell] : -1;
        bool waiting = cell >= 0 && (!grid.IsDetailedCell(cell) || edge >= 0 && !grid.IsDetailedCell(grid.Edges.Span[edge].ToCell) &&
            Vector3.DistanceSquared(motion.Position, grid.Cells.Span[cell].Position) <= 0.000001f);
        NavigationStatus status = distance <= 0.0001f
            ? headingError <= goal.HeadingTolerance ? NavigationStatus.Arrived : NavigationStatus.Aligning
            : waiting ? NavigationStatus.WaitingForTiles
            : cell >= 0 && float.IsFinite(field.CumulativeCosts.Span[cell]) ? NavigationStatus.Moving : NavigationStatus.Unreachable;
        progress = new(status, distance, headingError, field.Input.Version);
    }
}

internal partial struct NavigationFlowStopJob : IJobEntity
{
    public void Execute(ref NavigationFlowFollowState following, ref NavigationGuidance guidance,
        ref NavigationSteering steering, ref NavigationProgress progress, ref NavigationTraversalRequest traversal,
        [JobResources<NavigationPassageReference>] ref NavigationPassageUse passage)
    {
        following = new() { TraversalSequence = following.TraversalSequence };
        guidance = default;
        steering = default;
        progress = new(NavigationStatus.Idle, 0, 0, 0);
        if (traversal.Status is NavigationTraversalStatus.Waiting or NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
            traversal = traversal with { Status = NavigationTraversalStatus.Cancelled };
        passage = default;
    }
}
