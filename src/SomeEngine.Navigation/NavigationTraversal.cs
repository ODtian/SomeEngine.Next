using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Navigation;

public enum NavigationTraversalStatus : byte { None, Requested, Executing, Completed, Failed, Cancelled, Waiting }
public enum NavigationTraversalExecution : byte { None, Running, Completed, Failed }

/// <summary>Navigation's action handoff. The action consumer owns execution and actual movement.</summary>
public readonly record struct NavigationTraversalRequest(ulong RequestId, int Connection, int Action,
    Vector3 Entry, Vector3 Exit, Vector4 Parameters, NavigationTraversalStatus Status,
    ulong PathVersion, ulong ConnectionVersion, int ExitCorner, float ExitRange) : IComponent;

/// <summary>Written by the game's action consumer for the matching request.</summary>
public readonly record struct NavigationTraversalResult(ulong RequestId, NavigationTraversalExecution Status, float Progress = 0) : IComponent;

public sealed class NavigationTraversalSystem<TSpace, TState, TMotion, TReader>(World world)
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    private readonly QueryDefinition _actors = world.QueryDefinition().All<NavigationRequest<TSpace, TState>>()
        .Enabled<NavigationGoal>().Build();
    private JobHandle _completion;

    public JobHandle Schedule(JobHandle guidanceReady)
    {
        _completion = new NavigationTraversalJob<TSpace, TState, TMotion, TReader>().ScheduleParallel(world,
            new JobEntityScheduleOptions(filter: _actors), WorldJobs.CombineDependencies(world, [_completion, guidanceReady]));
        return _completion;
    }
}

internal static class NavigationTraversalGuidance
{
    internal static int CornerLimit(NavigationPath path, int traversalIndex) => traversalIndex < path.Traversals.Length
        ? path.Traversals.Span[traversalIndex].EntryCorner : path.Corners.Length - 1;
}

internal partial struct NavigationTraversalJob<TSpace, TState, TMotion, TReader> : IJobEntity
    where TSpace : class, INavigationSpace<TSpace>
    where TState : class
    where TMotion : unmanaged, IComponent
    where TReader : struct, INavigationMotionReader<TMotion>
{
    public void Execute([JobResources<NavigationPathResources>] in NavigationPathResult result,
        [JobResources(typeof(NavigationRequestResources<,>))] ref NavigationRequest<TSpace, TState> request,
        in TMotion movement, in NavigationTraversalResult execution,
        ref NavigationTraversalRequest traversal, ref NavigationGuidance guidance)
    {
        NavigationRequestData<TSpace, TState> data = request.Data;
        NavigationMotion motion = TReader.Read(in movement);
        if (traversal.Status is NavigationTraversalStatus.Requested or NavigationTraversalStatus.Executing)
        {
            guidance = new(default, motion.Heading, result.RequestVersion, false);
            if (execution.RequestId != traversal.RequestId) return;
            if (execution.Status == NavigationTraversalExecution.Completed &&
                Vector3.Distance(motion.Position, traversal.Exit) <= traversal.ExitRange)
            {
                data.CornerIndex = traversal.ExitCorner;
                data.TraversalIndex++;
                traversal = traversal with { Status = NavigationTraversalStatus.Completed };
            }
            else if (execution.Status == NavigationTraversalExecution.Failed)
                traversal = traversal with { Status = NavigationTraversalStatus.Failed };
            else if (execution.Status == NavigationTraversalExecution.Running)
                traversal = traversal with { Status = NavigationTraversalStatus.Executing };
            return;
        }
        if (traversal.PathVersion != result.RequestVersion) traversal = default;
        if (traversal.Status == NavigationTraversalStatus.Failed)
        {
            guidance = new(default, motion.Heading, result.RequestVersion, false);
            return;
        }
        if (result.Path is not { } path || result.Status == NavigationSearchStatus.Searching) return;
        if (data.TraversalIndex < path.Traversals.Length)
        {
            NavigationPathTraversal action = path.Traversals.Span[data.TraversalIndex];
            if (Vector3.Distance(motion.Position, action.Entry) > action.EntryRange) return;
            data.TraversalSequence++;
            traversal = new(data.TraversalSequence, action.Connection, action.Action, action.Entry, action.Exit,
                action.Parameters, NavigationTraversalStatus.Requested, result.RequestVersion, action.ContentVersion,
                action.ExitCorner, action.ExitRange);
            guidance = new(default, motion.Heading, result.RequestVersion, false);
            return;
        }
    }
}
