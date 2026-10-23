using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Systems;

namespace SomeEngine.Navigation;

internal static class HeadingNavigationFlowGuidance
{
    internal static NavigationGuidance Create(HeadingNavigationFlowGraph graph, NavigationFlowField field, long fieldVersion,
        NavigationAgent agent, NavigationMotion motion, float deltaTime, ref NavigationFlowFollowState following)
    {
        var pose = new NavigationPose(motion.Position, motion.Heading);
        if (following.FieldVersion != fieldVersion)
            following = new() { Cell = graph.FindState(field, pose), Destination = -1, Edge = -1,
                FieldVersion = fieldVersion, TraversalSequence = following.TraversalSequence };
        NavigationFlowSample sample;
        if (following.Edge >= 0 && following.Destination >= 0)
        {
            HeadingNavigationFlowTransition transition = graph.Transitions.Span[following.Edge];
            NavigationPose target = graph.Nodes.Span[following.Destination].Pose;
            bool atPosition = Vector3.DistanceSquared(pose.Position, target.Position) <= 0.000001f;
            bool completed = transition.Action switch
            {
                NavigationFlowAction.Move => atPosition,
                NavigationFlowAction.Rotate => atPosition && MathF.Abs(NavigationHeading.Difference(target.Heading, pose.Heading)) <= 0.001f,
                _ => false,
            };
            if (completed)
            {
                following.Cell = following.Destination;
                following.Destination = -1;
                following.Edge = -1;
                sample = graph.Sample(field, pose, ref following.Cell);
            }
            else
            {
                if (transition.Action == NavigationFlowAction.Move) target = target with { Heading = graph.Nodes.Span[following.Cell].Pose.Heading };
                sample = new(transition.Action, target, following.Cell, following.Edge, transition.Traversal);
            }
        }
        else sample = graph.Sample(field, pose, ref following.Cell);
        following.Edge = sample.Edge;
        following.Destination = sample.Edge >= 0 ? field.Input.Costs.Grid.Edges.Span[sample.Edge].ToCell : -1;
        if (sample.Action is NavigationFlowAction.Traversal or NavigationFlowAction.Unreachable)
            return new(default, motion.Heading, field.Input.Version, false);
        float turn = Math.Clamp(NavigationHeading.Difference(sample.Target.Heading, motion.Heading),
            -agent.Profile.MaximumAngularSpeed * deltaTime, agent.Profile.MaximumAngularSpeed * deltaTime);
        float heading = motion.Heading + turn;
        if (sample.Action is NavigationFlowAction.Rotate or NavigationFlowAction.Arrived)
            return new(default, heading, field.Input.Version, true);
        Vector3 displacement = sample.Target.Position - motion.Position;
        float distance = displacement.Length();
        float travel = MathF.Min(distance, MathF.Min(agent.DesiredSpeed, agent.Profile.MaximumSpeed) * deltaTime);
        if (sample.Edge < 0 && field.SuccessorEdges.Span[following.Cell] < 0)
            travel = MathF.Min(travel, MathF.Max(0, distance - field.Input.Goals.Span[field.GoalIndices.Span[following.Cell]].ArrivalRange));
        Vector3 velocity = distance > 0 ? displacement * (travel / (distance * deltaTime)) : Vector3.Zero;
        return new(velocity, heading, field.Input.Version, true);
    }
}

internal partial struct NavigationFlowTraversalJob<TMotion, TReader> : IJobEntity
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public void Execute([JobResources<FlowFollowerResources>] in FlowFollower follower, in TMotion movement,
        in NavigationTraversalResult execution, ref NavigationFlowFollowState following,
        ref NavigationTraversalRequest traversal, ref NavigationGuidance guidance,
        [JobResources<NavigationPassageReference>] ref NavigationPassageUse passage)
    {
        NavigationMotion motion = TReader.Read(in movement);
        if (traversal.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
        {
            guidance = new(default, motion.Heading, traversal.PathVersion, false);
            if (execution.RequestId != traversal.RequestId) return;
            if (execution.Status == NavigationTraversalExecution.Completed && Vector3.Distance(motion.Position, traversal.Exit) <= traversal.ExitRange)
            {
                traversal = traversal with { Status = NavigationTraversalStatus.Completed };
                following.FieldVersion = 0;
                following.Edge = -1;
                following.Destination = -1;
                passage = default;
            }
            else if (execution.Status == NavigationTraversalExecution.Failed)
            {
                traversal = traversal with { Status = NavigationTraversalStatus.Failed };
                passage = default;
            }
            else if (execution.Status == NavigationTraversalExecution.Running)
                traversal = traversal with { Status = NavigationTraversalStatus.Executing };
            return;
        }
        NavigationFlowField? field = follower.Flow.Field;
        if (traversal.PathVersion != field?.Input.Version)
        {
            traversal = default;
            passage = default;
        }
        if (traversal.Status == NavigationTraversalStatus.Failed) return;
        if (field is null || follower.Flow.GetStatus(motion.Position) != NavigationFlowStatus.Ready || following.Edge < 0)
        {
            if (traversal.Status == NavigationTraversalStatus.Waiting) { traversal = default; passage = default; }
            return;
        }
        NavigationPathTraversal? selected;
        if (field.Input.HeadingGraph is { } heading)
            selected = heading.Transitions.Span[following.Edge].Traversal;
        else
        {
            NavigationGrid grid = field.Input.Costs.Grid;
            NavigationGridEdge edge = grid.Edges.Span[following.Edge];
            selected = edge.Action != 0 ? new(following.Edge, edge.Action, 0, 0, grid.Cells.Span[following.Cell].Position,
                grid.Cells.Span[edge.ToCell].Position, default, 0.05f, 0.1f, field.Input.Costs.Version) : null;
        }
        if (selected is not { } action)
        {
            if (traversal.Status == NavigationTraversalStatus.Waiting) { traversal = default; passage = default; }
            return;
        }
        if (Vector3.Distance(motion.Position, action.Entry) > action.EntryRange) return;
        guidance = new(default, motion.Heading, field.Input.Version, false);
        if (traversal.Status == NavigationTraversalStatus.Waiting && traversal.Connection == action.Connection &&
            traversal.ConnectionVersion == action.ContentVersion) return;
        passage = action.Links is { } links
            ? new(links.GetPassage(action.Connection), links.GetLink(action.Connection).Capacity, action.ContentVersion) : default;
        following.TraversalSequence++;
        traversal = new(following.TraversalSequence, action.Connection, action.Action, action.Entry, action.Exit, action.Parameters,
            passage.Passage is null ? NavigationTraversalStatus.Requested : NavigationTraversalStatus.Waiting,
            field.Input.Version, action.ContentVersion, 0, action.ExitRange);
    }
}
